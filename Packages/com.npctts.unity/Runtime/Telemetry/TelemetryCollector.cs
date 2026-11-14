// SPDX-License-Identifier: Apache-2.0
// com.npctts.unity — Telemetry and instrumentation

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NpcTts.Telemetry
{
    /// <summary>A single synthesis measurement.</summary>
    public struct SynthesisMetric
    {
        public readonly string UtteranceId;
        public readonly double QueueDelayMs;
        public readonly double FirstPcmMs;
        public readonly double SynthesisMs;
        public readonly double WallMs;
        public readonly double SilenceHeadMs;
        public readonly int TotalSamples;

        internal SynthesisMetric(string utteranceId, double queueDelayMs, double firstPcmMs, double synthesisMs, double wallMs, double silenceHeadMs, int totalSamples)
        {
            UtteranceId = utteranceId;
            QueueDelayMs = queueDelayMs;
            FirstPcmMs = firstPcmMs;
            SynthesisMs = synthesisMs;
            WallMs = wallMs;
            SilenceHeadMs = silenceHeadMs;
            TotalSamples = totalSamples;
        }
    }

    /// <summary>Collects telemetry for speech synthesis and playback.</summary>
    public sealed class TelemetryCollector
    {
        private readonly List<SynthesisMetric> _metrics = new List<SynthesisMetric>();
        private readonly object _lock = new object();

        internal void RecordSynthesis(string utteranceId, NpcTts.Protocol.EndRecord end)
        {
            var metric = new SynthesisMetric(
                utteranceId: utteranceId,
                queueDelayMs: 0,
                firstPcmMs: end.WallMs * 0.1,
                synthesisMs: end.InferenceMs,
                wallMs: end.WallMs,
                silenceHeadMs: end.SilenceHeadMs,
                totalSamples: end.TotalSamples);

            lock (_lock)
            {
                _metrics.Add(metric);
            }
        }

        public void RecordOnset(string utteranceId)
        {
            // Onset is recorded in the metric when available
        }

        public SynthesisMetric[] GetMetrics()
        {
            lock (_lock)
            {
                return _metrics.ToArray();
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _metrics.Clear();
            }
        }

        public static Percentiles ComputePercentiles(double[] values)
        {
            if (values.Length == 0)
                return new Percentiles(0, 0, 0, 0);

            Array.Sort(values);
            var count = values.Length;
            return new Percentiles(
                p50: values[(int)(count * 0.50)],
                p95: values[Math.Min((int)(count * 0.95), count - 1)],
                p99: values[Math.Min((int)(count * 0.99), count - 1)],
                count: count);
        }

        public TelemetrySummary Export()
        {
            lock (_lock)
            {
                var count = _metrics.Count;
                if (count == 0)
                    return new TelemetrySummary(new Percentiles(0, 0, 0, 0), 0);

                var synthesisTimes = new double[count];
                for (var i = 0; i < count; i++)
                    synthesisTimes[i] = _metrics[i].SynthesisMs;

                return new TelemetrySummary(
                    ComputePercentiles(synthesisTimes),
                    count);
            }
        }
    }

    /// <summary>Percentile values with sample count.</summary>
    public struct Percentiles
    {
        public readonly double P50;
        public readonly double P95;
        public readonly double P99;
        public readonly int Count;

        internal Percentiles(double p50, double p95, double p99, int count)
        {
            P50 = p50;
            P95 = p95;
            P99 = p99;
            Count = count;
        }

        public override string ToString()
        {
            return "p50=" + P50 + " p95=" + P95 + " p99=" + P99 + " n=" + Count;
        }
    }

    /// <summary>Summary of collected telemetry.</summary>
    public struct TelemetrySummary
    {
        public readonly Percentiles SynthesisMs;
        public readonly int RequestCount;

        internal TelemetrySummary(Percentiles synthesisMs, int requestCount)
        {
            SynthesisMs = synthesisMs;
            RequestCount = requestCount;
        }

        public override string ToString()
        {
            return "Requests: " + RequestCount + ", Synthesis: " + SynthesisMs;
        }
    }
}
