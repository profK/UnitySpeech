// SPDX-License-Identifier: Apache-2.0
// com.npctts.unity — Audio playback with spatial support

using System;
using UnityEngine;

namespace NpcTts.Audio
{
    /// <summary>Handles spatial audio playback for a single NPC utterance.</summary>
    internal sealed class UtterancePlayer : IDisposable
    {
        private readonly GameObject _sourceObject;
        private readonly AudioSource _source;
        private readonly PcmBuffer _buffer;
        private readonly float _prebufferMs;
        private readonly Action<PlaybackEvent> _onEvent;

        private volatile bool _isPlaying;
        private volatile bool _eof;
        private volatile bool _disposed;
        private int _samplesSinceEof;
        private int _underruns;
        private double _insertedSilenceMs;
        private int _totalPlayed;
        private int _totalReceived;
        private float _rmsThreshold;
        private int _rmsWindow;
        private int _rmsCount;
        private double _rmsSum;
        private bool _onsetDetected;

        // Reusable array for underrun handling (avoid GC)
        private readonly short[] _readBuffer = new short[4096];

        internal UtterancePlayer(Transform parent, PcmBuffer buffer, float prebufferMs, int sampleRate, Action<PlaybackEvent> onEvent)
        {
            _buffer = buffer;
            _prebufferMs = prebufferMs;
            _onEvent = onEvent;

            _sourceObject = new GameObject("NpcTtsSource");
            _sourceObject.hideFlags = HideFlags.HideAndDontSave;
            _sourceObject.transform.SetParent(parent);

            _source = _sourceObject.AddComponent<AudioSource>();
            _source.spatialBlend = 1.0f;
            _source.panStereo = 0.0f;
            _source.reverbZoneMix = 0.5f;
            _source.volume = 1.0f;
            _source.playOnAwake = false;
            _source.priority = 128;
            _source.minDistance = 1.0f;
            _source.maxDistance = 50.0f;
            _source.dopplerLevel = 0.0f;

            // Create streaming AudioClip - callback signature: Action<float[]>
            var clip = AudioClip.Create("NpcTtsStream", int.MaxValue, 1, sampleRate, false, OnAudioRead);
            _source.clip = clip;

            _rmsThreshold = 32767.0f * (float)Math.Pow(10.0, -45.0 / 20.0);
            _rmsWindow = sampleRate / 100;
        }

        internal void StartPlayback()
        {
            if (_disposed)
                return;

            if (!_isPlaying)
            {
                _isPlaying = true;
                _source.Play();
                _onEvent?.Invoke(PlaybackEvent.Started);
            }
        }

        internal void SignalEof()
        {
            _eof = true;
        }

        internal void Stop()
        {
            if (!_disposed && _isPlaying)
            {
                _source.Stop();
                _isPlaying = false;
                _onEvent?.Invoke(PlaybackEvent.Stopped);
            }
        }

        private void OnAudioRead(float[] data)
        {
            if (_disposed)
            {
                System.Array.Clear(data, 0, data.Length);
                return;
            }

            var needed = data.Length;
            var available = _buffer.Count;

            if (available < needed)
            {
                _underruns++;
                var silenceSamples = needed - available;
                _insertedSilenceMs += silenceSamples / 24000.0 * 1000.0;
            }

            var readCount = _buffer.Read(_readBuffer, 0, Math.Max(needed, _readBuffer.Length));

            for (var i = 0; i < needed; i++)
            {
                if (i < readCount)
                    data[i] = _readBuffer[i] / 32768.0f;
                else
                    data[i] = 0.0f;
            }

            _totalPlayed += needed;

            if (!_onsetDetected && _isPlaying)
            {
                _rmsCount += needed;
                for (var i = 0; i < needed; i++)
                {
                    var sample = _readBuffer[i < readCount ? i : 0];
                    _rmsSum += sample * sample;
                }

                if (_rmsCount >= _rmsWindow)
                {
                    var rmsSqr = _rmsSum / _rmsCount;
                    if (rmsSqr >= _rmsThreshold * _rmsThreshold)
                    {
                        _onsetDetected = true;
                        _onEvent?.Invoke(PlaybackEvent.OnsetDetected);
                    }
                    _rmsSum = 0;
                    _rmsCount = 0;
                }
            }

            if (_eof && _buffer.Count == 0 && _samplesSinceEof > 480)
            {
                Stop();
                _onEvent?.Invoke(PlaybackEvent.Finished);
            }
            else if (_eof)
            {
                _samplesSinceEof += needed;
            }
        }

        internal PlaybackStats GetStats()
        {
            return new PlaybackStats(_underruns, _insertedSilenceMs, _totalPlayed, _totalReceived);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            Stop();
            if (_sourceObject != null)
            {
                if (Application.isPlaying)
                    UnityEngine.Object.Destroy(_sourceObject);
                else
                    UnityEngine.Object.DestroyImmediate(_sourceObject);
            }
        }
    }

    /// <summary>Events emitted during playback.</summary>
    public enum PlaybackEvent
    {
        Started,       // Audio began playing
        OnsetDetected, // First sustained audio content detected
        Finished,      // Buffer drained after EOF
        Stopped,       // Explicitly stopped
    }
}
