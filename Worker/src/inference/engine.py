"""Qwen3-TTS inference engine.

Manages a persistent model session on CPU or GPU (MPS on Apple Silicon).
Supports voice cloning via reference prompts and chunked PCM streaming.

Integration with qwen-tts library:
  - Qwen3TTSModel.from_pretrained() for model loading
  - create_voice_clone_prompt() for voice profile creation
  - generate_voice_clone() for runtime synthesis

The engine is thread-safe for cancellation but processes one request at a time.
Falls back to stub mode when the model is not available.
"""

from __future__ import annotations

import json
import logging
import os
import struct
import threading
import time
from dataclasses import dataclass, field
from typing import Any, Callable, Dict, Generator, List, Optional, Tuple

import numpy as np

from protocol import (
    SpeechTiming, TerminalReason, encode_pcm, encode_start,
    encode_end, encode_error, SAMPLE_RATE, SAMPLES_PER_FRAME,
)

log = logging.getLogger("inference")

# ── Model state ──────────────────────────────────────────────────────

@dataclass
class InferenceConfig:
    """Configuration for the inference engine."""
    model_path: str = ""              # Local path or HF model ID (e.g. "Qwen/Qwen3-TTS-12Hz-0.6B-Base")
    device: str = ""                   # "auto" | "cpu" | "mps" | "cuda"
    cpu_threads: int = 4               # OMP threads for CPU mode
    quantize: str = ""                 # "none" | "4bit" | "8bit"
    sample_rate: int = 24000
    samples_per_frame: int = 480       # 20 ms chunks at 24 kHz
    stream_threshold: int = 960        # Flush after N samples (40 ms)
    # Generation defaults (from model config, overridden at runtime)
    max_new_tokens: int = 2048
    temperature: float = 0.9
    top_k: int = 50
    top_p: float = 1.0
    repetition_penalty: float = 1.05


@dataclass
class _ModelState:
    """Per-model cached state. Created once on warmup."""
    loaded: bool = False
    device: str = ""
    quantization: str = ""
    threads: int = 0
    model_ref = None                   # Actual loaded model object (Qwen3TTSModel)
    model_sample_rate: int = 0         # Actual sample rate from the model


# ── Prompt serialization ─────────────────────────────────────────────
# VoiceClonePromptItem is not JSON-serializable. We serialize it to/from
# a compact dict format for storage in the voice catalog.

def _serialize_prompt(item) -> dict:
    """Serialize a VoiceClonePromptItem to a JSON-safe dict."""
    import torch
    from qwen_tts import VoiceClonePromptItem
    if not isinstance(item, VoiceClonePromptItem):
        raise TypeError(f"Expected VoiceClonePromptItem, got {type(item)}")
    # Convert tensors to CPU float32 for JSON serialization
    spk_emb = item.ref_spk_embedding
    if spk_emb.device.type != 'cpu':
        spk_emb = spk_emb.cpu()
    if spk_emb.dtype != torch.float32:
        spk_emb = spk_emb.float()  # bfloat16 -> float32 for JSON

    ref_code = None
    if item.ref_code is not None:
        rc = item.ref_code.cpu()
        if rc.dtype != torch.int64:
            rc = rc.long()  # Ensure int64 for JSON
        ref_code = rc.numpy().tolist()

    return {
        "ref_spk_embedding": spk_emb.numpy().tolist(),
        "ref_code": ref_code,
        "x_vector_only_mode": item.x_vector_only_mode,
        "icl_mode": item.icl_mode,
        "ref_text": item.ref_text,
    }


def _deserialize_prompt(data: dict):
    """Deserialize a dict back to a VoiceClonePromptItem."""
    from qwen_tts import VoiceClonePromptItem
    import torch
    ref_code = None
    if data.get("ref_code") is not None:
        ref_code = torch.tensor(data["ref_code"], dtype=torch.int64)
    # ref_spk_embedding stored as float32; model expects bfloat16 on device
    ref_spk = torch.tensor(data["ref_spk_embedding"], dtype=torch.float32)
    return VoiceClonePromptItem(
        ref_code=ref_code,
        ref_spk_embedding=ref_spk,
        x_vector_only_mode=data["x_vector_only_mode"],
        icl_mode=data["icl_mode"],
        ref_text=data.get("ref_text"),
    )


def _prompt_to_json(item) -> str:
    return json.dumps(_serialize_prompt(item))


def _prompt_from_json(text: str):
    return _deserialize_prompt(json.loads(text))


# ── Engine ───────────────────────────────────────────────────────────

class InferenceEngine:
    """Singleton inference engine. One active generation at a time.

    Loads the model once at startup. Caches derived voice prompts by
    (model, profile_revision). Streams PCM chunks via a generator.

    Falls back to stub mode when the model is not available (for testing
    without downloading models).
    """

    def __init__(self, config: Optional[InferenceConfig] = None):
        self.config = config or InferenceConfig()
        self._state = _ModelState()
        self._lock = threading.Lock()
        self._voice_cache: Dict[str, str] = {}  # voice_id -> prompt JSON
        self._cancelled_request: Optional[str] = None

    # ── Lifecycle ────────────────────────────────────────────────────

    @property
    def is_warm(self) -> bool:
        return self._state.loaded

    def load(self) -> Optional[TerminalReason]:
        """Load the model. Call once at startup.

        Returns None on success, or a TerminalReason on failure.
        In stub mode (no model path), returns immediately with no model loaded.
        """
        t0 = time.monotonic()

        # Stub mode: no model path, no torch needed
        if not self.config.model_path:
            log.warning("No model path set; running in STUB mode")
            self._state.loaded = True
            # Resolve device without importing torch
            if self.config.device == "auto":
                self._state.device = "cpu"  # Stub always uses CPU
            else:
                self._state.device = self.config.device
            self._state.quantization = self.config.quantize or "none"
            self._state.threads = self.config.cpu_threads
            elapsed = (time.monotonic() - t0) * 1000
            log.info("Stub mode active in %.0f ms", elapsed)
            return None

        log.info("Loading model from %s on device %s", self.config.model_path, self.config.device)

        # Resolve device
        device = self._resolve_device(self.config.device)
        if device is None:
            log.error("Cannot resolve device %s", self.config.device)
            return TerminalReason.InferenceError

        # Set CPU threads
        if device == "cpu":
            self._set_cpu_threads(self.config.cpu_threads)

        try:
            self._state.model_ref = self._load_model(
                self.config.model_path, device, self.config.quantize
            )
            if self._state.model_ref is None:
                log.error("Model load returned None")
                return TerminalReason.InferenceError
        except Exception:
            log.exception("Failed to load model")
            return TerminalReason.InferenceError

        # Capture the model's actual sample rate
        self._state.model_sample_rate = self._model_sample_rate()

        # Mark as loaded so warmup can use generate()
        self._state.device = device
        self._state.quantization = self.config.quantize or "none"
        self._state.threads = self.config.cpu_threads
        self._state.loaded = True

        # Warmup: generate a short probe to cache device paths
        log.info("Warming up model...")
        self._run_warmup()

        elapsed = (time.monotonic() - t0) * 1000
        log.info("Model loaded and warm in %.0f ms (model_sr=%d, worker_sr=%d)",
                 elapsed, self._state.model_sample_rate, self.config.sample_rate)
        return None

    def unload(self) -> None:
        """Unload the model. For cleanup on shutdown."""
        self._state.model_ref = None
        self._state.loaded = False

    # ── Voice cache ─────────────────────────────────────────────────

    def cache_voice_prompt(self, voice_id: str, prompt_json: str) -> None:
        """Cache a voice prompt (serialized VoiceClonePromptItem as JSON)."""
        self._voice_cache[voice_id] = prompt_json

    def get_voice_prompt(self, voice_id: str) -> Optional[str]:
        return self._voice_cache.get(voice_id)

    def create_voice_prompt(self, ref_audio: str, ref_text: str,
                           x_vector_only: bool = False) -> Optional[str]:
        """Create a voice prompt from reference audio. Returns serialized JSON.

        Args:
            ref_audio: Path to reference WAV file or URL
            ref_text: Transcript of the reference audio
            x_vector_only: If True, use only speaker embedding (faster, less control)
                          If False, use full ICL mode (requires ref_text, better quality)
        """
        if not self._state.loaded or self._state.model_ref is None:
            log.error("Cannot create voice prompt: model not loaded")
            return None

        try:
            items = self._state.model_ref.create_voice_clone_prompt(
                ref_audio=ref_audio,
                ref_text=ref_text if not x_vector_only else None,
                x_vector_only_mode=x_vector_only,
            )
            if not items:
                log.error("create_voice_clone_prompt returned empty list")
                return None
            return _prompt_to_json(items[0])
        except Exception:
            log.exception("Failed to create voice prompt from %s", ref_audio)
            return None

    # ── Synthesis ────────────────────────────────────────────────────

    def generate(
        self,
        request_id: str,
        text: str,
        language: str,
        voice_prompt: str,  # JSON-serialized prompt (empty = use defaults)
        cancel_check: Optional[Callable[[], bool]] = None,
    ) -> Generator[bytes, None, SpeechTiming]:
        """Generate speech and yield encoded binary records.

        In stub mode: generates a synthetic tone.
        In real mode: uses Qwen3-TTS model for synthesis.

        Yields: start record, PCM records, end record.
        Raises on internal errors (server wraps to error record).
        """
        if not self._state.loaded:
            raise RuntimeError("Model not loaded")

        with self._lock:
            self._cancelled_request = None

        t_wall = time.monotonic()
        t_inference = 0.0

        # Determine which mode we're in
        if self._state.model_ref is None:
            # STUB MODE
            return self._generate_stub(request_id, text, language, voice_prompt,
                                       cancel_check, t_wall, t_inference)

        # REAL MODE: use the Qwen3-TTS model
        return self._generate_real(request_id, text, language, voice_prompt,
                                   cancel_check, t_wall, t_inference)

    # ── Real generation ──────────────────────────────────────────────

    def _generate_real(
        self, request_id: str, text: str, language: str, voice_prompt: str,
        cancel_check, t_wall, t_inference,
    ) -> Generator[bytes, None, SpeechTiming]:
        """Real generation using Qwen3-TTS model."""
        import torch

        # Build voice clone prompt from cached prompt
        # The model expects List[VoiceClonePromptItem], not a dict.
        # We store as JSON for persistence, deserialize back to objects.
        voice_clone_items = None
        if voice_prompt:
            try:
                item = _prompt_from_json(voice_prompt)
                voice_clone_items = [item]
            except Exception:
                log.warning("Invalid voice prompt for %s; using defaults", request_id)

        # Language normalization: "en" → "English", "zh" → "Chinese", etc.
        lang_map = {
            "en": "English", "zh": "Chinese", "ja": "Japanese", "ko": "Korean",
            "de": "German", "fr": "French", "ru": "Russian", "pt": "Portuguese",
            "es": "Spanish", "it": "Italian", "auto": "Auto",
        }
        lang_name = lang_map.get(language.lower(), language) if language else "Auto"

        # Estimate duration: ~150 ms per 100 characters (varies by language)
        est_duration = len(text) / 100.0 * 0.15

        # Yield start record
        yield encode_start(voice_prompt or "default", language, est_duration)

        try:
            t_gen = time.monotonic()

            # Build kwargs for generation
            gen_kwargs = {
                "text": text,
                "language": lang_name,
                "non_streaming_mode": True,  # Full generation, we chunk for streaming
                "max_new_tokens": self.config.max_new_tokens,
                "do_sample": True,
                "top_k": self.config.top_k,
                "top_p": self.config.top_p,
                "temperature": self.config.temperature,
                "repetition_penalty": self.config.repetition_penalty,
            }

            # If we have a cached voice prompt, use it; otherwise fall back to
            # a synthetic reference audio (warmup / no-voice mode)
            if voice_clone_items is not None:
                # Pass VoiceClonePromptItem objects directly (not as dict)
                gen_kwargs["voice_clone_prompt"] = voice_clone_items
            else:
                # No cached voice prompt: use a synthetic reference audio.
                # This is acceptable for warmup and fallback.
                ref_sr = self._state.model_sample_rate
                ref_audio = np.random.randn(ref_sr * 3).astype(np.float32) * 0.01
                gen_kwargs["ref_audio"] = (ref_audio, ref_sr)
                gen_kwargs["ref_text"] = "Hello there."

            # Generate audio (full utterance)
            wavs, model_sr = self._state.model_ref.generate_voice_clone(**gen_kwargs)

            if cancel_check and cancel_check():
                log.info("Cancellation after generation for %s; discarding", request_id)
                yield encode_error(TerminalReason.Timeout, "Cancelled after generation")
                t_inference = (time.monotonic() - t_gen) * 1000
                timing = SpeechTiming(
                    wall_ms=(time.monotonic() - t_wall) * 1000,
                    inference_ms=t_inference,
                )
                yield encode_end(0, timing)
                return

            # Extract and convert audio
            audio = wavs[0]  # First (and only) output
            if audio.ndim > 1:
                audio = audio[0]  # Take first channel if stereo

            # Resample to our target sample rate if needed
            if model_sr != self.config.sample_rate:
                try:
                    import librosa
                    audio = librosa.resample(
                        y=audio.astype(np.float32),
                        orig_sr=model_sr,
                        target_sr=self.config.sample_rate,
                    )
                except ImportError:
                    log.warning("librosa not available; skipping resampling %d→%d",
                              model_sr, self.config.sample_rate)

            t_inference = (time.monotonic() - t_gen) * 1000

            # Detect leading silence
            silence_head = self._detect_leading_silence_ms(audio)

            # Stream as PCM records
            sample_offset = 0
            pcm_bytes = self._to_pcm16(audio)

            for i in range(0, len(pcm_bytes) // 2, self.config.samples_per_frame):
                if cancel_check and cancel_check():
                    log.info("Cancellation during streaming for %s; discarding remainder", request_id)
                    break

                chunk = pcm_bytes[i * 2:(i + self.config.samples_per_frame) * 2]
                yield encode_pcm(sample_offset, chunk)
                sample_offset += self.config.samples_per_frame

            # Yield end record
            t_total = (time.monotonic() - t_wall) * 1000
            total_samples = sample_offset
            timing = SpeechTiming(
                wall_ms=t_total,
                inference_ms=t_inference,
                silence_head_ms=silence_head,
            )
            yield encode_end(total_samples, timing)

        except Exception:
            log.exception("Generation error for %s", request_id)
            raise

    # ── Stub generation ──────────────────────────────────────────────

    def _generate_stub(
        self, request_id: str, text: str, language: str, voice_prompt: str,
        cancel_check, t_wall, t_inference,
    ) -> Generator[bytes, None, SpeechTiming]:
        """Stub generation: synthetic tone for testing without a real model."""
        # Estimate duration
        duration = max(len(text) / 100.0 * 0.15, 0.2)  # seconds
        samples = int(self.config.sample_rate * duration)

        # 440 Hz tone at moderate amplitude
        t = np.arange(samples, dtype=np.float32) / self.config.sample_rate
        audio = np.sin(2 * np.pi * 440.0 * t) * 0.3

        # Yield start record
        yield encode_start(voice_prompt or "stub", language, duration)

        # Stream as PCM records
        sample_offset = 0
        pcm_bytes = self._to_pcm16(audio)
        silence_head = 0.0  # No leading silence in stub

        for i in range(0, len(pcm_bytes) // 2, self.config.samples_per_frame):
            if cancel_check and cancel_check():
                log.info("Cancellation during streaming for %s; discarding remainder", request_id)
                break

            chunk = pcm_bytes[i * 2:(i + self.config.samples_per_frame) * 2]
            yield encode_pcm(sample_offset, chunk)
            sample_offset += self.config.samples_per_frame

        # Yield end record
        t_total = (time.monotonic() - t_wall) * 1000
        timing = SpeechTiming(
            wall_ms=t_total,
            inference_ms=t_total,  # In stub, wall ≈ inference
            silence_head_ms=silence_head,
        )
        yield encode_end(sample_offset, timing)

    # ── Internal: model loading ──────────────────────────────────────

    def _resolve_device(self, device: str) -> Optional[str]:
        """Resolve device string to a PyTorch device."""
        import torch
        if device == "auto":
            if hasattr(torch.backends, "mps") and torch.backends.mps.is_available():
                return "mps"
            if torch.cuda.is_available():
                return "cuda"
            return "cpu"
        return device if device in ("cpu", "mps", "cuda") else None

    def _set_cpu_threads(self, n: int) -> None:
        """Set OMP and PyTorch threads for CPU inference."""
        os.environ.setdefault("OMP_NUM_THREADS", str(n))
        try:
            import torch
            torch.set_num_threads(n)
        except Exception:
            pass
        log.info("CPU threads set to %d", n)

    def _load_model(self, model_path: str, device: str, quantize: str):
        """Load Qwen3-TTS model from local path or HuggingFace Hub.

        Returns the loaded Qwen3TTSModel instance.
        """
        from qwen_tts import Qwen3TTSModel
        import torch

        dtype = torch.bfloat16  # Default for Qwen3-TTS
        if quantize == "4bit":
            # 4-bit quantization via bitsandbytes (if available)
            try:
                from transformers import BitsAndBytesConfig
                dtype = BitsAndBytesConfig.load_in_4bit()
                log.info("Using 4-bit quantization")
            except ImportError:
                log.warning("bitsandbytes not available; using bf16")
                dtype = torch.bfloat16
        elif quantize == "8bit":
            try:
                from transformers import BitsAndBytesConfig
                dtype = BitsAndBytesConfig.load_in_8bit()
                log.info("Using 8-bit quantization")
            except ImportError:
                log.warning("bitsandbytes not available; using bf16")
                dtype = torch.bfloat16

        # MPS (Apple Silicon) doesn't support flash_attention_2; use eager
        attn_impl = None
        if device == "cuda":
            try:
                import flash_attn  # noqa: F401
                attn_impl = "flash_attention_2"
            except ImportError:
                log.info("flash_attn not available; using default attention")

        kwargs = {
            "pretrained_model_name_or_path": model_path,
            "device_map": device,
            "dtype": dtype,
        }
        if attn_impl:
            kwargs["attn_implementation"] = attn_impl

        log.info("Loading Qwen3-TTS: path=%s device=%s dtype=%s",
                model_path, device, dtype)

        model = Qwen3TTSModel.from_pretrained(**kwargs)
        log.info("Model loaded on device %s (sample_rate=%d)",
                model.device, self._model_sample_rate())
        return model

    def _model_sample_rate(self) -> int:
        """Get the model's native sample rate."""
        if self._state.model_ref is None:
            return self.config.sample_rate  # Stub uses our config
        try:
            # Qwen3-TTS models use 24kHz sample rate
            return self._state.model_ref.model.speech_tokenizer.sampling_rate
        except AttributeError:
            # Fallback: check processor
            try:
                return self._state.model_ref.model.speaker_encoder_sample_rate
            except AttributeError:
                return 24000

    def _run_warmup(self) -> None:
        """Generate a short utterance to warm caches."""
        try:
            records = list(self.generate(
                request_id="warmup",
                text="hello",
                language="en",
                voice_prompt="",
                cancel_check=lambda: False,
            ))
            log.info("Warmup complete: %d records", len(records))
        except Exception:
            log.exception("Warmup failed; continuing anyway")

    # ── Audio utilities ──────────────────────────────────────────────

    def _to_pcm16(self, samples: np.ndarray) -> bytes:
        """Convert float32 samples [-1, 1] to little-endian PCM16 bytes."""
        clipped = np.clip(samples, -1.0, 1.0)
        int16 = (clipped * 32767).astype(np.int16)
        return int16.tobytes()  # Native order is LE on x86/ARM

    def _detect_leading_silence_ms(self, audio: np.ndarray) -> float:
        """Detect leading silence in ms. Threshold: -45 dBFS RMS over 10 ms windows."""
        if audio.size == 0:
            return 0.0

        sr = self.config.sample_rate
        window = int(0.010 * sr)  # 10 ms
        threshold = (32767.0 * 10 ** (-45.0 / 20.0)) ** 2  # Squared threshold

        # Check first window for immediate onset
        peak = np.max(np.abs(audio[:window])) if audio.size >= window else np.max(np.abs(audio))
        if peak > 32767.0 * 10 ** (-45.0 / 20.0):
            return 0.0

        # Binary search for onset
        lo, hi = 0, min(audio.size, len(audio))
        while lo < hi:
            mid = (lo + hi) // 2
            seg = audio[mid:mid + window]
            rms_sq = np.mean(seg ** 2) * (32767.0 ** 2)
            if rms_sq >= threshold:
                hi = mid
            else:
                lo = mid + 1
        return lo / sr * 1000.0
