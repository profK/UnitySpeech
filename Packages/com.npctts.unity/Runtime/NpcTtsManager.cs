// SPDX-License-Identifier: Apache-2.0
// com.npctts.unity — Main manager for NPC speech synthesis

using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using NpcTts.Audio;
using NpcTts.Networking;
using NpcTts.Telemetry;

namespace NpcTts
{
    /// <summary>
    /// Main entry point for NPC speech synthesis.
    /// </summary>
    public sealed class NpcTtsManager : MonoBehaviour
    {
        [Header("Worker Connection")]
        [Tooltip("Worker HTTP endpoint (e.g. http://127.0.0.1:8080)")]
        [SerializeField] private string _endpoint = "http://127.0.0.1:8080";

        [Tooltip("Health check interval in seconds")]
        [SerializeField] private float _healthInterval = 10f;

        [Header("Playback")]
        [Tooltip("Prebuffer duration in ms before starting playback")]
        [SerializeField] private float _prebufferMs = 120f;

        [Tooltip("PCM buffer capacity in seconds")]
        [SerializeField] private float _bufferCapacitySeconds = 5f;

        [Header("Voice")]
        [Tooltip("Default voice ID")]
        [SerializeField] private string _defaultVoiceId = "default";

        [Tooltip("Default language")]
        [SerializeField] private string _defaultLanguage = "en";

        /// <summary>Worker endpoint URL.</summary>
        public string Endpoint { get => _endpoint; set => _endpoint = value; }

        /// <summary>Default voice ID.</summary>
        public string DefaultVoiceId { get => _defaultVoiceId; set => _defaultVoiceId = value; }

        /// <summary>Default language.</summary>
        public string DefaultLanguage { get => _defaultLanguage; set => _defaultLanguage = value; }

        // Internal state
        private WorkerClient _client;
        private readonly Dictionary<string, List<SpeechHandle>> _activeHandles = new();
        private readonly TelemetryCollector _telemetry = new();
        private readonly Dictionary<string, PcmBuffer> _npcBuffers = new();

        private int _utteranceCounter;
        private Coroutine _healthCheckCoroutine;

        /// <summary>Events fired on the main thread.</summary>
        public event Action<SpeechHandle> OnStarted;
        public event Action<SpeechHandle> OnFinished;
        public event Action<SpeechHandle> OnCancelled;
        public event Action<SpeechHandle, string> OnFailed;

        /// <summary>Telemetry collector for benchmarking.</summary>
        public TelemetryCollector Telemetry => _telemetry;

        /// <summary>Whether the worker is healthy.</summary>
        public bool IsWorkerHealthy { get; private set; }

        /// <summary>Current worker status string.</summary>
        public string WorkerStatus { get; private set; } = "disconnected";

        private void Awake()
        {
            _client = gameObject.AddComponent<WorkerClient>();
            if (!string.IsNullOrEmpty(_endpoint))
                _client.Endpoint = _endpoint;

            _healthCheckCoroutine = StartCoroutine(PingWorker());
        }

        private void OnDestroy()
        {
            if (_healthCheckCoroutine != null)
                StopCoroutine(_healthCheckCoroutine);

            foreach (var npcId in _activeHandles.Keys)
                StopNpc(npcId);

            foreach (var buffer in _npcBuffers.Values)
                buffer.Dispose();
            _npcBuffers.Clear();
        }

        /// <summary>Speak text for an NPC.</summary>
        public SpeechHandle Speak(SpeechRequest request, CancellationToken cancellation = default)
        {
            if (string.IsNullOrEmpty(request.NpcId))
                return CreateFailedHandle(request, TerminalReason.PlaybackFailure, "NPC ID is required");

            if (string.IsNullOrEmpty(request.Text))
                return CreateFailedHandle(request, TerminalReason.InferenceError, "Text is required");

            if (!IsWorkerHealthy)
            {
                Debug.LogWarning($"[NpcTts] Worker unavailable: {WorkerStatus}");
                return CreateFailedHandle(request, TerminalReason.WorkerUnavailable, WorkerStatus);
            }

            var utteranceId = $"{request.NpcId}-{++_utteranceCounter}";
            var handle = new SpeechHandle(utteranceId, request.NpcId);

            lock (_activeHandles)
            {
                if (!_activeHandles.ContainsKey(request.NpcId))
                    _activeHandles[request.NpcId] = new List<SpeechHandle>();
                _activeHandles[request.NpcId].Add(handle);
            }

            PcmBuffer buffer;
            lock (_npcBuffers)
            {
                if (!_npcBuffers.TryGetValue(request.NpcId, out buffer))
                {
                    buffer = new PcmBuffer((int)_bufferCapacitySeconds);
                    _npcBuffers[request.NpcId] = buffer;
                }
            }

            StartCoroutine(ProcessSpeech(request, handle, buffer));

            return handle;
        }

        /// <summary>Stop all active speech for an NPC.</summary>
        public void StopNpc(string npcId)
        {
            List<SpeechHandle> handles;
            lock (_activeHandles)
            {
                if (!_activeHandles.TryGetValue(npcId, out handles))
                    return;
            }

            foreach (var handle in handles)
            {
                if (!handle.IsCompleted)
                {
                    handle.Cancel();
                    handle.SetStatus(SpeechStatus.Cancelled);
                    OnCancelled?.Invoke(handle);
                }
            }

            lock (_activeHandles)
            {
                if (_activeHandles.TryGetValue(npcId, out handles))
                {
                    handles.RemoveAll(h => h.IsCompleted);
                    if (handles.Count == 0)
                        _activeHandles.Remove(npcId);
                }
            }

            lock (_npcBuffers)
            {
                if (_npcBuffers.ContainsKey(npcId))
                {
                    var buf = _npcBuffers[npcId];
                    buf.Clear();
                    // Check if we should remove the buffer
                    List<SpeechHandle> npcHandles;
                    if (_activeHandles.TryGetValue(npcId, out npcHandles) && npcHandles.Count == 0)
                    {
                        buf.Dispose();
                        _npcBuffers.Remove(npcId);
                    }
                }
            }
        }

        /// <summary>Get active handles for an NPC.</summary>
        public SpeechHandle[] GetActiveHandles(string npcId)
        {
            lock (_activeHandles)
            {
                if (!_activeHandles.TryGetValue(npcId, out var handles))
                    return Array.Empty<SpeechHandle>();
                return handles.ToArray();
            }
        }

        /// <summary>Check worker health manually (coroutine).</summary>
        public IEnumerator CheckHealth()
        {
            return _client.CheckHealth(OnHealthResponse);
        }

        /// <summary>Check worker health synchronously (for integration tests).</summary>
        /// <returns>True if worker is reachable and healthy.</returns>
        public bool TryCheckHealth()
        {
            try
            {
                var url = _endpoint.TrimEnd('/') + "/v1/health";
                using (var client = new System.Net.WebClient())
                {
                    var json = client.DownloadString(url);
                    if (json != null && json.Length > 0)
                    {
                        OnHealthResponse(new NpcTts.Networking.HealthStatus(true, "healthy", ""));
                        return true;
                    }
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[NpcTts] Health check failed: " + ex.Message);
            }
            return false;
        }

        /// <summary>Poll the worker until it is healthy or timeout expires.</summary>
        /// <param name="timeoutSeconds">Maximum time to wait.</param>
        /// <returns>True if worker became healthy.</returns>
        public bool WaitUntilHealthy(float timeoutSeconds)
        {
            var deadline = UnityEngine.Time.realtimeSinceStartup + timeoutSeconds;
            while (UnityEngine.Time.realtimeSinceStartup < deadline)
            {
                if (IsWorkerHealthy || TryCheckHealth())
                    return true;
                System.Threading.Thread.Sleep(200);
            }
            return IsWorkerHealthy;
        }

        /// <summary>
        /// Synthesize speech synchronously using HttpClient.
        /// For integration tests where coroutines don't advance.
        /// Does not play audio; just verifies synthesis succeeds.
        /// </summary>
        public SpeechHandle SpeakSync(SpeechRequest request)
        {
            if (string.IsNullOrEmpty(request.NpcId))
                return CreateFailedHandle(request, TerminalReason.PlaybackFailure, "NPC ID is required");

            if (string.IsNullOrEmpty(request.Text))
                return CreateFailedHandle(request, TerminalReason.InferenceError, "Text is required");

            if (!IsWorkerHealthy && !TryCheckHealth())
            {
                Debug.LogWarning("[NpcTts] Worker unavailable: " + WorkerStatus);
                return CreateFailedHandle(request, TerminalReason.WorkerUnavailable, WorkerStatus);
            }

            var utteranceId = request.NpcId + "-" + (++_utteranceCounter);
            var handle = new SpeechHandle(utteranceId, request.NpcId);

            var voiceId = string.IsNullOrEmpty(request.VoiceId) ? _defaultVoiceId : request.VoiceId;
            var language = string.IsNullOrEmpty(request.Language) ? _defaultLanguage : request.Language;

            try
            {
                var url = _endpoint.TrimEnd('/') + "/v1/tts/stream";
                var body = "{\"text\":" + EscapeJson(request.Text) + ",\"language\":\"" + language + "\",\"voiceId\":\"" + voiceId + "\"}";

                using (var client = new System.Net.WebClient())
                {
                    client.Headers[System.Net.HttpRequestHeader.ContentType] = "application/json";
                    var pcmData = client.UploadData(url, "POST", System.Text.Encoding.UTF8.GetBytes(body));

                    if (pcmData != null && pcmData.Length > 0)
                    {
                        handle.SetStatus(SpeechStatus.Finished);
                        OnFinished?.Invoke(handle);
                    }
                    else
                    {
                        handle.TerminalReason = TerminalReason.InferenceError;
                        handle.SetStatus(SpeechStatus.Failed);
                        OnFailed?.Invoke(handle, "No audio data received");
                    }
                }
            }
            catch (System.Exception ex)
            {
                handle.TerminalReason = TerminalReason.WorkerUnavailable;
                handle.SetStatus(SpeechStatus.Failed);
                OnFailed?.Invoke(handle, ex.Message);
                Debug.LogWarning("[NpcTts] SpeakSync failed: " + ex.Message);
            }

            return handle;
        }

        /// <summary>Create a voice from reference audio.</summary>
        public IEnumerator CreateVoice(string voiceId, string language, string transcript, string refAudioPath)
        {
            return _client.CreateVoice(voiceId, language, transcript, refAudioPath, success =>
            {
                if (success)
                    Debug.Log($"[NpcTts] Voice '{voiceId}' created successfully");
                else
                    Debug.LogError($"[NpcTts] Failed to create voice '{voiceId}'");
            });
        }

        // ── Internal Coroutines ──

        private IEnumerator ProcessSpeech(SpeechRequest request, SpeechHandle handle, PcmBuffer buffer)
        {
            handle.SetStatus(SpeechStatus.Queued);

            var voiceId = string.IsNullOrEmpty(request.VoiceId) ? _defaultVoiceId : request.VoiceId;
            var language = string.IsNullOrEmpty(request.Language) ? _defaultLanguage : request.Language;

            // Use the raw PCM stream endpoint directly
            byte[] pcmData = null;
            var op = _client.StreamRawPcm(request.Text, language, voiceId, data => pcmData = data);

            yield return op;

            if (pcmData == null || pcmData.Length == 0)
            {
                handle.TerminalReason = TerminalReason.InferenceError;
                handle.SetStatus(SpeechStatus.Failed);
                OnFailed?.Invoke(handle, "No audio data received");
                CleanUpHandle(request.NpcId, handle);
                yield break;
            }

            // Write PCM to buffer
            var pcm = PcmBuffer.ToSamples(pcmData);
            buffer.Write(pcm, 0, pcm.Length);

            // Start playback
            var source = FindOrCreateSource(request.NpcId);
            if (source != null)
            {
                handle.SetStatus(SpeechStatus.Playing);
                OnStarted?.Invoke(handle);

                // Create and play the audio clip
                var floats = new float[pcm.Length];
                for (var i = 0; i < pcm.Length; i++)
                    floats[i] = pcm[i] / 32768.0f;

                var clip = AudioClip.Create($"NpcTts-{request.UtteranceId}", pcm.Length, 1, 24000, false);
                clip.SetData(floats, 0);
                source.PlayOneShot(clip);

                // Wait for playback to finish
                var duration = pcm.Length / 24000.0f;
                yield return new WaitForSeconds(duration);

                handle.SetStatus(SpeechStatus.Finished);
                OnFinished?.Invoke(handle);
                CleanUpHandle(request.NpcId, handle);
            }
            else
            {
                handle.TerminalReason = TerminalReason.PlaybackFailure;
                handle.SetStatus(SpeechStatus.Failed);
                OnFailed?.Invoke(handle, "Could not create audio source");
                CleanUpHandle(request.NpcId, handle);
            }
        }

        private AudioSource FindOrCreateSource(string npcId)
        {
            var existing = UnityEngine.Object.FindAnyObjectByType<NpcAudioSource>();
            if (existing != null && existing.NpcId == npcId)
                return existing.Source;

            var go = new GameObject($"NpcTts-{npcId}");
            go.hideFlags = HideFlags.HideAndDontSave;
            var component = go.AddComponent<NpcAudioSource>();
            component.NpcId = npcId;
            return component.Source;
        }

        private void CleanUpHandle(string npcId, SpeechHandle handle)
        {
            lock (_activeHandles)
            {
                if (_activeHandles.TryGetValue(npcId, out var handles))
                    handles.Remove(handle);
            }
        }

        private IEnumerator PingWorker()
        {
            while (true)
            {
                yield return _client.CheckHealth(OnHealthResponse);
                yield return new WaitForSeconds(_healthInterval);
            }
        }

        private void OnHealthResponse(HealthStatus status)
        {
            IsWorkerHealthy = status.IsHealthy;
            WorkerStatus = status.Status;
            Debug.Log($"[NpcTts] Worker health: {status.Status} (model: {status.Model})");
        }

        private static string EscapeJson(string s)
        {
            if (s == null) return "null";
            var escaped = s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
            return "\"" + escaped + "\"";
        }

        private SpeechHandle CreateFailedHandle(SpeechRequest request, TerminalReason reason, string message)
        {
            var utteranceId = $"{request.NpcId}-{++_utteranceCounter}";
            var handle = new SpeechHandle(utteranceId, request.NpcId, reason);
            OnFailed?.Invoke(handle, message);
            return handle;
        }
    }

    /// <summary>AudioSource component for an NPC.</summary>
    internal sealed class NpcAudioSource : MonoBehaviour
    {
        public string NpcId { get; set; }
        public AudioSource Source { get; private set; }

        private void Awake()
        {
            Source = GetComponent<AudioSource>();
            if (Source == null)
                Source = gameObject.AddComponent<AudioSource>();

            Source.spatialBlend = 1.0f;
            Source.panStereo = 0.0f;
            Source.reverbZoneMix = 0.5f;
            Source.volume = 1.0f;
            Source.playOnAwake = false;
            Source.priority = 128;
            Source.minDistance = 1.0f;
            Source.maxDistance = 50.0f;
        }
    }
}
