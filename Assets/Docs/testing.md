# Testing

The project includes comprehensive tests for both the Python worker and the Unity runtime.

## Python Worker Tests

Run with pytest from the `Worker/` directory:

```bash
cd Worker
source .venv/bin/activate
python -m pytest tests/ -v
```

### Test Suite Coverage

| Module | File | Tests | What It Tests |
|--------|------|-------|---------------|
| Protocol | `test_protocol.py` | 16 | Record encoding/decoding, boundary tolerance, edge cases |
| Queue | `test_request_queue.py` | 17 | FIFO ordering, capacity, cancellation, deadlines |
| Voice Catalog | `test_voice_catalog.py` | 12 | Create, list, persist, cross-model isolation |

**Total: 49 tests, 0 failures**

### Protocol Tests

Verify the binary protocol encoder/decoder handles:
- START, PCM, END, ERROR record roundtrips
- Empty and Unicode voice IDs
- Headers split across read boundaries (byte by byte)
- Payloads split across two reads
- Multiple records in a single read
- Trailing partial records (buffered, not lost)
- Oversized record rejection (16 MB guard)
- Early EOF with buffered data preservation

### Queue Tests

Verify the request queue manager:
- First request occupies the active slot
- Subsequent requests queue FIFO
- Requests beyond capacity are rejected (429)
- Cancellation before synthesis frees the slot
- Cancellation during synthesis marks "draining"
- Already-finished requests cannot be cancelled
- Expired deadlines are rejected on submit
- Expired queued requests are skipped on promotion
- Concurrent submission from multiple threads (thread safety)

### Voice Catalog Tests

Verify voice profile management:
- Create profiles with reference audio
- Revision bumps on update
- Persist across reloads
- Cross-model isolation (0.6B profiles invisible to 1.7B catalog)
- Removal preserves reference assets
- Hash computation and comparison

## Unity Runtime Tests

### EditMode Tests

Run without a Play Mode scene. Uses a deterministic stub — no model needed.

**From Unity Editor:**
1. Open **Window → General → Test Runner**
2. Click **EditMode** → **Run All**

**From Command Line:**
```bash
Unity -batchmode -projectPath /path/to/UnitySpeech \
  -runTests -testPlatform EditMode -testResults editmode.xml
```

**Test: `ProtocolTests`**
- START record roundtrip
- PCM record roundtrip
- END record roundtrip
- ERROR record roundtrip
- Header split across reads
- Multiple records in one read
- Oversized record rejection
- Complete stream parsing (START → PCM → END)
- Error record handling

### PlayMode Tests

Run in Play Mode with a live worker. Tests start the worker automatically.

**From Unity Editor:**
1. Open **Window → General → Test Runner**
2. Click **PlayMode** → **Run All**

**From Command Line:**
```bash
Unity -batchmode -projectPath /path/to/UnitySpeech \
  -runTests -testPlatform PlayMode -testResults playmode.xml
```

**Test: `AudioPlaybackTests`**
- Invalid NPC ID fails gracefully
- Empty text fails gracefully
- PCM buffer write and read (correct sample values)
- PCM buffer underrun fills with silence
- PCM buffer wraparound (circular buffer edge)
- PCM `ToSamples` conversion
- `StopNpc` with no active handles (no error)

**Test: `SpeechRequestTests`**
- Constructor validates required fields (throws on null)
- Default language ("en")
- Custom language ("ja")
- Optional deadline

**Test: `SpeechHandleTests`**
- Initial state is Pending, not completed
- Failed initial state with terminal reason
- Cancel is idempotent
- SetStatus marks terminal states as completed

**Test: `IntegrationTests`**
- Health check succeeds
- HelloWorld synthesis via `SpeakSync`
- Long text synthesis completes
- Japanese text synthesis works
- Multiple utterances in sequence
- Unknown voice falls back to default (stream endpoint)
- Empty text fails

## Probe Tool

`Worker/probe_tts.py` is a standalone smoke benchmark against the raw PCM endpoint.

```bash
cd Worker
python probe_tts.py http://127.0.0.1:8080/v1/tts/stream
```

**Output:** CSV to stdout with columns:
- `trial` — Trial number (first 3 are warmup)
- `first_pcm_ms` — Time to first PCM bytes
- `onset_received_ms` — Time to first non-silent audio
- `complete_ms` — Total response time
- `audio_seconds` — Duration of synthesized audio
- `rtf_wall` — Real-time factor (wall time / audio duration)

**Usage Tips:**
- Run with Unity closed first (isolated baseline)
- Run with the same scene rendering (real-world load)
- Compare RTF values between runs
- Lower is better: RTF < 1.0 means faster than real-time

## Writing Tests

### EditMode Protocol Tests

Use `RecordReader` and `StreamParser` with hand-crafted byte arrays. No worker needed.

```csharp
[Test]
public void MyProtocolTest()
{
    var reader = new RecordReader();
    var records = reader.Feed(myBytes);
    Assert.AreEqual(1, records.Count);
}
```

### PlayMode Audio Tests

Create an `NpcTtsManager` in `OneTimeSetUp`. Use `SpeakSync` for synchronous tests.

```csharp
[OneTimeSetUp]
public void SetUp()
{
    _manager = new GameObject("NpcTtsManager");
    _ttsManager = _manager.AddComponent<NpcTtsManager>();
}

[Test]
public void MySpeechTest()
{
    var request = new SpeechRequest("test", "npc", "default", "Hello!");
    var handle = _ttsManager.SpeakSync(request);
    Assert.AreEqual(SpeechStatus.Finished, handle.Status);
}
```

## Continuous Integration

The `.xml` result files (`editmode.xml`, `playmode-*.xml`) can be consumed by CI tools like Jenkins, GitHub Actions, or Azure DevOps.

```bash
# Full test run
Unity -batchmode -projectPath . \
  -runTests -testPlatform EditMode -testResults editmode.xml \
  -runTests -testPlatform PlayMode -testResults playmode.xml \
  -quit
```
