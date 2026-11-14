# Unity NPC TTS – Specification

A persistent Qwen3-TTS worker runs alongside a Unity 6 client, providing
speech synthesis and spatial playback for in-world NPCs. In multiplayer, the
server synthesizes each line once and fans out to players within earshot.

---

## Status

| Component | Status |
|-----------|--------|
| Worker (Python) | ✅ Complete — Qwen3-TTS 0.6B Base on MPS |
| Voice cloning | ✅ Working — ref audio → cached prompt |
| Binary protocol | ✅ Complete — length-prefixed records |
| Probe / benchmark | ✅ Running — 49/49 tests pass |
| Unity Runtime | ✅ Complete — C# UPM package with full API |
| Unity Editor | ✅ Complete — Worker Manager window, build tools |
| Unity Tests | ✅ Complete — EditMode protocol + PlayMode audio/integration |
| Unity Samples | ⬜ Not started — Dwarf NPC demo pending |

---

## Scope

Build a Unity 6 UPM package, `com.npctts.unity`, for local Qwen3-TTS speech
and spatial playback. Target macOS Apple Silicon (Metal and CPU) first, with
Windows/Linux standalone clients and Linux headless authority supported through
the same worker protocol.

Two deployment modes:
1. **Server synthesis** – Authority synthesizes; listening clients play spatially.
2. **Local synthesis** – External worker process on the rendering machine.

Multiplayer transport is left to the host game through an adapter.
Inference dependencies and weights stay outside the Unity player.

Use **Qwen3-TTS 1.7B VoiceDesign** offline to audition voices. Store each
accepted reference WAV and exact transcript, then use **Qwen3-TTS 0.6B Base**
for runtime synthesis. Audition 1.7B Base if 0.6B loses too much character.

Voice profiles are model-specific and include: voice ID, revision, language,
reference hash, transcript hash. Preserve original assets so profiles can be
regenerated when changing runtime or model. Do not ship reference audio to
clients in server mode.

---

## Package layout

| Component | Responsibility |
|-----------|----------------|
| **Worker** (Python) | Persistent model session, versioned voice catalog, bounded queue, native CPU/GPU inference, stream encoding |
| **Runtime** (C# UPM) | Request handles, worker client, PCM buffering, per-NPC playback, cancellation, telemetry |
| **Editor** (C# UPM) | Worker health check, voice preview/validation, benchmark build/launcher |
| **Samples** | Dwarf NPC demo, fixed-camera-path stress scene, benchmark utterance manifest |
| **Tests** | EditMode protocol tests, PlayMode audio/lifecycle tests, standalone performance runner |

---

## Binary protocol

The worker's HTTP responses stream length-prefixed records. The client must
tolerate arbitrary read boundaries.

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
| START  | 0x01 | version (uint8), voice_id (len-prefixed UTF-8), language (len-prefixed UTF-8), duration_s (float64), sample_rate (uint32) |
| PCM    | 0x02 | sample_offset (uint64, contiguous index), pcm_bytes (raw int16 LE) |
| END    | 0x03 | total_samples (uint32), wall_ms, inference_ms, silence_head_ms (float64 each) |
| ERROR  | 0x04 | code (int32), message (len-prefixed UTF-8) |

PCM records include a contiguous sample offset. End includes total samples and
worker timing deltas. HTTP failures before streaming use structured JSON errors.
Errors after headers use a terminal ERROR record. Unexpected EOF is a failure.

---

## Worker endpoints

| Method | Path | Purpose |
|--------|------|---------|
| GET | `/v1/health` | Status, device, model, quantization, threads, streaming, voice_selection, queue depth |
| POST | `/v1/speech` | Synthesis with queuing; returns JSON with request_id and status |
| DELETE | `/v1/speech/{requestId}` | Idempotent cancellation |
| POST | `/v1/tts/stream` | Raw PCM stream for smoke tests (no protocol framing) |
| GET | `/v1/voices` | List available voice profiles |
| POST | `/v1/voices` | Create a voice profile from reference + transcript |

The worker advertises: actual device, model, quantization, configured CPU
threads, streaming capability, voice selection per request.

**Queue policy:** One active request, eight queued (FIFO). Reject additional
requests explicitly with 429. Expire requests at their deadline. Remove
cancelled entries promptly. Cancel in-flight generation when the backend
supports it; otherwise drain/discard and hold the active slot until inference
exits. Avoid silent device fallback during measurement.

CPU mode: configurable threads (default 4). GPU mode: one active generation.
Keep the model warm. Cache derived voice prompts by model and profile revision.
A single-voice C backend that preloads its clone at startup is suitable for
the dwarf prototype. A multi-NPC worker must pass a per-request cached voice
prompt. Do not start one model process per NPC.

Bind local workers to loopback. For remote deployments use authenticated
transport and an authoritative game-server relay.

---

## Unity runtime (public API)

```csharp
SpeechHandle Speak(SpeechRequest request, CancellationToken cancellation);
void StopNpc(string npcId);
```

**SpeechRequest:** utterance_id, npc_id, voice_id, language, text, optional deadline.
**SpeechHandle:** status, completion Task, cancellation.

Main-thread events: started, finished, canceled, failed.
Terminal reasons: unknown_voice, overload, worker_unavailable, timeout,
protocol_error, playback_failure.

Cancellation is idempotent. Ignore late chunks for canceled or superseded
requests. Show the subtitle on failure. Keep the dialogue system responsive.

---

## Spatial audio and world integration

Unity receives and decodes PCM off the main thread into a bounded, preallocated
single-producer/single-consumer buffer. The Unity audio callback consumes
samples and zero-fills on underrun. It must not block, allocate, log, or call
gameplay APIs.

Create and configure clips and AudioSources on the main thread. Use a streaming
AudioClip on a pooled, fully spatial AudioSource attached to the NPC.

Begin playback after a configurable prebuffer (120 ms initially, or all samples
for a shorter finished line). Apply backpressure when the buffer fills. Finish
after end-of-stream and buffer drain. Track underrun events and inserted silence
separately from leading silence generated by the model.

**Multiplayer delivery:** The authoritative server resolves NPC identity, current
map, position, and eligible recipients using the game's existing proximity policy.
Synthesize once per utterance and fan out the same audio to those recipients.
Associate frames with NPC ID and utterance ID. Cancel playback on despawn, map
transition, or possession handoff. Revoke delivery when a recipient loses
eligibility. A listener entering range mid-utterance waits for the next line (v1).
The server sends subtitles independently of audio. A headless server never
instantiates an AudioSource. The local mode uses the same playback path with a
loopback worker. Keep NPC mood and relationship history in the dialogue system.

---

## Instrumentation

Use monotonic timestamps per process and log correlation IDs. Report only
same-clock differences across local events. Pass worker stage durations rather
than subtracting worker timestamps from client timestamps.

| Metric | Measurement |
|--------|-------------|
| Queue delay | Worker dequeued minus accepted |
| Worker first PCM | First PCM record minus inference start |
| Synthesis completion | Final PCM minus inference start; separate model loading and voice prep |
| First PCM received | Client first complete PCM record minus client submission |
| First audible-content proxy | Client submission to first above-threshold sample consumed by audio callback. Record its offset in the callback buffer. Use 10 ms sustained RMS above -45 dBFS. Flag leading silence. Label as software estimate. |
| Physical output onset | Optional wired loopback capture on same clock using marker channel and calibrated capture delay. Report separately. |
| RTF | Worker active wall time ÷ final speech duration. Below 1 can sustain playback after startup if chunks arrive steadily. |
| Playback stability | Underrun count, inserted silence ms, dropped/canceled lines |
| Rendering | Per-frame CPU main/render and GPU timing where supported, plus wall frame interval. Report p50/p95/p99. Share over 16.7 and 33.3 ms for 60 FPS target. Unsupported GPU counters are missing values. |
| Load | Worker CPU time and peak RSS, Unity peak RSS, GPU memory/utilization where available. Record sample interval. Label shared-memory metrics by platform. |

Export request and frame CSV plus a JSON summary with: hardware, OS, Unity
version, graphics API, model and worker revisions, resolution, scene settings,
VSync and frame cap, audio device/buffer, voice revision, seed where supported,
concurrency, queue depth, startup prebuffer.

Use Unity FrameTimingManager with frame timing enabled. Deduplicate delayed
timing samples. Capture frame intervals independently. Measure CPU core-equivalents
as worker CPU seconds ÷ elapsed wall seconds. Report total process startup
separately from warm synthesis.

---

## Test plan

**EditMode** fixtures run without a model using a deterministic worker stub:
- Split headers and PCM records across read boundaries
- Multiple records per read
- Malformed offsets/lengths
- Early EOF
- Out-of-order lifecycle events
- Full queue, deadlines, cancellation before and during synthesis
- Absence of duplicate terminal events
- Scheduler never exceeds active limit
- Cancellation on a backend without interruption does not free the active slot early

**PlayMode** fixtures use fixed PCM with known silence and a tone:
- Conversion, prebuffer behavior
- Onset detection within one DSP block of expected offset
- Underrun accounting
- End-of-stream for a short clip
- Source reuse
- Movement of emitter with NPC

With a fake game transport:
- One synthesis per line
- Same-map proximity delivery
- No audio to unauthorized recipients
- Stopping on NPC possession or despawn

Fixtures use an injected monotonic clock for deterministic timeout and queue assertions.

---

## Benchmark runner

After implementation, run correctness tests with the installed Unity executable:

```bash
Unity -batchmode -projectPath /path/to/project -runTests -testPlatform EditMode -testResults editmode.xml
Unity -batchmode -projectPath /path/to/project -runTests -testPlatform PlayMode -testResults playmode.xml
```

Build the benchmark sample as a standalone player with frame timing enabled.
The package must implement the following player flags:

```bash
./NpcTtsBench.app/Contents/MacOS/NpcTtsBench \
  --tts-bench --scenario render-stress --backend cpu \
  --endpoint http://127.0.0.1:8080 --voice dwarf --parallel 1 \
  --out results/cpu-1
```

Repeat with GPU endpoint and `--parallel 2`. Implement `--backend idle` for
identical scene motion with no speech and `--backend playback-only` for
pre-generated PCM. Also run `--scenario empty` to isolate synthesis overhead.

Use a real render device and audio output for performance runs. Keep render
path, resolution, quality, audio settings and power mode fixed. Render benchmark
workload is repeatable with a fixed camera path and request schedule.

For each scenario/backend/concurrency cell: three passes, five warmups per pass,
at least 100 timed requests total. Use a fixed corpus with short, medium, and
long lines and the same cloned voice across backends. In the parallel-2 cell
submit paired requests on the same frame; keep the worker cap at one and label
first and queued requests in results. Use matched idle and playback-only windows
with the same camera phases. Separately run five fresh-process trials for cold
startup. Report request p50/p95/p99 with sample counts, frame p50/p95/p99, and
failures including deadlines or aborted runs. Alternate CPU and GPU trial order.
Never report failed requests as fast successes.

**Suggested initial targets:**
- Warm solo p95 first audible-content proxy ≤ 1 s for short lines
- No underruns
- ≤ 2 ms rise in render-stress p99 frame interval vs idle

Report budget misses even if average FPS looks stable. If the idle scene already
misses 60 FPS, record that separately and compare degradation. Thresholds are
provisional and configurable.

---

## Probe

`Worker/probe_tts.py` is a runnable smoke benchmark against the raw PCM
streaming endpoint. Run it while Unity is closed, then with the same scene
rendering. Reports client-observed first PCM, first sustained non-silent audio,
and complete response time. Unity's player suite is required for audio callback
and frame metrics.

```bash
cd Worker && python main.py --device cpu   # or --device mps
python probe_tts.py http://127.0.0.1:8080/v1/tts/stream
```

---

## Implementation references

- Qwen3-TTS: https://github.com/QwenLM/Qwen3-TTS
- Qwen3-TTS HuggingFace: https://huggingface.co/Qwen/Qwen3-TTS-12Hz-0.6B-Base
- qwen-tts Python library: `pip install qwen-tts`
- Actual models used: `Qwen3-TTS-12Hz-0.6B-Base` (runtime), `Qwen3-TTS-12Hz-1.7B-VoiceDesign` (audition)
- Unity Frame Timing counters
- Unity command-line tests

---

## Measured performance (Apple Silicon, MPS)

| Metric | Value |
|--------|-------|
| Model load time | ~25 s (cold), ~5 s (warm) |
| Warmup synthesis | ~3 s |
| RTF (avg) | **0.58** (faster than real-time) |
| Cloned voice latency | ~1.8 s |
| Default voice latency | ~2.5 s |
| Unit tests | 49 pass, 0 fail |

*Tested with Qwen3-TTS 0.6B Base on M-series Mac, MPS device.*
