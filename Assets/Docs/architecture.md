# Project Architecture

This document describes the high-level architecture of the Unity NPC TTS system, including component responsibilities, data flow, and deployment modes.

## System Overview

```
┌─────────────────────────────────────────────────────────────────────┐
│                           Unity 6 Client                            │
│                                                                     │
│  ┌──────────────────┐   ┌──────────────────┐   ┌────────────────┐  │
│  │   NpcTtsManager  │──▶│  WorkerClient    │──▶│  StreamParser  │  │
│  │  (MonoBehaviour) │   │  (HTTP client)   │   │  (Protocol)    │  │
│  └────────┬─────────┘   └──────────────────┘   └────────┬───────┘  │
│           │                                              │           │
│           │                    ┌──────────────────┐      │           │
│           │                    │   PcmBuffer      │◀─────┘           │
│           │                    │  (SPSC circular) │                  │
│           │                    └────────┬─────────┘                  │
│           │                             │                            │
│           │                    ┌────────▼─────────┐                  │
│           │                    │ UtterancePlayer  │                  │
│           │                    │ (Spatial audio)  │                  │
│           │                    └────────┬─────────┘                  │
│           │                             │                            │
│           │                    ┌────────▼──────────────┐             │
│           │                    │   AudioSource         │             │
│           │                    │   (Unity engine)      │             │
│           └────────────────────┼───────────────────────┘             │
│                                │                                     │
│                    ┌───────────▼──────────────────┐                   │
│                    │     TelemetryCollector        │                   │
│                    │  (Metrics & benchmarking)     │                   │
│                    └───────────────────────────────┘                   │
└───────────────────────────────────────────────────────────────────────┘
          │
          │  HTTP / Binary protocol (length-prefixed records)
          │
┌─────────▼───────────────────────────────────────────────────────────┐
│                         Worker Process (Python)                      │
│                                                                      │
│  ┌──────────────┐   ┌───────────────┐   ┌───────────────────────┐  │
│  │  FastAPI     │──▶│  QueueManager  │──▶│  InferenceEngine     │  │
│  │  (HTTP server│   │  (1 active,    │   │  (Qwen3-TTS model)   │  │
│  │   endpoints) │   │   N queued)    │   │                       │  │
│  └──────────────┘   └───────────────┘   └───────────┬───────────┘  │
│          │                                          │              │
│          │                ┌─────────────────────────┤              │
│          │                │  VoiceCatalog           │              │
│          │                │  (Voice profiles,       │              │
│          │                │   cached prompts)       │              │
│          │                └────────────────────────────────────────│
│          │                                                          │
│          └── Health, Voices, Cancel endpoints                      │
└──────────────────────────────────────────────────────────────────────┘
```

## Components

### Unity Runtime (`com.npctts.unity`)

The UPM package that lives inside the Unity project. It handles:

| Module | File | Responsibility |
|--------|------|----------------|
| **Core** | `Core.cs` | `SpeechRequest`, `SpeechHandle`, `SpeechStatus`, `TerminalReason` |
| **Manager** | `NpcTtsManager.cs` | Main MonoBehaviour, orchestrates synthesis and playback |
| **Networking** | `WorkerClient.cs` | HTTP client for all worker endpoints |
| **Protocol** | `StreamParser.cs` | Binary protocol decoder with boundary tolerance |
| **Audio Buffer** | `PcmBuffer.cs` | Lock-free SPSC circular buffer for PCM samples |
| **Audio Player** | `UtterancePlayer.cs` | Streaming AudioClip with spatial AudioSource |
| **Telemetry** | `TelemetryCollector.cs` | Metrics collection and percentile reporting |
| **Benchmark** | `BenchmarkRunner.cs` | Automated performance benchmarking |

### Editor Tools (`com.npctts.unity.editor`)

Unity Editor-only scripts for development and testing:

| Module | File | Responsibility |
|--------|------|----------------|
| **Editor Window** | `NpcTtsEditorWindow.cs` | Worker Manager UI (health, probe) |
| **Build Tools** | `NpcTtsBuildTools.cs` | Menu items, post-process build callbacks |

### Python Worker

The persistent TTS server process:

| Module | File | Responsibility |
|--------|------|----------------|
| **CLI** | `main.py` | Command-line entry point, argument parsing |
| **Server** | `server.py` | FastAPI app, HTTP route handlers |
| **Inference** | `inference/engine.py` | Model loading, generation, voice prompt caching |
| **Protocol** | `protocol.py` | Binary record encoding/decoding |
| **Queue** | `request_queue/__init__.py` | FIFO request scheduling, cancellation, deadlines |
| **Voices** | `voice_catalog.py` | Voice profile persistence and model isolation |

## Data Flow

### Speech Synthesis Path

```
1. Game calls NpcTtsManager.Speak(request)
   │
2. Manager validates request, creates SpeechHandle
   │
3. Manager calls WorkerClient.StreamRawPcm(text, language, voiceId)
   │      │
   │      │  POST /v1/tts/stream  →  {"text":"...", "language":"en", "voice_id":"default"}
   │      │
   │      ▼
   │   Worker: InferenceEngine.generate()
   │            │
   │            ├─→ Lookup voice prompt from VoiceCatalog
   │            ├─→ Qwen3-TTS model generates audio
   │            ├─→ Convert float32 → PCM16
   │            └─→ Stream raw PCM bytes
   │      │
   │      │  Response: raw PCM16 @ 24kHz
   │      ▼
   │   WorkerClient receives bytes
   │
4. Manager converts bytes → short[] samples → writes to PcmBuffer
   │
5. UtterancePlayer's OnAudioRead callback reads from PcmBuffer
   │
6. Unity AudioSource plays the streaming AudioClip
   │
7. OnFinished fires when buffer drains
```

### Voice Cloning Path

```
1. Record ~3s reference audio (WAV)
   │
2. POST /v1/voices → {"voice_id":"dwarf", "ref_audio_path":"/path.wav", "transcript":"..."}
   │      │
   │      ▼
   │   Worker: model.create_voice_clone_prompt(ref_audio, ref_text)
   │            │
   │            ├─→ Extract speaker embedding (x-vector)
   │            ├─→ Encode reference audio as discrete codes
   │            └─→ Store as VoiceClonePromptItem
   │
3. VoiceCatalog persists profile with model-specific prompt
   │
4. Runtime synthesis reuses cached prompt (no re-encoding)
```

## Deployment Modes

### Local Mode (Default)

```
┌──────────────┐      HTTP      ┌──────────────┐
│  Unity Game  │ ─────────────▶ │  Worker      │
│  (same PC)   │                │  (localhost)  │
└──────────────┘                └──────────────┘
```

- Worker binds to `127.0.0.1:8080` (loopback only)
- Single machine, no network overhead
- Ideal for single-player games and development

### Server Authority Mode (Multiplayer)

```
         ┌──────────┐
         │  Server   │
         │  (Headless│
         │   Linux)  │
         └────┬─────┘
              │  Synthesize once
       ┌──────┴────────┬─────────┐
       │               │         │
   ┌───▼───┐     ┌────▼───┐  ┌──▼────┐
   │ Player│     │ Player │  │Player │
   │  #1   │     │  #2    │  │  #3   │
   └───────┘     └────────┘  └───────┘
```

- Authoritative server runs the worker
- Server synthesizes each line once
- Audio is fanned out to players within earshot
- Server uses proximity policy to determine recipients
- Clients receive pre-synthesized audio over game transport
- **Reference audio is never sent to clients**

## Key Design Decisions

### One Worker, Many NPCs

A single worker process serves all NPCs. The request queue (1 active + N queued) prevents GPU overload. NPCs share the model session; voice prompts are cached per profile.

### Binary Protocol Over HTTP

The custom length-prefixed binary protocol provides:
- **Streaming**: PCM chunks arrive as they're generated
- **Chunked transfer**: Tolerates arbitrary read boundaries
- **Metadata**: Timing, voice info, and error data in structured records
- **Safety**: 16 MB record size guard, validated before allocation

### Lock-Free Audio Buffer

The PcmBuffer uses a single-producer/single-consumer pattern with atomic indices. The producer (network thread) writes; the consumer (Unity audio callback) reads. No locks in the hot path.

### Model Isolation

Voice profiles are tagged with a `model_id`. Changing the runtime model does not silently use stale prompts. Original reference assets are preserved so profiles can be regenerated.

### Stub Mode

The worker supports running without a model for development and testing. It generates a synthetic 440 Hz tone with proper protocol framing. All Unity-side code works identically.
