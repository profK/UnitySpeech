# Unity Runtime API Reference

Complete API documentation for `com.npctts.unity`, the C# UPM package for NPC speech synthesis.

## Namespace: `NpcTts`

### `NpcTtsManager` : MonoBehaviour

The main entry point for NPC speech synthesis. Add this component to a persistent GameObject in your scene.

#### Inspector Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| **Endpoint** | `string` | `http://127.0.0.1:8080` | Worker HTTP endpoint |
| **Health Interval** | `float` | `10` seconds | Interval between health checks |
| **Prebuffer Ms** | `float` | `120` | Prebuffer duration before starting playback |
| **Buffer Capacity Seconds** | `float` | `5` | PCM buffer capacity |
| **Default Voice ID** | `string` | `default` | Voice used when not specified |
| **Default Language** | `string` | `en` | Language used when not specified |

#### Public Properties

| Property | Type | Description |
|----------|------|-------------|
| `Endpoint` | `string` | Worker endpoint URL (get/set) |
| `DefaultVoiceId` | `string` | Default voice ID (get/set) |
| `DefaultLanguage` | `string` | Default language code (get/set) |
| `IsWorkerHealthy` | `bool` | Whether the worker is currently healthy |
| `WorkerStatus` | `string` | Current status string ("ok", "disconnected", etc.) |
| `Telemetry` | `TelemetryCollector` | Access to the telemetry collector |

#### Events

| Event | Signature | Fired When |
|-------|-----------|------------|
| `OnStarted` | `Action<SpeechHandle>` | Playback begins |
| `OnFinished` | `Action<SpeechHandle>` | Playback completes |
| `OnCancelled` | `Action<SpeechHandle>` | Request is cancelled |
| `OnFailed` | `Action<SpeechHandle, string>` | Request fails with a message |

#### Methods

##### `Speak(SpeechRequest request, CancellationToken cancellation = default) → SpeechHandle`

Primary method for synthesizing speech. Submits a request to the worker and begins playback.

**Parameters:**
- `request` — A `SpeechRequest` describing the utterance
- `cancellation` — Optional `CancellationToken` for programmatic cancellation

**Returns:** A `SpeechHandle` for tracking and cancellation

**Behavior:**
1. Validates the NPC ID and text
2. Checks worker health
3. Creates a `SpeechHandle` with an auto-generated utterance ID
4. Streams PCM from the worker into a per-NPC buffer
5. Begins playback when prebuffer threshold is reached
6. Fires events as the utterance progresses

**Example:**
```csharp
var request = new SpeechRequest(
    utteranceId: "greeting-01",
    npcId: "dwarf-blacksmith",
    voiceId: "default",
    text: "Welcome, traveler!",
    language: "en"
);
var handle = manager.Speak(request);
```

##### `StopNpc(string npcId)`

Stops all active speech for an NPC. Cancels in-progress requests.

**Example:**
```csharp
manager.StopNpc("dwarf-blacksmith");
```

##### `GetActiveHandles(string npcId) → SpeechHandle[]`

Returns all active (non-completed) handles for an NPC.

**Example:**
```csharp
var handles = manager.GetActiveHandles("dwarf-blacksmith");
Debug.Log(handles.Length + " active utterances");
```

##### `CheckHealth() → IEnumerator`

Coroutine that checks worker health. Use with `StartCoroutine`.

**Example:**
```csharp
StartCoroutine(manager.CheckHealth());
// Result available via IsWorkerHealthy and WorkerStatus
```

##### `TryCheckHealth() → bool`

Synchronous health check. Blocks briefly. Useful in test fixtures.

**Returns:** `true` if the worker responded with a healthy status.

##### `WaitUntilHealthy(float timeoutSeconds) → bool`

Blocks until the worker is healthy or the timeout expires.

**Returns:** `true` if the worker became healthy within the timeout.

**Example:**
```csharp
if (manager.WaitUntilHealthy(30f))
    Debug.Log("Worker is ready");
```

##### `SpeakSync(SpeechRequest request) → SpeechHandle`

Synchronous speech synthesis for integration tests. Does not use coroutines.

**Note:** Intended for tests where coroutines do not advance. In gameplay, use `Speak()` instead.

##### `CreateVoice(string voiceId, string language, string transcript, string refAudioPath) → IEnumerator`

Create a cloned voice from reference audio. Use with `StartCoroutine`.

**Example:**
```csharp
StartCoroutine(manager.CreateVoice(
    voiceId: "dwarf",
    language: "en",
    transcript: "Hello there, friend!",
    refAudioPath: "/path/to/reference.wav"
));
```

---

### `SpeechRequest` (struct)

Describes a speech synthesis request.

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `UtteranceId` | `string` | Yes | Unique identifier for this utterance |
| `NpcId` | `string` | Yes | NPC this utterance belongs to |
| `VoiceId` | `string` | Yes | Voice profile to use ("default" for built-in) |
| `Text` | `string` | Yes | Text to synthesize |
| `Language` | `string` | No | Language code (default: "en") |
| `Deadline` | `TimeSpan?` | No | Optional deadline for the request |

**Constructor:**
```csharp
new SpeechRequest(utteranceId, npcId, voiceId, text, language = "en", deadline = null)
```

**Example:**
```csharp
var request = new SpeechRequest(
    "line-001",
    "dwarf-blacksmith",
    "default",
    "Five silver pieces for the hammer.",
    "en"
);
```

---

### `SpeechHandle` : IDisposable

Handle for tracking and controlling a speech utterance.

| Property | Type | Description |
|----------|------|-------------|
| `UtteranceId` | `string` | The utterance identifier |
| `NpcId` | `string` | The NPC this belongs to |
| `Status` | `SpeechStatus` | Current lifecycle status |
| `TerminalReason` | `TerminalReason` | Failure reason (if failed) |
| `IsCompleted` | `bool` | Whether the utterance reached a terminal state |

**Methods:**
- `Cancel()` — Cancel this utterance. Idempotent.
- `Dispose()` — Cleanup. Calls `Cancel()`.

---

### `SpeechStatus` (enum)

Lifecycle status of a speech utterance.

| Value | Description |
|-------|-------------|
| `Pending` | Request created, waiting to submit |
| `Queued` | Submitted to the worker's queue |
| `Active` | Worker is synthesizing |
| `Playing` | Audio is playing back |
| `Finished` | Playback completed successfully |
| `Cancelled` | Request was cancelled |
| `Failed` | Request failed (check `TerminalReason`) |

---

### `TerminalReason` (enum)

Failure reasons for speech requests.

| Value | Description |
|-------|-------------|
| `Ok` | Success |
| `UnknownVoice` | Voice ID not found in the catalog |
| `QueueOverload` | Worker queue is full (429) |
| `WorkerUnavailable` | Worker not reachable or model not loaded |
| `Timeout` | Request deadline expired |
| `ProtocolError` | Malformed response from worker |
| `PlaybackFailure` | Audio playback failed |
| `InferenceError` | Model inference error |

---

## Namespace: `NpcTts.Protocol`

### `StreamParser`

Parses a complete binary protocol response stream.

| Property | Type | Description |
|----------|------|-------------|
| `IsComplete` | `bool` | Whether the stream is complete |
| `Start` | `StartRecord?` | Parsed START record |
| `End` | `EndRecord?` | Parsed END record |
| `Error` | `ErrorRecord?` | Error record (if any) |
| `TotalSamples` | `int` | Total PCM samples received |

**Methods:**
- `Feed(byte[] data) → bool` — Feed bytes and return `true` when complete
- `GetPcmBytes() → byte[]` — Get all assembled PCM bytes
- `Reset()` — Reset parser state

### `RecordReader`

Low-level record reader that tolerates arbitrary read boundaries.

**Methods:**
- `Feed(byte[] data) → List<RecordEnvelope>` — Feed bytes, return complete records
- `BufferedBytes → int` — Number of buffered but incomplete bytes
- `Clear()` — Clear the internal buffer

---

## Namespace: `NpcTts.Audio`

### `PcmBuffer` : IDisposable

Lock-free single-producer/single-consumer circular buffer for PCM16 audio.

**Constructor:**
```csharp
new PcmBuffer(capacitySeconds = 5)  // 5 seconds of 24 kHz mono audio
```

**Methods:**
- `Write(byte[] data, int offset, int byteCount) → int` — Write PCM16 bytes
- `Write(short[] samples, int offset, int count) → int` — Write short array directly
- `Read(short[] output, int offset, int count) → int` — Read samples (zero-fill on underrun)
- `Clear()` — Reset buffer positions
- `Dispose()` — Cleanup

**Properties:**
- `Capacity` — Total sample capacity
- `Count` — Number of available samples
- `Available` — Number of free slots

**Static:**
- `ToSamples(byte[] data) → short[]` — Convert PCM16 bytes to a short array

### `UtterancePlayer` : IDisposable

Handles spatial audio playback for a single NPC utterance. Creates a streaming AudioClip attached to an AudioSource positioned at the NPC's transform.

**Features:**
- Spatial audio with `minDistance` (1m) and `maxDistance` (50m)
- Prebuffering before playback start
- Underrun tracking
- RMS-based onset detection
- Automatic cleanup on disposal

### `PlaybackEvent` (enum)

Events emitted during playback.

| Value | Description |
|-------|-------------|
| `Started` | Audio playback began |
| `OnsetDetected` | First sustained audio content detected |
| `Finished` | Buffer drained after end-of-stream |
| `Stopped` | Explicitly stopped |

### `PlaybackStats` (struct)

| Field | Type | Description |
|-------|------|-------------|
| `UnderrunCount` | `int` | Number of buffer underruns |
| `InsertedSilenceMs` | `double` | Total silence inserted due to underruns |
| `TotalSamplesPlayed` | `int` | Total samples consumed |
| `TotalSamplesReceived` | `int` | Total samples written |

---

## Namespace: `NpcTts.Telemetry`

### `TelemetryCollector`

Collects and reports speech synthesis metrics.

**Methods:**
- `RecordSynthesis(string utteranceId, EndRecord end)` — Record a synthesis result
- `RecordOnset(string utteranceId)` — Record onset detection time
- `GetMetrics() → SynthesisMetric[]` — Get all recorded metrics
- `Clear()` — Clear all metrics
- `Export() → TelemetrySummary` — Export a summary with percentiles

**Static:**
- `ComputePercentiles(double[] values) → Percentiles` — Compute p50/p95/p99

### `SynthesisMetric` (struct)

| Field | Type | Description |
|-------|------|-------------|
| `UtteranceId` | `string` | Utterance identifier |
| `QueueDelayMs` | `double` | Time waiting in queue |
| `FirstPcmMs` | `double` | Time to first PCM data |
| `SynthesisMs` | `double` | Worker inference time |
| `WallMs` | `double` | Total wall-clock time |
| `SilenceHeadMs` | `double` | Leading silence from model |
| `TotalSamples` | `int` | Total PCM samples |

### `Percentiles` (struct)

| Field | Type | Description |
|-------|------|-------------|
| `P50` | `double` | 50th percentile (median) |
| `P95` | `double` | 95th percentile |
| `P99` | `double` | 99th percentile |
| `Count` | `int` | Number of samples |

### `TelemetrySummary` (struct)

| Field | Type | Description |
|-------|------|-------------|
| `SynthesisMs` | `Percentiles` | Synthesis time percentiles |
| `RequestCount` | `int` | Total request count |

---

## Namespace: `NpcTts.Benchmark`

### `BenchmarkRunner` : MonoBehaviour

Automated performance benchmarking. Add to a GameObject and call `Run()`.

**Inspector Properties:**
- `Iterations` — Number of iterations per utterance
- `Delay Between Iterations` — Seconds between each iteration
- `Results Text` — Optional UI Text field for displaying results

**Public:**
- `IsRunning` — Whether a benchmark is in progress
- `IsCompleted` — Whether the last benchmark completed
- `Results` — Collected `BenchmarkResult` entries
- `Summary` — Aggregated `BenchmarkSummary`

**Methods:**
- `Run()` — Start the benchmark suite
- `Cancel()` — Cancel a running benchmark

### `BenchmarkResult` (struct)

Per-iteration result with timing and status.

### `BenchmarkSummary` (struct)

Aggregated results with percentiles.
