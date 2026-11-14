# Building and Using the NPC TTS Plugin in Unity

This document explains how to build the two parts of this project — the Python TTS
**worker** and the Unity **UPM package** (`com.npctts.unity`) — and how to use the
plugin from a Unity project to make NPCs speak.

```
Unity Project  ─────HTTP (localhost)─────>  Worker Process (Python)
  com.npctts.unity                              Qwen3-TTS 0.6B / 1.7B
  NpcTtsManager → Speak()                       Queue, voice cache, streaming PCM
```

There are two "builds" involved:

1. **Worker build** — set up the Python environment and download the model.
2. **Package build** — compile the `com.npctts.unity` package into your Unity project
   (Unity compiles it automatically once the package is installed).

---

## 1. Prerequisites

| Requirement | Version | Notes |
|-------------|---------|-------|
| Unity | 6000.3 or later (6000.3.27f1 recommended) | URP and Input System used by the demo project |
| Python | 3.10+ (3.12 recommended) | For the worker process |
| Disk space | ~2 GB | Qwen3-TTS 0.6B Base model (~1.7 GB) |
| Hardware | Apple Silicon (MPS) or any CPU | MPS strongly recommended on macOS |

---

## 2. Build the Worker

The worker is a standalone Python (FastAPI) process. Unity talks to it over HTTP;
it does **not** run inside Unity.

### 2a. Create the environment

```bash
cd Worker
python3 -m venv .venv
source .venv/bin/activate        # macOS/Linux
# .venv\Scripts\activate         # Windows
pip install -r requirements.txt
```

### 2b. Download a model (optional for stub mode)

```bash
# Runtime synthesis model (~1.7 GB) — required for real speech
python download_models.py --model base

# Optional: voice-design model for creating voices from text descriptions
python download_models.py --model voicedesign

# Mainland China mirror
python download_models.py --model base --modelscope
```

Models land in `Worker/models/`. You can skip this step and run the worker in
**stub mode**, which generates a 440 Hz tone instead of speech — useful for
developing the Unity side without downloading anything.

### 2c. Run the worker

```bash
python main.py --device mps                          # Apple Silicon GPU (recommended)
python main.py --device cpu --threads 8              # CPU fallback
python main.py                                       # stub mode (no model)
python main.py --model ./models/Qwen--Qwen3-TTS-12Hz-0.6B-Base --device mps
python main.py --quantize 4bit --model ./models/... --device cpu   # quantized CPU
python main.py --port 9000 --queue 16                # custom port / queue size
```

### 2d. Verify the build

```bash
curl http://127.0.0.1:8080/v1/health
```

Expected:

```json
{ "status": "ok", "device": "mps", "model": "Qwen/Qwen3-TTS-12Hz-0.6B-Base", "streaming": true, "voice_selection": true }
```

Optional smoke test of the binary stream protocol:

```bash
python probe_tts.py http://127.0.0.1:8080/v1/tts/stream
```

---

## 3. Install the Package in a Unity Project

The Unity side ships as a UPM package at `Packages/com.npctts.unity` with three
assemblies: `Runtime`, `Editor` (asmdef: `com.npctts.unity.editor`), and `Tests`.
Unity compiles them automatically once installed — there is no separate build step.

### Option A — Open this repository as the project

The repo is itself a Unity project with the package embedded:

1. Open **Unity Hub → Add → Add project from drive**, select this folder.
2. Open it with Unity 6000.3+. Unity resolves the embedded package and compiles
   the plugin assemblies.

### Option B — Install into your own project from a local path

Copy the package folder into your project's `Packages/` directory (an *embedded*
package), or reference it by path. Add to `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.npctts.unity": "file:../path/to/com.npctts.unity"
  }
}
```

### Option C — Package as a tarball and install by file

From the folder **containing** `Packages/com.npctts.unity`:

```bash
cd Packages/com.npctts.unity
npm pack          # produces com.npctts.unity-0.1.0.tgz
```

Then in your project's `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.npctts.unity": "file:/absolute/path/to/com.npctts.unity-0.1.0.tgz"
  }
}
```

Or via the Editor: **Window → Package Manager → + → Add package from tarball…**

### Package dependencies

The package declares only `com.unity.modules.audio` and
`com.unity.modules.unitywebrequest` (built-in modules). No third-party
dependencies. A sample (**Dwarf NPC Demo**) is declared under `Samples~` in
`package.json`; when present it can be imported from the Package Manager's
Samples page.

### Verify compilation

After installation, open the Console — the `com.npctts.unity` runtime and editor
assemblies should compile without errors. You can also run the EditMode tests
(see [Testing](testing.md)).

---

## 4. Use the Plugin

### 4a. Add the NpcTtsManager

`NpcTtsManager` is the single entry point. Add it to a persistent GameObject:

**Inspector:**
1. Hierarchy → right-click → **Create Empty**, name it `NpcTtsManager`.
2. **Add Component → NPC TTS → NpcTtsManager**.
3. Confirm **Endpoint** is `http://127.0.0.1:8080`.
4. Make sure an **AudioListener** exists in the scene (usually on the main camera).

**Or from script:**

```csharp
using UnityEngine;
using NpcTts;

public class Setup : MonoBehaviour
{
    void Awake()
    {
        var go = new GameObject("NpcTtsManager");
        var manager = go.AddComponent<NpcTtsManager>();
        manager.Endpoint = "http://127.0.0.1:8080";
        DontDestroyOnLoad(go);
    }
}
```

Key inspector settings: **Endpoint**, **Health Interval** (10 s), **Prebuffer Ms**
(120), **Buffer Capacity Seconds** (5), **Default Voice ID** (`default`),
**Default Language** (`en`).

### 4b. Make an NPC speak

```csharp
using NpcTts;

public class DwarvenGreeting : MonoBehaviour
{
    private NpcTtsManager _tts;

    void Awake()  => _tts = FindObjectOfType<NpcTtsManager>();

    void Start()
    {
        var request = new SpeechRequest(
            utteranceId: "dwarf-greeting",
            npcId: "dwarf-blacksmith",     // one spatial AudioSource per NPC
            voiceId: "default",            // or a cloned voice id
            text: "Mind the step. I put it there forty years ago.",
            language: "en"                 // optional, defaults to "en"
        );
        _tts.Speak(request);
    }
}
```

Audio streams from the worker as mono PCM16 @ 24 kHz, is prebuffered, and plays
through an automatically created spatial `AudioSource` at the NPC's transform
(min distance 1 m, max distance 50 m).

### 4c. Handle lifecycle events and cancel

```csharp
_tts.OnStarted   += h => Debug.Log($"Started: {h.UtteranceId}");
_tts.OnFinished  += h => Debug.Log($"Finished: {h.UtteranceId}");
_tts.OnCancelled += h => Debug.Log($"Cancelled: {h.UtteranceId}");
_tts.OnFailed    += (h, msg) => Debug.LogError($"Failed: {msg}");

var handle = _tts.Speak(request);
handle.Cancel();              // cancel one utterance (idempotent)
_tts.StopNpc("dwarf-blacksmith");  // stop everything for one NPC
```

Handle status moves through: `Pending → Queued → Active → Playing → Finished`
(or `Cancelled` / `Failed` with a `TerminalReason` such as `WorkerUnavailable`,
`QueueOverload`, `Timeout`).

### 4d. Wait for the worker before speaking

```csharp
if (_tts.WaitUntilHealthy(30f))
    _tts.Speak(request);
```

### 4e. Use cloned voices

1. Record a ~3 second mono WAV clip of the target voice.
2. Create the profile (worker must be running with a real model):

```csharp
StartCoroutine(_tts.CreateVoice(
    voiceId: "dwarf",
    language: "en",
    transcript: "Hello there, friend!",           // exact words in the clip
    refAudioPath: "/absolute/path/to/dwarf.wav"
));
```

3. Speak with it by setting `voiceId: "dwarf"` in the `SpeechRequest`.

The worker caches the clone prompt, so later lines cost only normal synthesis
time (~1.8 s with a cloned voice on MPS). See [Voice Cloning](voice-cloning.md).

### 4f. Editor tools

With the package installed, the menu **Window → NPC TTS → Worker Manager** opens
a window that can:

- Set the endpoint URL on the scene's `NpcTtsManager`
- **Check Health** against the worker
- **Run Probe** — speak a test line (requires Play Mode)

### 4g. Telemetry and benchmarking

`NpcTtsManager.Telemetry` collects per-utterance metrics (queue delay, time to
first PCM, synthesis time) with p50/p95/p99 export. For full performance runs add
`BenchmarkRunner` to a GameObject and call `Run()` — see
[Benchmarking](benchmarking.md).

---

## 5. Building a Standalone Player

- Build normally via **File → Build Settings → Build**. The NPC TTS postprocessor
  only logs; no special build steps are required.
- The worker is a **separate process** — the built player does not embed it. End
  users (or your deploy script) must start the worker before or alongside the
  game, and point the manager at its URL.
- **Local play**: keep the default loopback endpoint `http://127.0.0.1:8080`.
- **Remote/multiplayer**: host the worker on a game server (headless Linux, CPU
  inference works) and set the endpoint accordingly. Only bind `0.0.0.0` behind
  authenticated transport — the worker itself has no auth. See
  [Multiplayer](multiplayer.md).

---

## 6. Quick Troubleshooting

| Symptom | Fix |
|---------|-----|
| `WorkerUnavailable` / "Worker Unavailable" | Worker not running or model not loaded. `curl http://127.0.0.1:8080/v1/health`, check `Worker/worker.log` |
| No audio | Ensure an AudioListener exists; check **Edit → Project Settings → Audio**; NPC must be within 50 m |
| Package errors after install | Confirm Unity 6000.3+; confirm the path in `manifest.json` resolves |
| First line very slow | Cold model load (~25 s) plus warmup (~3 s); keep the worker running between sessions |
| `QueueOverload` (429) | Worker queue full — raise it with `--queue 16` or slow request rate |

Full list: [Troubleshooting](troubleshooting.md).

## Related Documents

- [Getting Started with Unity 6](unity6-getting-started.md)
- [Unity Runtime API](unity-runtime-api.md)
- [Worker Reference](worker.md)
- [Binary Protocol](binary-protocol.md)
- [Voice Cloning](voice-cloning.md)
- [Testing](testing.md) · [Benchmarking](benchmarking.md) · [Architecture](architecture.md)
