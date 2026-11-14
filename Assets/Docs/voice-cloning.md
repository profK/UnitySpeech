# Voice Cloning

Create custom NPC voices by providing a reference audio sample. The worker extracts a speaker embedding from the audio and caches it for fast runtime synthesis.

## Workflow Overview

```
Record audio  →  Register voice  →  Use in synthesis
  (3s WAV)       (POST /v1/voices)    (voice_id: "dwarf")
```

## Step 1: Record Reference Audio

Record a clean, ~3 second audio clip of the target voice speaking naturally.

**Requirements:**
- Format: WAV (PCM, mono preferred)
- Duration: 2-5 seconds (3 seconds ideal)
- Quality: Clear speech, minimal background noise
- Content: Natural, conversational text

**Tips:**
- Use a quiet recording environment
- Speak naturally — the model captures prosody and emotion
- Longer clips (up to 5s) capture more character but slow down initial processing
- Multiple short clips are not better than one medium-quality clip

## Step 2: Register the Voice

### Via the Worker API

```bash
curl -X POST http://127.0.0.1:8080/v1/voices \
  -H "Content-Type: application/json" \
  -d '{
    "voice_id": "dwarf",
    "language": "en",
    "transcript": "Hello there, friend! Welcome to my forge.",
    "ref_audio_path": "/absolute/path/to/dwarf.wav"
  }'
```

**Response:**
```json
{
  "voice_id": "dwarf",
  "status": "created"
}
```

### Via Unity Runtime

```csharp
IEnumerator Start()
{
    var manager = FindObjectOfType<NpcTtsManager>();
    yield return manager.CreateVoice(
        voiceId: "dwarf",
        language: "en",
        transcript: "Hello there, friend! Welcome to my forge.",
        refAudioPath: "/absolute/path/to/dwarf.wav"
    );
    Debug.Log("Voice 'dwarf' is ready!");
}
```

**Important:** The `refAudioPath` must be an **absolute path** accessible by the worker process. On macOS, this is typically `/Users/username/...` or a path relative to the project root.

### Via Unity Editor Window

1. Open **Window → NPC TTS → Worker Manager**
2. (Voice management UI coming in a future update)

## Step 3: Use the Voice

```csharp
var request = new SpeechRequest(
    utteranceId: "dwarf-line-001",
    npcId: "dwarf-blacksmith",
    voiceId: "dwarf",              // ← Use the cloned voice
    text: "That'll be five silver pieces.",
    language: "en"
);

var handle = manager.Speak(request);
```

## How It Works

### Voice Registration

1. The worker sends the reference audio and transcript to the model
2. The model extracts a **speaker embedding** (x-vector) from the audio
3. The model encodes the reference audio into **discrete speech codes**
4. These are stored as a `VoiceClonePromptItem` and cached in the voice catalog
5. The profile is persisted to disk with model-specific tagging

### Runtime Synthesis

1. The worker looks up the cached `VoiceClonePromptItem` by voice ID
2. The model uses the cached prompt directly (no re-encoding)
3. Synthesis is fast because the voice characteristics are pre-computed

**Result:** Cloned voice synthesis is ~1.8s latency vs ~2.5s for the default voice.

## Voice Profile Details

Each voice profile stores:

| Field | Purpose |
|-------|---------|
| `voice_id` | Unique identifier used in synthesis requests |
| `model_id` | Which model generated this profile (for isolation) |
| `revision` | Monotonic version number; bumps on regeneration |
| `language` | Default language for this voice |
| `ref_wav_path` | Path to the original reference WAV |
| `transcript` | Exact transcript of the reference audio |
| `ref_hash` | SHA-256 hash of the reference file (integrity check) |
| `transcript_hash` | SHA-256 hash of the transcript text |
| `prompt_data` | Model-specific cached voice prompt (JSON) |

## Managing Voices

### List All Voices

```bash
curl http://127.0.0.1:8080/v1/voices
```

### Model Isolation

Voice profiles are tagged with a `model_id`. If you change from the 0.6B model to the 1.7B model, the worker will NOT silently use stale prompts. You must re-create voices for the new model.

Original reference assets are preserved so you can regenerate voices when changing models.

### Update a Voice

Re-register with the same `voice_id`. The revision number increments, and the new prompt replaces the old one.

```bash
curl -X POST http://127.0.0.1:8080/v1/voices \
  -H "Content-Type: application/json" \
  -d '{
    "voice_id": "dwarf",
    "language": "en",
    "transcript": "A slightly different line for re-recording.",
    "ref_audio_path": "/path/to/better-recording.wav"
  }'
```

## Voice Audition (VoiceDesign)

The 1.7B VoiceDesign model lets you create voices from text descriptions rather than recordings.

```bash
curl -X POST http://127.0.0.1:8080/v1/voices/audition \
  -H "Content-Type: application/json" \
  -d '{
    "voice_id": "grumpy-dwarf",
    "language": "en",
    "description": "A grumpy old dwarf with a gravelly, deep voice",
    "sample_text": "Get out of my mine!"
  }'
```

**Status:** Integration is pending. The endpoint currently returns a placeholder.

## Performance Notes

| Scenario | First Latency | Subsequent Latency |
|----------|-------------|-------------------|
| Default voice | ~2.5s | ~1.5s |
| Cloned voice | ~1.8s | ~1.2s |
| Voice prompt cache hit | ~1.0s | ~0.8s |

Cloned voices are generally faster than the default voice because the prompt is pre-computed and cached. The first use of a cloned voice may be slightly slower as the prompt is loaded from disk.

## Troubleshooting

### "Prompt Creation Failed"

- The reference audio may be too short (< 1 second)
- The audio file may be corrupt or in an unsupported format
- The model may not be loaded (check health endpoint)

### Voice Sounds Wrong

- Ensure the transcript exactly matches the audio content
- Try a cleaner recording with less background noise
- Check that the language code matches the spoken language

### Voice Not Found

- Check the voice ID spelling (case-sensitive)
- Verify the worker has the correct model loaded
- List voices: `curl http://127.0.0.1:8080/v1/voices`
