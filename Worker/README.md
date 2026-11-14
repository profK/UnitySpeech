# Qwen3-TTS Worker

Persistent text-to-speech synthesis server for Unity NPC dialogue.
Supports local CPU or GPU (Metal/MPS) inference with streaming PCM output.

Built on [Qwen3-TTS](https://github.com/QwenLM/Qwen3-TTS) — a family of
multilingual, controllable, streaming text-to-speech models.

## Quick start

```bash
# 1. Create a virtual environment
cd Worker
python3 -m venv .venv
source .venv/bin/activate

# 2. Install dependencies
pip install -r requirements.txt

# 3. Start the worker (stub mode, no model needed)
python main.py --device cpu    # CPU mode

# 4. Check health
curl http://127.0.0.1:8080/v1/health

# 5. Smoke test with the probe
python probe_tts.py http://127.0.0.1:8080/v1/tts/stream
```

## With real models

```bash
# Download the 0.6B Base model (runtime synthesis, ~1.7 GB)
python download_models.py --model base

# Download the 1.7B VoiceDesign model (voice audition, optional)
python download_models.py --model voicedesign

# Start with the downloaded model
python main.py --model ./models/Qwen--Qwen3-TTS-12Hz-0.6B-Base --device mps

# Or let the library auto-download from HuggingFace
python main.py --model Qwen/Qwen3-TTS-12Hz-0.6B-Base --device mps
```

## Performance (Apple Silicon, MPS)

| Metric | Value |
|--------|-------|
| Model load time | ~25 s |
| Warmup (first synthesis) | ~3 s |
| RTF (avg) | **0.58** (faster than real-time) |
| Cloned voice latency | ~1.8 s |
| Default voice latency | ~2.5 s |

*Tested with Qwen3-TTS 0.6B Base on M-series Mac, MPS device.*

## Architecture

```
Unity Client  ─────HTTP─────>  Worker Process
                          │
                     ┌─────┴──────┐
                     │  Queue mgr  │  1 active, N queued
                     │  Inference  │  Qwen3-TTS 0.6B / 1.7B
                     │  Voice cache│  Model-specific prompts
                     └─────────────┘
```

## Endpoints

| Method | Path | Purpose |
|--------|------|---------|
| GET | `/v1/health` | Status, device, model, queue depth |
| POST | `/v1/speech` | Synthesis with queuing + cancel |
| DELETE | `/v1/speech/{id}` | Idempotent cancellation |
| POST | `/v1/tts/stream` | Raw PCM stream (for probe/smoke tests) |
| POST | `/v1/speech/stream` | Full protocol stream (for Unity runtime) |
| GET | `/v1/voices` | List voice profiles |
| POST | `/v1/voices` | Create voice from audio or pre-computed prompt |

## Binary protocol

HTTP responses from speech endpoints use a length-prefixed record format:

```
┌───────────┬──────────┬──────────┬──────────────────────────┐
│  Type (1) │  Length  │ Payload  │  Padding (to 8-byte)     │
│  (uint8)  │ (uint32) │  (bytes) │                         │
└───────────┴──────────┴──────────┴──────────────────────────┘
```

All integers are little-endian. Validate record sizes before allocation.
Audio is mono PCM16 at 24 kHz.

| Record | Type | Payload |
|--------|------|---------|
| START  | 0x01 | version, voice_id, language, duration_s, sample_rate |
| PCM    | 0x02 | sample_offset (uint64), pcm_bytes (int16 LE) |
| END    | 0x03 | total_samples, wall_ms, inference_ms, silence_head_ms |
| ERROR  | 0x04 | code (int32), message (UTF-8) |

## Voice cloning workflow

1. **Record** a ~3 second reference clip of the target voice
2. **Create** a voice profile: `POST /v1/voices` with `ref_audio_path` + `transcript`
3. **Use** the voice: any synthesis endpoint with `voice_id: "dwarf"`

The worker creates a `VoiceClonePromptItem` from the reference audio and
caches it. Subsequent syntheses use the cached prompt directly (no re-encoding).

## Models

| Model | Use case | Size |
|-------|----------|------|
| Qwen3-TTS-12Hz-0.6B-Base | Runtime synthesis with voice cloning | ~1.7 GB |
| Qwen3-TTS-12Hz-1.7B-Base | Higher-quality synthesis (audition if 0.6B lacks character) | ~4 GB |
| Qwen3-TTS-12Hz-1.7B-VoiceDesign | Voice design from text descriptions | ~4 GB |
| Qwen3-TTS-12Hz-1.7B-CustomVoice | 9 premium timbres with style control | ~4 GB |

All models support: Chinese, English, Japanese, Korean, German, French,
Russian, Portuguese, Spanish, Italian.

## Deployment

- **Local**: Worker on loopback, same machine as Unity. Loopback-only binding.
- **Remote**: Authenticated transport, authoritative game-server relay.
- **Headless**: Linux server with CPU inference for multiplayer authority.

## Tests

```bash
cd Worker
source .venv/bin/activate
python -m pytest tests/ -v     # 49 tests: protocol, queue, voice catalog
```

## CLI reference

```bash
python main.py                              # Stub mode, port 8080
python main.py --device mps                 # Apple Silicon GPU mode
python main.py --device cpu --threads 8     # CPU with 8 threads
python main.py --model ./models/Qwen--Qwen3-TTS-12Hz-0.6B-Base
python main.py --quantize 4bit --model ./models/... --device cpu
python main.py --queue 16                    # Larger queue
python main.py --port 9000 --host 0.0.0.0   # Remote access (use with auth!)
```

## License

Qwen3-TTS models are released under Apache 2.0.
