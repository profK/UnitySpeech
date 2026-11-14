# Worker Reference

The Worker is a Python HTTP server that provides text-to-speech synthesis using the Qwen3-TTS model. It runs as a separate process and communicates with the Unity client over HTTP.

## Quick Start

```bash
cd Worker
python3 -m venv .venv
source .venv/bin/activate
pip install -r requirements.txt
python main.py --device mps
```

## Command-Line Interface

### `main.py` — Server Entry Point

```bash
python main.py [OPTIONS]
```

| Argument | Default | Description |
|----------|---------|-------------|
| `--host HOST` | `127.0.0.1` | Listen address (loopback by default) |
| `--port PORT` | `8080` | Listen port |
| `--device DEVICE` | `auto` | Inference device: `auto`, `cpu`, `mps`, `cuda` |
| `--threads N` | `4` | CPU threads for inference (OMP threads) |
| `--model PATH` | *(auto-detect)* | Model path or HuggingFace repo ID. Omit for stub mode. |
| `--quantize MODE` | `none` | Quantization: `4bit`, `8bit`. Requires bitsandbytes. |
| `--queue N` | `8` | Maximum queued requests |
| `--voices DIR` | `voices` | Voice catalog directory |
| `--log-level LEVEL` | `info` | Logging level: `debug`, `info`, `warning`, `error` |

### Examples

```bash
# Stub mode (no model, synthetic audio only)
python main.py

# Apple Silicon GPU (recommended on Mac)
python main.py --device mps

# CPU with 8 threads
python main.py --device cpu --threads 8

# CUDA (NVIDIA GPU)
python main.py --device cuda

# 4-bit quantization on CPU (lower memory, slightly lower quality)
python main.py --quantize 4bit --device cpu

# Custom port and model path
python main.py --port 9000 --model ./models/Qwen--Qwen3-TTS-12Hz-0.6B-Base

# Remote access (use with authentication!)
python main.py --host 0.0.0.0 --port 8080
```

### `download_models.py` — Model Downloader

```bash
python download_models.py [OPTIONS]
```

| Argument | Description |
|----------|-------------|
| `--model base` | Download 0.6B Base (runtime synthesis, required) |
| `--model voicedesign` | Download 1.7B VoiceDesign (audition, optional) |
| `--list` | List available models and their local status |
| `--dir DIR` | Custom output directory |
| `--huggingface` | Use HuggingFace Hub (default) |
| `--modelscope` | Use ModelScope (recommended for China mainland) |

Models are saved to `Worker/models/` by default. Set `HF_TOKEN` in your environment if models require authentication.

## Shell Scripts

### `start_worker.sh`

Launch the worker as a background process:

```bash
./start_worker.sh                        # Default port 8080, auto-detect model
./start_worker.sh --port 9000            # Custom port
./start_worker.sh --model ./models/...   # Explicit model path
```

Creates `worker.pid` and `worker.log`. Kills any existing worker first.

### `stop_worker.sh`

Stop a running worker:

```bash
./stop_worker.sh
```

Gracefully stops the worker and cleans up the PID file.

## HTTP Endpoints

### `GET /v1/health`

Check worker status and capabilities.

**Response:**
```json
{
  "status": "ok",                    // "ok" or "degraded"
  "device": "mps",                   // "cpu", "mps", "cuda"
  "model": "Qwen/Qwen3-TTS-...",     // Model ID or "stub"
  "quantization": "none",            // "none", "4bit", "8bit"
  "cpu_threads": 4,                   // Active CPU thread count
  "streaming": true,                  // Always true
  "voice_selection": true,            // Voice cloning available
  "queue": {
    "active": 1,                      // 0 or 1
    "queued": 0,                      // Number waiting
    "max_queue": 8                    // Configured limit
  }
}
```

### `POST /v1/speech`

Submit a synthesis request with queuing and cancellation support.

**Request Body:**
```json
{
  "request_id": "unique-id",          // Client-generated unique ID
  "text": "Hello, world!",            // Text to synthesize
  "language": "en",                    // Language code
  "voice_id": "default",               // Voice to use
  "deadline": 1234567890.0            // Optional monotonic timestamp
}
```

**Success Response (200):**
```json
{
  "request_id": "unique-id",
  "status": "queued"                   // "queued" or "active"
}
```

**Error Response (429):**
```json
{
  "error": "queue_overload",
  "reason": "QueueOverload"
}
```

**Error Response (404):**
```json
{
  "error": "unknown_voice",
  "voice_id": "nonexistent"
}
```

### `DELETE /v1/speech/{request_id}`

Cancel a submitted request. Idempotent.

**Success Response (200):**
```json
{
  "request_id": "unique-id",
  "cancelled": true
}
```

**Error Response (404):**
```json
{
  "error": "request_not_found"
}
```

### `POST /v1/tts/stream`

Raw PCM streaming endpoint. Returns raw PCM16 bytes with no protocol framing. Used by the Unity runtime and probe tool.

**Request Body:**
```json
{
  "text": "Hello, world!",
  "language": "en",
  "voice_id": "default"
}
```

**Response:** Raw mono PCM16 @ 24 kHz bytes. Headers include `X-Sample-Rate`, `X-Channels`, `X-Bits`.

### `POST /v1/speech/stream`

Full protocol streaming endpoint. Returns length-prefixed binary records with START, PCM, END, and ERROR framing.

**Request Body:** Same as `/v1/tts/stream`.

**Response:** Binary stream of length-prefixed records. Header includes `X-Protocol-Version`.

### `GET /v1/voices`

List all available voice profiles.

**Response:**
```json
[
  {
    "voice_id": "dwarf",
    "model_id": "qwen3-tts-0.6b",
    "revision": 1,
    "language": "en",
    "ref_wav_path": "/path/to/dwarf.wav",
    "transcript": "Hello there!",
    "ref_hash": "abc123...",
    "transcript_hash": "def456...",
    "prompt_data": "...",
    "metadata": {}
  }
]
```

### `POST /v1/voices`

Create a voice profile from reference audio or pre-computed prompt data.

**Request Body (from audio):**
```json
{
  "voice_id": "dwarf",
  "language": "en",
  "transcript": "Hello there!",
  "ref_audio_path": "/path/to/dwarf.wav",
  "x_vector_only": false
}
```

**Request Body (from prompt):**
```json
{
  "voice_id": "dwarf",
  "language": "en",
  "prompt_data": "..."              // Pre-computed VoiceClonePromptItem as JSON
}
```

**Response (200):**
```json
{
  "voice_id": "dwarf",
  "status": "created"
}
```

### `POST /v1/voices/audition`

Audition a voice design using the 1.7B VoiceDesign model.

**Request Body:**
```json
{
  "voice_id": "grumpy-dwarf",
  "language": "en",
  "description": "A grumpy old dwarf with a gravelly voice",
  "sample_text": "Get out of my mine!"
}
```

**Response:** Placeholder (VoiceDesign integration pending).

## Queue Behavior

The worker processes requests in a strict FIFO order:

- **1 active** slot for synthesis
- **N queued** slots (default 8) for waiting requests
- Requests beyond capacity are rejected with 429

**Cancellation:**
- Queued requests are removed immediately
- Active requests during synthesis are marked "draining" and finish their current chunk
- Cancellation is idempotent

**Deadlines:**
- Requests with expired deadlines are rejected on submission
- Expired requests in the queue are skipped during promotion

## Stub Mode

When no model is specified or available, the worker runs in stub mode:

- Generates a 440 Hz sine wave matching the text length
- All endpoints work normally
- Protocol framing is correct
- Use for development without downloading models

## Models

| Model | Use Case | Size |
|-------|----------|------|
| Qwen3-TTS-12Hz-0.6B-Base | Runtime synthesis with voice cloning | ~1.7 GB |
| Qwen3-TTS-12Hz-1.7B-Base | Higher-quality synthesis | ~4 GB |
| Qwen3-TTS-12Hz-1.7B-VoiceDesign | Voice design from text descriptions | ~4 GB |
| Qwen3-TTS-12Hz-1.7B-CustomVoice | 9 premium timbres with style control | ~4 GB |

All models support: Chinese, English, Japanese, Korean, German, French, Russian, Portuguese, Spanish, Italian.

## Configuration via Environment

| Variable | Description |
|----------|-------------|
| `HF_TOKEN` | HuggingFace authentication token |
| `OMP_NUM_THREADS` | OMP threads (also set via `--threads`) |
