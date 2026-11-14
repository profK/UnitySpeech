// SPDX-License-Identifier: Apache-2.0
// com.npctts.unity — Streaming synthesis session (simplified, used for future streaming)

using System;
using System.Threading;
using UnityEngine;
using NpcTts.Audio;
using NpcTts.Telemetry;

namespace NpcTts
{
    /// <summary>Manages a single synthesis-to-playback session. Placeholder for future streaming support.</summary>
    internal sealed class StreamingSession : IDisposable
    {
        private readonly SpeechHandle _handle;
        private readonly TelemetryCollector _telemetry;
        private volatile bool _disposed;

        internal StreamingSession(SpeechHandle handle, TelemetryCollector telemetry)
        {
            _handle = handle;
            _telemetry = telemetry;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
        }
    }
}
