# Developer Guide

Guidelines and references for contributing to the Unity NPC TTS project.

## Project Structure

```
UnitySpeech/
├── README.md                          # Top-level project overview
├── SPEC.md                            # Original project specification
├── Assets/Docs/                       # This documentation
│   ├── architecture.md
│   ├── binary-protocol.md
│   ├── benchmarking.md
│   ├── build-and-use.md
│   ├── multiplayer.md
│   ├── testing.md
│   ├── troubleshooting.md
│   ├── unity6-getting-started.md
│   ├── unity-runtime-api.md
│   └── voice-cloning.md
├── Worker/                            # Python TTS worker
│   ├── main.py                        # CLI entry point
│   ├── download_models.py             # Model downloader
│   ├── probe_tts.py                   # Smoke benchmark
│   ├── start_worker.sh / stop_worker.sh
│   ├── requirements.txt
│   ├── src/
│   │   ├── server.py                  # FastAPI app, route handlers
│   │   ├── protocol.py                # Binary protocol encoding/decoding
│   │   ├── inference/engine.py        # Qwen3-TTS model integration
│   │   ├── voice_catalog.py           # Voice profile management
│   │   └── request_queue/__init__.py  # FIFO queue with cancellation
│   ├── tests/
│   │   ├── test_protocol.py           # 16 protocol tests
│   │   ├── test_request_queue.py      # 17 queue tests
│   │   └── test_voice_catalog.py      # 12 catalog tests
│   └── models/                        # Downloaded models
├── Packages/
│   ├── manifest.json                  # Unity package manifest
│   └── com.npctts.unity/             # UPM package
│       ├── package.json               # Package metadata
│       ├── Runtime/                   # Runtime assembly
│       │   ├── Core.cs                # Core types
│       │   ├── NpcTtsManager.cs       # Main manager
│       │   ├── StreamingSession.cs    # Future streaming
│       │   ├── Networking/WorkerClient.cs
│       │   ├── Protocol/StreamParser.cs
│       │   ├── Audio/PcmBuffer.cs
│       │   ├── Audio/UtterancePlayer.cs
│       │   ├── Telemetry/TelemetryCollector.cs
│       │   └── Benchmark/BenchmarkRunner.cs
│       ├── Editor/                    # Editor assembly
│       │   ├── NpcTtsEditorWindow.cs
│       │   └── NpcTtsBuildTools.cs
│       ├── Tests/                     # Unity tests
│       │   ├── EditMode/ProtocolTests.cs
│       │   └── PlayMode/
│       │       ├── AudioPlaybackTests.cs
│       │       └── IntegrationTests.cs
│       └── Samples~/                  # Sample scenes
├── Assets/                            # Unity assets
│   ├── Scenes/
│   ├── Settings/
│   └── Benchmark/
├── ProjectSettings/                   # Unity project settings
└── UnitySpeech.sln                    # Visual Studio solution
```

## Development Workflow

### 1. Worker Development

```bash
cd Worker
source .venv/bin/activate

# Run in stub mode for rapid iteration (no model needed)
python main.py

# Run tests
python -m pytest tests/ -v

# Run with a real model
python main.py --device mps
```

### 2. Unity Development

1. Open the project in Unity Editor
2. Start the worker in a separate terminal
3. Play the scene and test NPC speech
4. Use the Worker Manager window (Window → NPC TTS → Worker Manager)

### 3. Run Tests

```bash
# Python tests
cd Worker && python -m pytest tests/ -v

# Unity EditMode tests
Unity -batchmode -projectPath . -runTests -testPlatform EditMode -testResults editmode.xml -quit

# Unity PlayMode tests (starts worker automatically)
Unity -batchmode -projectPath . -runTests -testPlatform PlayMode -testResults playmode.xml -quit
```

## Coding Conventions

### Python (Worker)

- **Type hints**: Use throughout (`→ Generator[bytes, None, SpeechTiming]`)
- **Logging**: Use `logging.getLogger("module_name")` with structured messages
- **Error handling**: Log exceptions with `log.exception()`, return sentinel values to callers
- **Naming**: `snake_case` for variables and functions, `PascalCase` for classes
- **Docstrings**: Document public APIs with Google-style docstrings

### C# (Unity)

- **Namespace**: `NpcTts` for runtime, `NpcTts.Editor` for editor code
- **Naming**: `PascalCase` for public members, `_camelCase` for private fields
- **Immutability**: Prefer `readonly struct` for data types, `sealed class` for stateful types
- **Documentation**: XML doc comments on all public APIs
- **GC awareness**: Avoid allocations in hot paths (audio callback, per-frame code)

## Adding a New Endpoint

### Worker Side

1. Add the route to `server.py`:
```python
@app.get("/v1/my-endpoint")
async def my_endpoint():
    return JSONResponse({"result": "success"})
```

2. Add request/response models if needed (module-level for FastAPI schema)

3. Add tests to `tests/`

### Client Side

1. Add a method to `WorkerClient.cs`:
```csharp
public IEnumerator MyEndpoint(Action<MyResult> callback)
{
    var req = new UnityWebRequest(Endpoint + "/v1/my-endpoint", "GET");
    // ... configure and send
    callback(ParseMyResult(req.downloadHandler.text));
}
```

2. Add a public method to `NpcTtsManager.cs` that calls the client

## Adding a New Record Type

### Protocol

1. Add the type constant to `protocol.py`:
```python
RECORD_MYTYPE = 0x05
```

2. Implement `encode_mytype()` and `decode_mytype()` functions

3. Update `StreamParser` in C# to handle the new type

4. Add tests for encoding, decoding, and boundary tolerance

## Adding a New Voice Profile Mode

### Voice Catalog

1. Extend `VoiceProfile` with new fields
2. Update `create()` to handle the new mode
3. Ensure `to_dict()` / `from_dict()` roundtrip correctly
4. Add tests for the new mode

### Inference Engine

1. Extend `create_voice_prompt()` with new parameters
2. Cache the new prompt type in `_voice_cache`
3. Update `_generate_real()` to handle the new prompt format

## Debugging

### Worker Logs

```bash
# Increase verbosity
python main.py --log-level debug

# Check worker.log
tail -f Worker/worker.log
```

### Unity Console

- Filter by `[NpcTts]` tag
- Check for warnings about worker connectivity
- Use `Debug.Log` in the WorkerClient for HTTP-level debugging

### Audio Issues

- Use `probe_tts.py` to verify the worker is producing valid audio
- Check `PlaybackStats` for underruns
- Monitor `TelemetryCollector` metrics
- Verify the AudioClip's sample rate matches the worker (24000 Hz)

## File Structure Rules

- **Runtime** (`Runtime/`): Code that runs in builds
- **Editor** (`Editor/`): Editor-only code (marked with `#if UNITY_EDITOR`)
- **Tests** (`Tests/`): EditMode and PlayMode test fixtures
- **Samples~** (`Samples~/`): Example scenes and prefabs
- The `~` suffix on `Samples~` means Unity treats it as a sample folder (not imported by default)

## Building

```bash
# Build for macOS
Unity -batchmode -projectPath . -buildTarget StandaloneOSX -buildPath Builds/macOS -quit

# Build for Windows
Unity -batchmode -projectPath . -buildTarget StandaloneWindows64 -buildPath Builds/Windows -quit
```

The `NpcTtsPostprocessor` logs build events automatically.
