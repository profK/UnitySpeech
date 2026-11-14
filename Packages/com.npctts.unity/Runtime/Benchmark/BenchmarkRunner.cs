// SPDX-License-Identifier: Apache-2.0
// com.npctts.unity — Benchmark runner for TTS synthesis performance

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using NpcTts;
using NpcTts.Telemetry;

namespace NpcTts.Benchmark
{
    /// <summary>
    /// Benchmarks TTS synthesis performance.
    /// 
    /// Runs multiple utterances and measures:
    /// - Synthesis time (worker response time)
    /// - Playback time (audio duration)
    /// - Total latency (request to onset)
    /// 
    /// Reports p50, p95, p99 statistics.
    /// </summary>
    public class BenchmarkRunner : MonoBehaviour
    {
        [Header("Configuration")]
        [Tooltip("Number of iterations per utterance")]
        [SerializeField] private int _iterations = 5;

        [Tooltip("Delay between iterations (seconds)")]
        [SerializeField] private float _delayBetweenIterations = 1f;

        [Header("Display")]
        [Tooltip("Text field to show results (optional)")]
        [SerializeField] private Text _resultsText;

        // Test utterances
        private static readonly SpeechRequest[] k_Utterances = new[]
        {
            new SpeechRequest("bench-short", "bench-npc", "default", "Hello world!", "en"),
            new SpeechRequest("bench-medium", "bench-npc", "default",
                "The quick brown fox jumps over the lazy dog.", "en"),
            new SpeechRequest("bench-long", "bench-npc", "default",
                "Once upon a time in a land far away, there lived a dwarf who spoke " +
                "with a deep, gravelly voice. He worked in the mountains, forging weapons " +
                "and telling tales of ancient battles. His words echoed through the caverns " +
                "like thunder on a cloudy day.", "en"),
            new SpeechRequest("bench-jp", "bench-npc", "default",
                "昔々ある国に、勇者と悪魔王が住んでいました。彼らは毎日戦い、世界を守っていました。", "ja"),
        };

        // Results
        private List<BenchmarkResult> _results = new List<BenchmarkResult>();
        private System.Diagnostics.Stopwatch _totalTimer = new System.Diagnostics.Stopwatch();
        private bool _isRunning;
        private bool _completed;

        /// <summary>Whether the benchmark is currently running.</summary>
        public bool IsRunning => _isRunning;

        /// <summary>Whether the benchmark has completed.</summary>
        public bool IsCompleted => _completed;

        /// <summary>Collected results.</summary>
        public List<BenchmarkResult> Results => _results;

        /// <summary>Summary statistics.</summary>
        public BenchmarkSummary Summary { get; private set; }

        private NpcTtsManager _manager;

        private void Awake()
        {
            _manager = UnityEngine.Object.FindAnyObjectByType<NpcTtsManager>();
            if (_manager == null)
            {
                var go = new GameObject("NpcTtsManager");
                _manager = go.AddComponent<NpcTtsManager>();
            }
        }

        /// <summary>Start the benchmark.</summary>
        public void Run()
        {
            if (_isRunning)
                return;

            _results.Clear();
            _isRunning = true;
            _completed = false;
            _totalTimer.Restart();

            StartCoroutine(RunBenchmark());
        }

        private IEnumerator RunBenchmark()
        {
            // Wait for worker to be healthy
            yield return WaitForWorkerHealthy(30f);

            if (!_manager.IsWorkerHealthy)
            {
                Debug.LogError("[Benchmark] Worker not available. Aborting.");
                _completed = true;
                _isRunning = false;
                yield break;
            }

            // Run each utterance for the configured number of iterations
            var index = 0;
            foreach (var utterance in k_Utterances)
            {
                for (var iter = 0; iter < _iterations; iter++)
                {
                    if (!_isRunning)
                        yield break; // Cancelled

                    var currentUtterance = utterance;
                    var currentIter = iter;
                    yield return RunSingleUtteranceAsync(currentUtterance, currentIter, OnBenchmarkResult);

                    index++;
                    Debug.Log("[Benchmark] Completed " + index + "/" + (k_Utterances.Length * _iterations));

                    if (iter < _iterations - 1 || index < k_Utterances.Length * _iterations)
                    {
                        yield return new WaitForSeconds(_delayBetweenIterations);
                    }
                }
            }

            // Compute summary
            _totalTimer.Stop();
            Summary = ComputeSummary();
            _completed = true;
            _isRunning = false;

            // Display results
            OnBenchmarkCompleted(Summary);
            Debug.Log("[Benchmark] " + Summary);
        }

        private IEnumerator WaitForWorkerHealthy(float timeout)
        {
            var elapsed = 0f;
            while (elapsed < timeout)
            {
                if (_manager.IsWorkerHealthy)
                    yield break;

                yield return new WaitForSeconds(0.5f);
                elapsed += 0.5f;
            }
        }

        private void OnBenchmarkResult(BenchmarkResult result)
        {
            _results.Add(result);
        }

        private IEnumerator RunSingleUtteranceAsync(SpeechRequest request, int iteration, System.Action<BenchmarkResult> callback)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var handle = _manager.Speak(request);
            var submitTime = sw.ElapsedMilliseconds;

            // Wait for completion
            yield return new WaitUntil(() => handle.IsCompleted);
            var totalTime = sw.ElapsedMilliseconds;

            var result = new BenchmarkResult(
                utteranceId: request.UtteranceId,
                text: request.Text,
                language: request.Language,
                iteration: iteration,
                submitTimeMs: submitTime,
                totalTimeMs: totalTime,
                status: handle.Status,
                terminalReason: handle.TerminalReason);

            callback(result);
        }

        private BenchmarkSummary ComputeSummary()
        {
            var totalTimes = new double[_results.Count];
            var submitTimes = new double[_results.Count];
            for (var i = 0; i < _results.Count; i++)
            {
                totalTimes[i] = _results[i].TotalTimeMs;
                submitTimes[i] = _results[i].SubmitTimeMs;
            }

            return new BenchmarkSummary(
                totalIterations: _results.Count,
                totalTimeMs: _totalTimer.ElapsedMilliseconds,
                totalLatencyPercentiles: TelemetryCollector.ComputePercentiles(totalTimes),
                submitLatencyPercentiles: TelemetryCollector.ComputePercentiles(submitTimes),
                passedCount: _results.FindAll(r => r.Status == SpeechStatus.Finished).Count);
        }

        private void OnBenchmarkCompleted(BenchmarkSummary summary)
        {
            if (_resultsText != null)
                _resultsText.text = summary.ToString();

            // Write results to file
            var path = Path.Combine(Application.persistentDataPath, "benchmark-results.txt");
            File.WriteAllText(path, summary.ToString());
            Debug.Log("[Benchmark] Results saved to: " + path);
        }

        /// <summary>Cancel a running benchmark.</summary>
        public void Cancel()
        {
            _isRunning = false;
        }

        private void OnDestroy()
        {
            Cancel();
        }
    }

    /// <summary>Result of a single benchmark iteration.</summary>
    public struct BenchmarkResult
    {
        public readonly string UtteranceId;
        public readonly string Text;
        public readonly string Language;
        public readonly int Iteration;
        public readonly double SubmitTimeMs;
        public readonly double TotalTimeMs;
        public readonly SpeechStatus Status;
        public readonly TerminalReason TerminalReason;

        public BenchmarkResult(
            string utteranceId,
            string text,
            string language,
            int iteration,
            double submitTimeMs,
            double totalTimeMs,
            SpeechStatus status,
            TerminalReason terminalReason)
        {
            UtteranceId = utteranceId;
            Text = text;
            Language = language;
            Iteration = iteration;
            SubmitTimeMs = submitTimeMs;
            TotalTimeMs = totalTimeMs;
            Status = status;
            TerminalReason = terminalReason;
        }

        public override string ToString()
        {
            return $"[{Language}] \"{Text.Substring(0, Math.Min(40, Text.Length))}...\" " +
                   "submit=" + SubmitTimeMs + "ms total=" + TotalTimeMs + "ms " +
                   "status=" + Status;
        }
    }

    /// <summary>Summary of benchmark results.</summary>
    public struct BenchmarkSummary
    {
        public readonly int TotalIterations;
        public readonly double TotalTimeMs;
        public readonly Percentiles TotalLatencyPercentiles;
        public readonly Percentiles SubmitLatencyPercentiles;
        public readonly int PassedCount;

        public BenchmarkSummary(
            int totalIterations,
            double totalTimeMs,
            Percentiles totalLatencyPercentiles,
            Percentiles submitLatencyPercentiles,
            int passedCount)
        {
            TotalIterations = totalIterations;
            TotalTimeMs = totalTimeMs;
            TotalLatencyPercentiles = totalLatencyPercentiles;
            SubmitLatencyPercentiles = submitLatencyPercentiles;
            PassedCount = passedCount;
        }

        public override string ToString()
        {
            return string.Format(
                "=== Benchmark Results ===\n" +
                "Iterations: {0}\n" +
                "Passed: {1}/{2}\n" +
                "Total Wall Time: {3:F0}ms\n\n" +
                "Total Latency:\n" +
                "  p50={4:F1}ms p95={5:F1}ms p99={6:F1}ms\n\n" +
                "Submit Latency:\n" +
                "  p50={7:F1}ms p95={8:F1}ms p99={9:F1}ms",
                TotalIterations,
                PassedCount,
                TotalIterations,
                TotalTimeMs,
                TotalLatencyPercentiles.P50,
                TotalLatencyPercentiles.P95,
                TotalLatencyPercentiles.P99,
                SubmitLatencyPercentiles.P50,
                SubmitLatencyPercentiles.P95,
                SubmitLatencyPercentiles.P99);
        }
    }
}
