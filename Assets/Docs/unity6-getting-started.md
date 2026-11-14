# Getting Started with Unity 6

This guide walks you through setting up the NPC TTS system in a Unity 6 project, from installing the worker to making your first NPC speak.

## Prerequisites

- **Unity 6** (version 6000.3 or later, 27f1 recommended)
- **Python 3.10+** (3.12 recommended)
- **macOS Apple Silicon** for MPS (Metal) GPU inference, or any platform for CPU mode
- **~2 GB** disk space for the base model

## Step 1: Set Up the Worker

The worker is a standalone Python process that handles speech synthesis. It runs outside Unity as a separate process.

### 1a. Create a Virtual Environment

```bash
cd Worker
python3 -m venv .venv
source .venv/bin/activate       # macOS/Linux
# .venv\Scripts\activate         # Windows
```

### 1b. Install Dependencies

```bash
pip install -r requirements.txt
```

This installs: FastAPI, uvicorn, PyTorch, transformers, qwen-tts, and other dependencies.

### 1c. Download the Model

```bash
python download_models.py --model base
```

This downloads the Qwen3-TTS 0.6B Base model (~1.7 GB) to `Worker/models/`.

**Optional:** Download the 1.7B VoiceDesign model for voice creation:

```bash
python download_models.py --model voicedesign
```

**For users in Mainland China:** Use ModelScope instead:

```bash
python download_models.py --model base --modelscope
```

### 1d. Start the Worker

**Recommended (Apple Silicon GPU):**
```bash
python main.py --device mps
```

**CPU fallback:**
```bash
python main.py --device cpu --threads 8
```

**Stub mode (no model, testing only):**
```bash
python main.py
```
Stub mode generates a synthetic 440 Hz tone instead of real speech. Use it for development without downloading models.

Verify the worker is running:
```bash
curl http://127.0.0.1:8080/v1/health
```

Expected response:
```json
{
  "status": "ok",
  "device": "mps",
  "model": "Qwen/Qwen3-TTS-12Hz-0.6B-Base",
  "streaming": true,
  "voice_selection": true
}
```

## Step 2: Open the Unity Project

1. Open **Unity Hub**
2. Click **Add** → **Add project from drive**
3. Navigate to this project folder and click **Add**
4. Double-click the project to open it

The project uses the **Universal Render Pipeline (URP)** and **Input System**.

## Step 3: Add the NpcTtsManager

The `NpcTtsManager` is the main entry point for all speech synthesis. Add it to a persistent GameObject:

### Method A: Inspector

1. In the Hierarchy, right-click → **Create Empty**
2. Name it `NpcTtsManager`
3. Click **Add Component** → **Npc TTS → NpcTtsManager**
4. In the Inspector, verify the **Endpoint** is `http://127.0.0.1:8080`
5. Click **Add Component** → **Audio Listener** (if not already present on your camera)

### Method B: Script

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

## Step 4: Make an NPC Speak

### Basic Usage

```csharp
using NpcTts;

public class DwarvenGreeting : MonoBehaviour
{
    private NpcTtsManager _tts;

    void Awake()
    {
        _tts = FindObjectOfType<NpcTtsManager>();
    }

    void Start()
    {
        var request = new SpeechRequest(
            utteranceId: "dwarf-greeting",
            npcId: "dwarf-blacksmith",
            voiceId: "default",
            text: "Mind the step. I put it there forty years ago."
        );
        _tts.Speak(request);
    }
}
```

### With Event Handling

```csharp
void Start()
{
    _tts.OnStarted += handle => Debug.Log($"Started: {handle.UtteranceId}");
    _tts.OnFinished += handle => Debug.Log($"Finished: {handle.UtteranceId}");
    _tts.OnFailed += (handle, msg) => Debug.LogError($"Failed: {msg}");

    var request = new SpeechRequest("line-1", "npc-id", "default", "Hello there!");
    var handle = _tts.Speak(request);

    // Cancel if needed
    // handle.Cancel();
}
```

### Multiple NPCs

Each NPC gets its own spatial AudioSource automatically:

```csharp
void OnTriggerEnter(Collider other)
{
    if (other.CompareTag("Player"))
    {
        var request = new SpeechRequest(
            utteranceId: "shop-welcome",
            npcId: "dwarf-shopkeeper",
            voiceId: "default",
            text: "Welcome, traveler! What brings you to my forge?"
        );
        _tts.Speak(request);
    }
}
```

## Step 5: Use Cloned Voices

### Create a Voice

1. Record a ~3 second audio clip (WAV format, mono)
2. Place it in your project or on disk

```csharp
// From the Editor or at runtime:
_tts.StartCoroutine(_tts.CreateVoice(
    voiceId: "dwarf",
    language: "en",
    transcript: "Hello there, friend!",
    refAudioPath: "/absolute/path/to/dwarf.wav"
));
```

### Use the Cloned Voice

```csharp
var request = new SpeechRequest(
    utteranceId: "dwarf-line",
    npcId: "dwarf-blacksmith",
    voiceId: "dwarf",            // <-- Use the cloned voice
    text: "That'll be five silver pieces."
);
_tts.Speak(request);
```

See [Voice Cloning](voice-cloning.md) for the full workflow.

## Step 6: Use the Editor Tools

Open **Window → NPC TTS → Worker Manager** to:

- Check worker health
- Configure the endpoint URL
- Run a quick probe test (must be in Play Mode)

## Troubleshooting

### Worker Not Found

- Ensure the worker is running: `curl http://127.0.0.1:8080/v1/health`
- Check the endpoint URL in the NpcTtsManager Inspector
- Check `Worker/worker.log` for errors

### No Audio Playing

- Verify Unity's audio settings: **Edit → Project Settings → Audio**
- Ensure an AudioListener exists in the scene
- Check that the worker model is loaded (health endpoint shows `status: ok`)
- Verify spatial audio settings: `minDistance` defaults to 1m, `maxDistance` to 50m

### "Worker Unavailable" Error

- The worker may not be running, or the model failed to load
- Check the Unity Console for the specific error message
- In stub mode, the worker returns synthetic audio — this is expected for development

### Chinese Language Support

Qwen3-TTS supports Chinese. Use `"zh"` as the language code:

```csharp
var request = new SpeechRequest(
    utteranceId: "hello-zh",
    npcId: "npc-chinese",
    voiceId: "default",
    text: "你好，世界！",
    language: "zh"
);
```

## Next Steps

- [Architecture Overview](architecture.md) — System design and data flow
- [Runtime API Reference](unity-runtime-api.md) — Complete API documentation
- [Benchmarking](benchmarking.md) — Performance measurement
- [Testing](testing.md) — Running the test suites
