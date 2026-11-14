# Multiplayer Deployment

Deploy NPC TTS across a multiplayer game with server authority.

## Architecture

```
         ┌──────────────────────────────┐
         │  Game Server (Headless)       │
         │                               │
         │  ┌──────────┐  ┌───────────┐  │
         │  │  Worker   │  │  Game     │  │
         │  │  (TTS)    │  │  Logic    │  │
         │  └────┬─────┘  └────┬──────┘  │
         │       │              │          │
         │       └────┬────────┘          │
         │            │                    │
         │  Synthesize once, fan out to   │
         │  eligible players               │
         └────────────┬───────────────────┘
                      │  Game transport
              ┌───────┼────────┐
              │       │        │
          ┌───▼──┐ ┌─▼──┐ ┌──▼────┐
          │ P1   │ │ P2 │ │  P3   │
          │(audio│ │(no │ │ (audio│
          │ play)│ │play)│ │ play)│
          └──────┘ └────┘ └───────┘
```

## Server-Side Setup

### 1. Run the Worker on the Server

```bash
# On the game server (headless Linux)
cd Worker
python3 -m venv .venv
source .venv/bin/activate
pip install -r requirements.txt
python download_models.py --model base
python main.py --device cpu --threads 8 --host 127.0.0.1
```

### 2. Implement Server Synthesis

The authoritative server (game logic) decides when an NPC speaks:

```python
# Pseudocode: Server-side speech dispatch
async def on_npc_dialogue(npc_id, text, voice_id, recipients):
    # Synthesize once
    audio = await worker.synthesize(text, voice_id)

    # Fan out to players within earshot
    for player in recipients:
        await game_transport.send(player, {
            "type": "npc_speech",
            "npc_id": npc_id,
            "position": npc.position,
            "audio": audio,
            "utterance_id": generate_id(),
        })
```

### 3. Client-Side Playback

The Unity client receives pre-synthesized audio and plays it spatially:

```csharp
// On receiving an npc_speech message from the server
void PlayRemoteSpeech(string npcId, Vector3 position, byte[] audioData)
{
    // Find or create the NPC's AudioSource
    var source = FindOrCreateSource(npcId, position);

    // Convert PCM16 to float array
    var samples = PcmBuffer.ToSamples(audioData);
    var floats = new float[samples.Length];
    for (int i = 0; i < samples.Length; i++)
        floats[i] = samples[i] / 32768.0f;

    // Create and play
    var clip = AudioClip.Create($"Remote-{npcId}", samples.Length, 1, 24000, false);
    clip.SetData(floats, 0);
    source.PlayOneShot(clip);
}
```

## Proximity Policy

The server determines which players hear each utterance:

```python
def get_recipients(npc_position, all_players, max_distance=50.0):
    """Return players within earshot of the NPC."""
    return [
        p for p in all_players
        if distance(p.position, npc_position) <= max_distance
        and p.is_alive
        and not p.is_muted
    ]
```

**Rules:**
- **Same map only**: Players on different maps do not hear the NPC
- **Distance-based**: Use the NPC's `maxDistance` (default 50m) as the threshold
- **Line of sight** (optional): Raycast from NPC to each player
- **Possession**: Cancel playback when a player loses NPC possession
- **Despawn**: Cancel all speech when an NPC despawns

## Client Delivery Rules

| Scenario | Behavior |
|----------|----------|
| Player enters range mid-utterance | Waits for the next line (v1) |
| Player leaves range during playback | Cancel playback immediately |
| NPC despawns | Cancel all speech for that NPC |
| Map transition | Cancel all NPC speech |
| Possession handoff | Cancel speech for the old owner |

## Security Considerations

### Loopback-Only Binding

The worker binds to `127.0.0.1` by default. For remote deployments:

```bash
# Server: bind to all interfaces (requires authentication layer)
python main.py --host 0.0.0.0 --port 8080

# Client: connect to the server's address
manager.Endpoint = "http://game-server.example.com:8080"
```

**WARNING:** Exposing the worker directly to the internet is a security risk. Always use:
1. An authenticated transport layer (game server relay)
2. Rate limiting and request validation
3. Network segmentation (worker only accessible from the game server)

### Do Not Ship Reference Audio to Clients

In server synthesis mode:
- The worker creates voice prompts on the server
- Clients receive pre-synthesized PCM audio
- **Never** send reference WAV files or voice prompts to clients
- This prevents voice model extraction

## Subtitles

The server sends subtitles independently of audio:

```json
{
  "type": "npc_subtitle",
  "npc_id": "dwarf-blacksmith",
  "text": "Five silver pieces for the hammer.",
  "language": "en",
  "duration_s": 2.5
}
```

This ensures players with audio disabled or in languages not yet supported still receive the dialogue content.

## Performance Considerations

### Server Resource Planning

| Metric | Estimate |
|--------|----------|
| CPU per utterance | ~2s on 8-core (0.6B Base) |
| Memory per utterance | ~200 MB (model常驻) + transient |
| Queue depth | 8 (default), configurable |
| Concurrent NPCs | Limited by queue + queue rejection |

**Rule of thumb:** One 8-core server handles ~4 concurrent NPC speech requests at 60 FPS.

### Client Performance

- Pre-synthesized audio playback has **zero** CPU overhead during playback
- AudioClip creation and `SetData` are single-frame operations
- Spatial audio uses Unity's built-in AudioSource (optimized)
- No per-frame CPU cost beyond Unity's standard audio mixing
