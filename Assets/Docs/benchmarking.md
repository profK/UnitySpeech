# Benchmarking

Measure and compare speech synthesis performance across configurations.

## Benchmark Runner (In-Unity)

The `BenchmarkRunner` MonoBehaviour automates performance measurement.

### Setup

1. Add `BenchmarkRunner` to a GameObject in your scene
2. (Optional) Assign a UI Text component to the `Results Text` field
3. Configure iterations and delays in the Inspector

### Running

```csharp
var runner = FindObjectOfType<BenchmarkRunner>();
runner.Run();

// Later, check results:
if (runner.IsCompleted)
{
    Debug.Log(runner.Summary);
    foreach (var result in runner.Results)
        Debug.Log(result);
}
```

### Test Utterances

The runner tests four utterances across lengths and languages:

| ID | Type | Language | Description |
|----|------|----------|-------------|
| `bench-short` | Short | English | "Hello world!" |
| `bench-medium` | Medium | English | "The quick brown fox..." |
| `bench-long` | Long | English | Paragraph of dwarven dialogue |
| `bench-jp` | Medium | Japanese | 勇者と悪魔王 tale |

### Output

The runner writes results to `Application.persistentDataPath/benchmark-results.txt`:

```
=== Benchmark Results ===
Iterations: 20
Passed: 20/20
Total Wall Time: 45230ms

Total Latency:
  p50=1850.3ms p95=2340.1ms p99=2510.7ms

Submit Latency:
  p50=1.2ms p95=3.5ms p99=5.1ms
```

## Probe Tool (Standalone)

Run `probe_tts.py` for lightweight, outside-Unity benchmarks:

```bash
cd Worker
python probe_tts.py http://127.0.0.1:8080/v1/tts/stream
```

### Interpreting Results

| Column | Description | Target |
|--------|-------------|--------|
| `first_pcm_ms` | Time to first bytes | < 500ms |
| `onset_received_ms` | Time to audible content | < 1500ms |
| `complete_ms` | Total response time | Varies by text length |
| `audio_seconds` | Duration of output audio | — |
| `rtf_wall` | Real-time factor | < 1.0 (faster than real-time) |

**RTF (Real-Time Factor):**
- RTF < 1.0: Synthesis is faster than real-time (audio completes before it could play)
- RTF = 1.0: Synthesis matches real-time
- RTF > 1.0: Synthesis is slower than real-time (may cause buffering)

**Target: Warm solo p95 first-audible-content proxy ≤ 1s for short lines**

## TelemetryCollector API

For custom benchmarking, use the built-in telemetry:

```csharp
var manager = FindObjectOfType<NpcTtsManager>();
var collector = manager.Telemetry;

// Run some utterances...
manager.Speak(request1);
manager.Speak(request2);

// Export results
var summary = collector.Export();
Debug.Log(summary);  // "Requests: 2, Synthesis: p50=1200.0 p95=1500.0 p99=1800.0 n=2"

// Individual metrics
foreach (var metric in collector.GetMetrics())
{
    Debug.Log($"{metric.UtteranceId}: {metric.SynthesisMs}ms inference, {metric.WallMs}ms wall");
}
```

## Measured Performance

| Metric | Value | Hardware |
|--------|-------|----------|
| Model load (cold) | ~25s | M-series Mac, MPS |
| Model load (warm) | ~5s | M-series Mac, MPS |
| Warmup synthesis | ~3s | First utterance after load |
| RTF (avg) | **0.58** | Faster than real-time |
| Cloned voice latency | ~1.8s | First utterance with cloned voice |
| Default voice latency | ~2.5s | First utterance with default |

## Benchmarking Best Practices

### Controlled Variables

- **Keep render path, resolution, quality, audio settings, and power mode fixed**
- **Use the same cloned voice across backends** for fair comparison
- **Run three passes with five warmups per pass**, at least 100 timed requests total
- **Alternate CPU and GPU trial order** to avoid thermal throttling bias

### Isolate Overhead

- Run `--scenario empty` to measure synthesis overhead without rendering
- Run `--scenario render-stress` with `--backend idle` for render-only baseline
- Run `--scenario render-stress` with `--backend playback-only` for playback-only baseline

### Reporting

- Report p50/p95/p99 with sample counts
- Include hardware, OS, Unity version, graphics API, and model revision
- Label shared-memory metrics by platform
- Report budget misses even if average FPS looks stable

## Suggested Targets

| Metric | Target |
|--------|--------|
| Warm solo p95 first audible-content proxy | ≤ 1s for short lines |
| Underruns | 0 |
| Render-stress p99 frame interval vs idle | ≤ 2ms rise |
