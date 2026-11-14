// SPDX-License-Identifier: Apache-2.0
// com.npctts.unity — Integration test: end-to-end synthesis with worker

using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using NpcTts;

namespace NpcTts.Tests.Integration
{
    /// <summary>Integration tests that start the worker and test end-to-end synthesis.</summary>
    public class IntegrationTests
    {
        private GameObject _manager;
        private NpcTtsManager _ttsManager;
        private System.Diagnostics.Process _workerProcess;
        private int _workerPort;
        private bool _workerStarted;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
#if !UNITY_EDITOR
            // Not running in Unity Editor, skip
            return;
#endif

            _workerPort = 8181;
            StartWorker();

            _manager = new GameObject("NpcTtsManager");
            _ttsManager = _manager.AddComponent<NpcTtsManager>();
            UnityEngine.Object.DontDestroyOnLoad(_manager);
            _ttsManager.Endpoint = "http://127.0.0.1:" + _workerPort;

            // Wait for worker health synchronously
            _ttsManager.WaitUntilHealthy(15f);
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
#if UNITY_EDITOR
            if (_manager != null)
                UnityEngine.Object.DestroyImmediate(_manager, true);
            StopWorker();
#endif
        }

        private void StartWorker()
        {
            try
            {
                var projectDir = Application.dataPath;
                while (Directory.Exists(projectDir) && !Directory.Exists(Path.Combine(projectDir, "Packages")))
                    projectDir = Path.Combine(projectDir, "..");

                var workerDir = Path.Combine(projectDir, "Worker");
                var mainPy = Path.Combine(workerDir, "main.py");
                var venvPython = Path.Combine(workerDir, ".venv", "bin", "python");

                if (!File.Exists(mainPy))
                {
                    UnityEngine.Debug.LogWarning("[IntegrationTests] Worker not found, skipping tests");
                    return;
                }

                if (!File.Exists(venvPython))
                    venvPython = "python3";

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = venvPython,
                    Arguments = "\"" + mainPy + "\" --port " + _workerPort + " --device auto --log-level warning",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                _workerProcess = System.Diagnostics.Process.Start(psi);
                if (_workerProcess == null)
                {
                    UnityEngine.Debug.LogWarning("[IntegrationTests] Failed to start worker process");
                    return;
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 30000)
                {
                    if (IsWorkerReady())
                    {
                        _workerStarted = true;
                        UnityEngine.Debug.Log("[IntegrationTests] Worker is ready on port " + _workerPort);
                        return;
                    }
                    System.Threading.Thread.Sleep(500);
                }

                UnityEngine.Debug.LogWarning("[IntegrationTests] Worker did not become ready within 30s");
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("[IntegrationTests] Error starting worker: " + ex.Message);
            }
        }

        private void StopWorker()
        {
            try
            {
                if (_workerProcess != null && !_workerProcess.HasExited)
                {
                    _workerProcess.Kill();
                    _workerProcess.WaitForExit(5000);
                    _workerProcess.Dispose();
                }
                _workerProcess = null;
                _workerStarted = false;
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("[IntegrationTests] Error stopping worker: " + ex.Message);
            }
        }

        private bool IsWorkerReady()
        {
            try
            {
                using (var client = new System.Net.WebClient())
                {
                    var response = client.DownloadString("http://127.0.0.1:" + _workerPort + "/v1/health");
                    return response != null && response.Contains("\"status\"");
                }
            }
            catch
            {
                return false;
            }
        }

        [Test]
        public void HealthCheck_ShouldSucceed()
        {
            if (!_workerStarted)
            {
                Assert.Ignore("Worker not available");
                return;
            }

            var healthy = _ttsManager.WaitUntilHealthy(10f);
            Assert.IsTrue(healthy, "Worker should be healthy");
        }

        [Test]
        public void SpeakSync_HelloWorld_ShouldSucceed()
        {
            if (!_workerStarted)
            {
                Assert.Ignore("Worker not available");
                return;
            }

            var request = new SpeechRequest("int-hello", "int-npc", "default", "Hello world!");
            var handle = _ttsManager.SpeakSync(request);

            Assert.IsNotNull(handle);
            Assert.IsTrue(handle.UtteranceId.StartsWith("int-npc-"), "Utterance ID should start with NPC ID");
            Assert.AreEqual(SpeechStatus.Finished, handle.Status, "Speech should complete successfully");
        }

        [Test]
        public void SpeakSync_LongText_ShouldComplete()
        {
            if (!_workerStarted)
            {
                Assert.Ignore("Worker not available");
                return;
            }

            var text = "The quick brown fox jumps over the lazy dog. " +
                       "This is a longer sentence to test that streaming works properly " +
                       "and that the audio playback completes without errors.";

            var request = new SpeechRequest("int-long", "int-npc-2", "default", text);
            var handle = _ttsManager.SpeakSync(request);

            Assert.IsNotNull(handle);
            Assert.AreEqual(SpeechStatus.Finished, handle.Status, "Long speech should complete");
        }

        [Test]
        public void SpeakSync_Japanese_ShouldWork()
        {
            if (!_workerStarted)
            {
                Assert.Ignore("Worker not available");
                return;
            }

            var request = new SpeechRequest("int-jp", "int-npc-3", "default", "\u3053\u3093\u306b\u3061\u306f", "ja");
            var handle = _ttsManager.SpeakSync(request);

            Assert.IsNotNull(handle);
            Assert.AreEqual(SpeechStatus.Finished, handle.Status, "Japanese speech should complete");
        }

        [Test]
        public void SpeakSync_MultipleUtterances_ShouldComplete()
        {
            if (!_workerStarted)
            {
                Assert.Ignore("Worker not available");
                return;
            }

            var handle1 = _ttsManager.SpeakSync(
                new SpeechRequest("int-q1", "int-npc-4", "default", "First utterance."));
            var handle2 = _ttsManager.SpeakSync(
                new SpeechRequest("int-q2", "int-npc-4", "default", "Second utterance."));

            Assert.AreEqual(SpeechStatus.Finished, handle1.Status, "First utterance should finish");
            Assert.AreEqual(SpeechStatus.Finished, handle2.Status, "Second utterance should finish");
        }

        [Test]
        public void SpeakSync_UnknownVoice_FallsBackToDefault()
        {
            if (!_workerStarted)
            {
                Assert.Ignore("Worker not available");
                return;
            }

            // The /v1/tts/stream endpoint falls back to default voice for unknown voices
            var request = new SpeechRequest("int-voice", "int-npc-6", "nonexistent-voice", "Hello!");
            var handle = _ttsManager.SpeakSync(request);

            Assert.IsNotNull(handle);
            // Stream endpoint falls back to default, so it succeeds
            Assert.AreEqual(SpeechStatus.Finished, handle.Status, "Stream endpoint falls back to default voice");
        }

        [Test]
        public void SpeakSync_EmptyText_ShouldFail()
        {
            if (!_workerStarted)
            {
                Assert.Ignore("Worker not available");
                return;
            }

            var request = new SpeechRequest("int-empty", "int-npc-7", "default", "");
            var handle = _ttsManager.SpeakSync(request);

            Assert.IsNotNull(handle);
            Assert.AreEqual(SpeechStatus.Failed, handle.Status, "Empty text should fail");
        }
    }
}
