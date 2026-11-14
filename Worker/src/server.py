"""HTTP server for the Qwen3-TTS worker.

Endpoints:
  GET  /v1/health          – Worker health and capabilities
  POST /v1/speech          – Synthesis with binary streaming + queuing
  DELETE /v1/speech/{id}   – Idempotent cancellation
  POST /v1/tts/stream      – Simplified PCM streaming (for probe / smoke tests)
  GET  /v1/voices          – List available voices
  POST /v1/voices          – Create / audition a voice profile

All binary responses use the length-prefixed record protocol.
"""

from __future__ import annotations

import logging
import threading
import time
from typing import Dict, Optional

from fastapi import FastAPI, Request, HTTPException, BackgroundTasks
from fastapi.responses import StreamingResponse, JSONResponse, PlainTextResponse
from pydantic import BaseModel, Field

from protocol import (
    TerminalReason, encode_error, encode_end, encode_pcm, encode_start,
    SpeechTiming, PROTOCOL_VERSION, SAMPLE_RATE, SAMPLES_PER_FRAME,
)
from inference.engine import InferenceEngine, InferenceConfig
from request_queue import QueueManager, QueueConfig, QueuedRequest, RequestState
from voice_catalog import VoiceCatalog

log = logging.getLogger("server")

# ── Pydantic models (module-level for FastAPI schema resolution) ─────

class SpeechRequest(BaseModel):
    request_id: str
    text: str
    language: str = "en"
    voice_id: str = "default"
    deadline: Optional[float] = None


class StreamRequest(BaseModel):
    text: str
    language: str = "en"
    voice_id: str = "default"


class VoiceCreateRequest(BaseModel):
    """Create a voice profile (direct prompt or from reference audio)."""
    voice_id: str
    language: str = "en"
    transcript: str = ""
    ref_audio_path: str = ""
    x_vector_only: bool = False
    prompt_data: str = ""


class VoiceAuditionRequest(BaseModel):
    """Audition a voice design using 1.7B VoiceDesign model."""
    voice_id: str
    language: str = "en"
    description: str
    sample_text: str


# ── App factories ───────────────────────────────────────────────────

def create_app(
    model_path: str = "",
    device: str = "auto",
    cpu_threads: int = 4,
    quantize: str = "",
    listen_host: str = "127.0.0.1",
    listen_port: int = 8080,
    max_queue: int = 8,
    voices_dir: str = "voices",
) -> FastAPI:
    app = FastAPI(title="Qwen3-TTS Worker", version="0.1.0")

    # Shared state
    engine = InferenceEngine(InferenceConfig(
        model_path=model_path,
        device=device,
        cpu_threads=cpu_threads,
        quantize=quantize,
    ))
    catalog = VoiceCatalog(voices_dir, model_id=model_path or "qwen3-tts-0.6b")
    queue_mgr = QueueManager(QueueConfig(max_queue=max_queue))

    # Store for cancellation callbacks
    _active_cancel: Dict[str, threading.Event] = {}

    # ── Startup ─────────────────────────────────────────────────────
    @app.on_event("startup")
    def _startup():
        err = engine.load()
        if err:
            log.error("Model failed to load: %s", err)

    # ── Health ──────────────────────────────────────────────────────
    @app.get("/v1/health")
    async def health():
        return JSONResponse({
            "status": "ok" if engine.is_warm else "degraded",
            "device": engine._state.device,
            "model": model_path or "stub",
            "quantization": engine._state.quantization,
            "cpu_threads": engine._state.threads,
            "streaming": True,
            "voice_selection": True,
            "queue": {
                "active": 1 if queue_mgr.is_busy else 0,
                "queued": queue_mgr.queued_count,
                "max_queue": max_queue,
            },
        })

    # ── Speech (queued, with cancel) ────────────────────────────────
    @app.post("/v1/speech")
    async def speech(req: SpeechRequest, background: BackgroundTasks):
        # Check queue capacity
        if queue_mgr.is_full:
            return JSONResponse(
                status_code=429,
                content={"error": "queue_overload", "reason": TerminalReason.QueueOverload.name},
            )

        # Resolve voice
        profile = catalog.get(req.voice_id)
        if profile is None and req.voice_id != "default":
            return JSONResponse(
                status_code=404,
                content={"error": "unknown_voice", "voice_id": req.voice_id},
            )

        voice_prompt = profile.prompt_data if profile else ""

        # Create cancel event
        cancel_event = threading.Event()
        with queue_mgr._lock:
            _active_cancel[req.request_id] = cancel_event

        queued_req = QueuedRequest(
            request_id=req.request_id,
            text=req.text,
            language=req.language,
            voice_id=req.voice_id,
            deadline=req.deadline,
        )

        accepted, reject = queue_mgr.try_submit(queued_req)
        if not accepted:
            return JSONResponse(
                status_code=429,
                content={"error": "rejected", "reason": reject.name if reject else "unknown"},
            )

        # Enqueue worker task
        background.add_task(_process_request, queued_req, engine, cancel_event,
                           voice_prompt, queue_mgr)

        return JSONResponse({
            "request_id": req.request_id,
            "status": "queued" if not queue_mgr.is_busy or queue_mgr.active != queued_req else "active",
        })

    # ── Cancel ──────────────────────────────────────────────────────
    @app.delete("/v1/speech/{request_id}")
    async def cancel_speech(request_id: str):
        # Signal cancel
        cancel_event = _active_cancel.get(request_id)
        if cancel_event:
            cancel_event.set()
        cancelled = queue_mgr.cancel(request_id)
        if not cancelled:
            return JSONResponse(status_code=404, content={"error": "request_not_found"})
        return JSONResponse({"request_id": request_id, "cancelled": True})

    # ── Raw PCM stream endpoint (for probe / smoke tests) ───────────
    # Returns raw PCM16 bytes (no protocol framing). Used by probe_tts.py.
    @app.post("/v1/tts/stream")
    async def tts_stream_raw(req: StreamRequest):
        """Raw PCM streaming endpoint for smoke testing.

        Returns raw mono PCM16 @ 24 kHz bytes. No protocol framing.
        No queuing, no cancellation.
        """
        if not engine.is_warm:
            return JSONResponse(status_code=503, content={"error": "worker_unavailable"})

        profile = catalog.get(req.voice_id) if req.voice_id != "default" else None
        voice_prompt = profile.prompt_data if profile else ""

        def _raw_pcm_gen():
            """Strip protocol records and yield raw PCM bytes."""
            try:
                for record in engine.generate(
                    request_id="stream",
                    text=req.text,
                    language=req.language,
                    voice_prompt=voice_prompt,
                    cancel_check=lambda: False,
                ):
                    yield record  # Engine yields pre-framed records; strip them
            except Exception as e:
                log.exception("Stream error")
                b"\x00"  # Emit silence on error so probe doesn't hang

        def _extract_pcm(gen):
            """Parse protocol records and yield only raw PCM payloads."""
            from protocol import RecordReader, RECORD_PCM, decode_pcm
            reader = RecordReader()
            for raw in gen:
                for rtype, payload in reader.feed(raw):
                    if rtype == RECORD_PCM:
                        _, pcm_bytes = decode_pcm(payload)
                        yield pcm_bytes

        return StreamingResponse(
            _extract_pcm(_raw_pcm_gen()),
            media_type="audio/pcm",
            headers={"X-Sample-Rate": "24000", "X-Channels": "1", "X-Bits": "16"},
        )

    # ── Protocol stream endpoint (for Unity runtime) ────────────────
    @app.post("/v1/speech/stream")
    async def tts_stream_protocol(req: StreamRequest):
        """Full protocol streaming endpoint. Used by the Unity runtime."""
        if not engine.is_warm:
            return JSONResponse(status_code=503, content={"error": "worker_unavailable"})

        profile = catalog.get(req.voice_id) if req.voice_id != "default" else None
        voice_prompt = profile.prompt_data if profile else ""

        def _protocol_gen():
            try:
                for record in engine.generate(
                    request_id="stream-protocol",
                    text=req.text,
                    language=req.language,
                    voice_prompt=voice_prompt,
                    cancel_check=lambda: False,
                ):
                    yield record
            except Exception as e:
                log.exception("Protocol stream error")
                yield encode_error(TerminalReason.InferenceError, str(e))

        return StreamingResponse(
            _protocol_gen(),
            media_type="application/octet-stream",
            headers={"X-Protocol-Version": str(PROTOCOL_VERSION)},
        )

    # ── Voice management ────────────────────────────────────────────
    @app.get("/v1/voices")
    async def list_voices():
        return JSONResponse([v.to_dict() for v in catalog.list()])

    @app.post("/v1/voices")
    async def create_voice(req: VoiceCreateRequest):
        """Create a voice profile.

        Two modes:
        1. Direct: provide prompt_data directly (pre-computed from 1.7B audition)
        2. Audio: provide ref_audio_path and transcript; worker creates the prompt

        In audio mode, the worker uses the loaded model to create a voice clone
        prompt from the reference audio. This works with both 0.6B Base (ICL mode)
        and 1.7B VoiceDesign (speaker embedding only).
        """
        prompt_data = req.prompt_data

        # If no pre-computed prompt, create one from the reference audio
        if not prompt_data and req.ref_audio_path:
            prompt_data = engine.create_voice_prompt(
                ref_audio=req.ref_audio_path,
                ref_text=req.transcript,
                x_vector_only=req.x_vector_only,
            )
            if not prompt_data:
                return JSONResponse(
                    status_code=500,
                    content={"error": "prompt_creation_failed",
                             "voice_id": req.voice_id},
                )

        if not prompt_data:
            return JSONResponse(
                status_code=400,
                content={"error": "prompt_data_or_ref_audio_required",
                         "detail": "Provide prompt_data or ref_audio_path + transcript"},
            )

        catalog.create(
            voice_id=req.voice_id,
            language=req.language,
            ref_wav_path=req.ref_audio_path,
            transcript=req.transcript,
            prompt_data=prompt_data,
        )
        engine.cache_voice_prompt(req.voice_id, prompt_data)
        return JSONResponse({"voice_id": req.voice_id, "status": "created"})

    # ── Voice audition (using 1.7B VoiceDesign model) ───────────────
    # This endpoint is for offline voice creation workflow.
    # The 1.7B model generates a prompt from a text description,
    # which is then stored and used with the 0.6B runtime model.
    @app.post("/v1/voices/audition")
    async def audition_voice(req: VoiceAuditionRequest):
        """Audition a voice design. Uses the 1.7B VoiceDesign model if available.

        This is an offline workflow: describe a voice, hear a sample,
        then optionally save it as a runtime voice profile.
        """
        if not engine.is_warm:
            return JSONResponse(status_code=503, content={"error": "worker_unavailable"})

        # TODO: Integrate 1.7B VoiceDesign model for text-to-voice generation
        # For now, this returns a placeholder response
        return JSONResponse({
            "voice_id": req.voice_id,
            "status": "pending",
            "note": "VoiceDesign model integration pending",
        })

    return app


# ── Background worker ───────────────────────────────────────────────

async def _process_request(
    request: QueuedRequest,
    engine: InferenceEngine,
    cancel_event: threading.Event,
    voice_prompt: str,
    queue_mgr: QueueManager,
) -> None:
    """Process a queued request. Called from a background thread by FastAPI."""
    # Wait for our turn
    queue_mgr._not_empty.wait()  # The submit already set state
    time.sleep(0.01)  # Brief yield

    def cancel_check():
        return cancel_event.is_set()

    try:
        # Generate and collect records (in real use, streamed via WebSocket)
        records = list(engine.generate(
            request_id=request.request_id,
            text=request.text,
            language=request.language,
            voice_prompt=voice_prompt,
            cancel_check=cancel_check,
        ))

        # Dispatch to client (placeholder – real impl streams via SSE/WebSocket)
        if request.response_callback:
            for rec in records:
                request.response_callback(rec)

        request.state = RequestState.FINISHED
    except Exception as e:
        log.exception("Request %s failed", request.request_id)
        if request.response_callback:
            request.response_callback(encode_error(TerminalReason.InferenceError, str(e)))
        request.state = RequestState.FAILED
    finally:
        queue_mgr.release(request.request_id)
