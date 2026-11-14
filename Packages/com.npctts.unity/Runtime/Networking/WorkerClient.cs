// SPDX-License-Identifier: Apache-2.0
// com.npctts.unity — HTTP client for the TTS worker

using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace NpcTts.Networking
{
    /// <summary>HTTP client for communicating with the TTS worker.</summary>
    internal sealed class WorkerClient : MonoBehaviour
    {
        [SerializeField] private string _endpoint = "http://127.0.0.1:8080";
        [SerializeField] private int _timeoutSeconds = 60;

        public string Endpoint { get => _endpoint; set => _endpoint = value; }

        private void Awake()
        {
            if (!Endpoint.StartsWith("http://") && !Endpoint.StartsWith("https://"))
                _endpoint = "http://" + Endpoint;
        }

        public IEnumerator CheckHealth(Action<HealthStatus> callback)
        {
            var url = Endpoint + "/v1/health";
            var req = new UnityWebRequest(url, "GET");
            req.downloadHandler = new DownloadHandlerBuffer();
            req.uploadHandler = new UploadHandlerRaw(new byte[0]);
            req.timeout = _timeoutSeconds;
            yield return req.SendWebRequest();

#if UNITY_2023_1_OR_NEWER
            if (req.result != UnityWebRequest.Result.Success)
#else
            if (req.isNetworkError || req.isHttpError)
#endif
            {
                callback(new HealthStatus(false, "connection_failed", string.Empty));
            }
            else
            {
                var text = req.downloadHandler.text;
                callback(ParseHealth(text));
            }
            req.Dispose();
        }

        public IEnumerator SubmitSpeech(string requestId, string text, string language, string voiceId, Action<SubmitResult> callback)
        {
            var json = "{\"request_id\":\"" + requestId + "\",\"text\":" + EscapeJson(text) + ",\"language\":\"" + language + "\",\"voice_id\":\"" + voiceId + "\"}";
            var url = Endpoint + "/v1/speech";
            var req = new UnityWebRequest(url, "POST");
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.timeout = _timeoutSeconds;
            yield return req.SendWebRequest();

#if UNITY_2023_1_OR_NEWER
            if (req.result != UnityWebRequest.Result.Success)
#else
            if (req.isNetworkError || req.isHttpError)
#endif
            {
                callback(new SubmitResult(false, "connection_failed", string.Empty));
            }
            else
            {
                var response = req.downloadHandler.text;
                callback(ParseSubmit(response));
            }
            req.Dispose();
        }

        public IEnumerator CancelSpeech(string requestId, Action<bool> callback)
        {
            var url = Endpoint + "/v1/speech/" + Uri.EscapeDataString(requestId);
            var req = new UnityWebRequest(url, "DELETE");
            req.downloadHandler = new DownloadHandlerBuffer();
            req.uploadHandler = new UploadHandlerRaw(new byte[0]);
            req.timeout = _timeoutSeconds;
            yield return req.SendWebRequest();

#if UNITY_2023_1_OR_NEWER
            var success = req.result == UnityWebRequest.Result.Success;
#else
            var success = !req.isNetworkError && !req.isHttpError;
#endif
            callback(success);
            req.Dispose();
        }

        public IEnumerator CreateVoice(string voiceId, string language, string transcript, string refAudioPath, Action<bool> callback)
        {
            var json = "{\"voice_id\":\"" + voiceId + "\",\"language\":\"" + language + "\",\"transcript\":" + EscapeJson(transcript) + ",\"ref_audio_path\":\"" + refAudioPath + "\"}";
            var url = Endpoint + "/v1/voices";
            var req = new UnityWebRequest(url, "POST");
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.timeout = _timeoutSeconds * 2;
            yield return req.SendWebRequest();

#if UNITY_2023_1_OR_NEWER
            var success = req.result == UnityWebRequest.Result.Success;
#else
            var success = !req.isNetworkError && !req.isHttpError;
#endif
            callback(success);
            req.Dispose();
        }

        public IEnumerator StreamRawPcm(string text, string language, string voiceId, Action<byte[]> callback)
        {
            var json = "{\"text\":" + EscapeJson(text) + ",\"language\":\"" + language + "\",\"voice_id\":\"" + voiceId + "\"}";
            var url = Endpoint + "/v1/tts/stream";
            var req = new UnityWebRequest(url, "POST");
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.timeout = _timeoutSeconds;
            yield return req.SendWebRequest();

#if UNITY_2023_1_OR_NEWER
            if (req.result != UnityWebRequest.Result.Success)
#else
            if (req.isNetworkError || req.isHttpError)
#endif
            {
                callback(null);
            }
            else
            {
                callback(req.downloadHandler.data);
            }
            req.Dispose();
        }

        private static string EscapeJson(string text)
        {
            var sb = new StringBuilder("\"");
            foreach (var c in text)
            {
                switch (c)
                {
                    case '\"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            sb.Append("\"");
            return sb.ToString();
        }

        private static HealthStatus ParseHealth(string json)
        {
            var status = "degraded";
            var device = string.Empty;
            var model = string.Empty;

            if (json.Contains("\"status\":\"ok\""))
                status = "ok";
            if (ExtractString(json, "device", out var d))
                device = d;
            if (ExtractString(json, "model", out var m))
                model = m;

            return new HealthStatus(status == "ok", status, model);
        }

        private static SubmitResult ParseSubmit(string json)
        {
            var requestId = string.Empty;
            var status = string.Empty;
            if (ExtractString(json, "request_id", out var rid))
                requestId = rid;
            if (ExtractString(json, "status", out var s))
                status = s;
            if (ExtractString(json, "error", out var e))
                return new SubmitResult(false, e, requestId);
            return new SubmitResult(true, status, requestId);
        }

        private static bool ExtractString(string json, string key, out string value)
        {
            var pattern = "\"" + key + "\":";
            var idx = json.IndexOf(pattern, StringComparison.Ordinal);
            if (idx < 0)
            {
                value = string.Empty;
                return false;
            }
            var start = idx + pattern.Length;
            while (start < json.Length && char.IsWhiteSpace(json[start]))
                start++;
            if (start >= json.Length || json[start] != '\"')
            {
                value = string.Empty;
                return false;
            }
            start++;
            var end = start;
            while (end < json.Length && json[end] != '\"')
            {
                if (json[end] == '\\')
                    end++;
                end++;
            }
            value = json.Substring(start, end - start);
            return true;
        }
    }

    /// <summary>Worker health status.</summary>
    public struct HealthStatus
    {
        public readonly bool IsHealthy;
        public readonly string Status;
        public readonly string Model;

        public HealthStatus(bool isHealthy, string status, string model)
        {
            IsHealthy = isHealthy;
            Status = status;
            Model = model;
        }
    }

    /// <summary>Result of a speech submission.</summary>
    public struct SubmitResult
    {
        public readonly bool Success;
        public readonly string Status;
        public readonly string RequestId;

        public SubmitResult(bool success, string status, string requestId)
        {
            Success = success;
            Status = status;
            RequestId = requestId;
        }
    }
}
