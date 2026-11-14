# Unity NPC TTS

A persistent Qwen3-TTS worker runs alongside a Unity 6 client, providing speech synthesis and spatial playback for in-world NPCs. In multiplayer, the server synthesizes each line once and fans out to players within earshot.

Built on [Qwen3-TTS](https://github.com/QwenLM/Qwen3-TTS) — a family of multilingual, controllable, streaming text-to-speech models.

```
Unity Client  ─────HTTP─────>  Worker Process
                          │
                     ┌─────┴──────┐
                     │  Queue mgr  │  1 active, N queued
                     │  Inference  │  Qwen3-TTS 0.6B / 1.7B
                     │  Voice cache│  Model-specific prompts
                     └─────────────┘
```

## Quick Start

```bash
# 1. Set up the Python worker
cd Worker
python3 -m venv .venv
source .venv/bin/activate
pip install -r requirements.txt

# 2. Download the model (~1.7 GB)
python download_models.py --model base

# 3. Start the worker
python main.py --device mps          # Apple Silicon GPU (recommended)
# or
python main.py --device cpu           # CPU fallback

# 4. Open Unity and import the project
#    The package auto-detects the worker at http://127.0.0.1:8080
```

## Architecture

| Component | Technology | Role |
|-----------|-----------|------|
| **Worker** | Python, FastAPI | Persistent model session, voice catalog, bounded queue, streaming PCM |
| **Unity Runtime** | C# UPM (`com.npctts.unity`) | Worker client, protocol decoder, PCM buffering, spatial playback |
| **Unity Editor** | C# Editor scripts | Worker health check, voice management, benchmark launcher |
| **Tests** | pytest (Python), NUnit (C#) | Protocol, queue, voice catalog, audio playback, integration |

## Documentation

| Document | Description |
|----------|-------------|
| [Building and Using the Plugin](Assets/Docs/build-and-use.md) | Build the worker and UPM package; use the plugin in a Unity project |
| [Getting Started with Unity 6](Assets/Docs/unity6-getting-started.md) | Step-by-step integration guide |
| [Project Architecture](Assets/Docs/architecture.md) | High-level system design and data flow |
| [Worker Reference](Assets/Docs/worker.md) | Python server: endpoints, CLI, configuration |
| [Binary Protocol](Assets/Docs/binary-protocol.md) | Length-prefixed record format specification |
| [Unity Runtime API](Assets/Docs/unity-runtime-api.md) | C# API reference for `NpcTtsManager` and related types |
| [Voice Cloning](Assets/Docs/voice-cloning.md) | Create, manage, and use cloned voices |
| [Testing](Assets/Docs/testing.md) | Running EditMode, PlayMode, and integration tests |
| [Benchmarking](Assets/Docs/benchmarking.md) | Performance measurement and reporting |

## Supported Languages

Chinese, English, Japanese, Korean, German, French, Russian, Portuguese, Spanish, Italian

## Measured Performance (Apple Silicon, MPS)

| Metric | Value |
|--------|-------|
| Model load time | ~25 s (cold), ~5 s (warm) |
| Warmup synthesis | ~3 s |
| RTF (avg) | **0.58** (faster than real-time) |
| Cloned voice latency | ~1.8 s |
| Default voice latency | ~2.5 s |

*Tested with Qwen3-TTS 0.6B Base on M-series Mac, MPS device.*

## License

Qwen3-TTS models are released under Apache 2.0. This project's code is also Apache 2.0.
