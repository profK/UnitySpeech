// SPDX-License-Identifier: Apache-2.0
// com.npctts.unity — Single-producer/single-consumer PCM buffer

using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace NpcTts.Audio
{
    /// <summary>
    /// Lock-free SPSC circular buffer for PCM16 audio streaming.
    /// </summary>
    internal sealed class PcmBuffer : IDisposable
    {
        private readonly short[] _samples;
        private readonly int _capacity;
        private int _head;   // Write position (producer)
        private int _tail;   // Read position (consumer)

        public PcmBuffer(int capacitySeconds = 5)
        {
            _capacity = capacitySeconds * 24000; // 24 kHz mono
            _samples = new short[_capacity];
            Debug.Log("[NpcTts] PCM buffer: " + _capacity + " samples (" + (_capacity / 24000.0f) + "s, " + (_capacity * 2 / 1024) + " KB)");
        }

        public int Capacity => _capacity;

        public int Count
        {
            get
            {
                var diff = _head - _tail;
                return diff >= 0 ? diff : diff + _capacity;
            }
        }

        public int Available => _capacity - 1 - Count;

        /// <summary>Write PCM16 samples from the given byte array. Returns the number of samples actually written.</summary>
        public int Write(byte[] data, int offset, int byteCount)
        {
            var sampleCount = byteCount / 2;
            if (sampleCount <= 0 || data == null)
                return 0;

            var toWrite = Math.Min(sampleCount, Available);
            if (toWrite == 0)
                return 0;

            var pos = _head;
            for (var i = 0; i < toWrite; i++)
            {
                var srcIdx = offset + i * 2;
                var lo = data[srcIdx];
                var hi = data[srcIdx + 1];
                _samples[pos] = (short)(lo | (hi << 8));

                pos++;
                if (pos == _capacity)
                    pos = 0;
            }

            _head = pos;
            return toWrite;
        }

        /// <summary>Write directly from a short array.</summary>
        public int Write(short[] samples, int offset, int count)
        {
            if (count <= 0 || samples == null)
                return 0;

            var toWrite = Math.Min(count, Available);
            if (toWrite == 0)
                return 0;

            var pos = _head;
            for (var i = 0; i < toWrite; i++)
            {
                _samples[pos] = samples[offset + i];
                pos++;
                if (pos == _capacity)
                    pos = 0;
            }

            _head = pos;
            return toWrite;
        }

        /// <summary>Read PCM16 samples into the output array. Always fills the requested count (with silence on underrun).</summary>
        public int Read(short[] output, int offset, int count)
        {
            if (count <= 0 || output == null)
                return 0;

            var available = Count;
            var toRead = Math.Min(count, available);

            var pos = _tail;
            for (var i = 0; i < toRead; i++)
            {
                output[offset + i] = _samples[pos];
                pos++;
                if (pos == _capacity)
                    pos = 0;
            }

            _tail = pos;

            for (var i = toRead; i < count; i++)
                output[offset + i] = 0;

            return count;
        }

        internal static short[] ToSamples(byte[] data)
        {
            var count = data.Length / 2;
            var samples = new short[count];
            for (var i = 0; i < count; i++)
            {
                var idx = i * 2;
                samples[i] = (short)(data[idx] | (data[idx + 1] << 8));
            }
            return samples;
        }

        public void Clear()
        {
            _head = 0;
            _tail = 0;
            System.Array.Clear(_samples, 0, _samples.Length);
        }

        public void Dispose()
        {
            Clear();
        }
    }

    /// <summary>Stats for audio playback monitoring.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PlaybackStats
    {
        public readonly int UnderrunCount;
        public readonly double InsertedSilenceMs;
        public readonly int TotalSamplesPlayed;
        public readonly int TotalSamplesReceived;

        internal PlaybackStats(int underruns, double silenceMs, int played, int received)
        {
            UnderrunCount = underruns;
            InsertedSilenceMs = silenceMs;
            TotalSamplesPlayed = played;
            TotalSamplesReceived = received;
        }
    }
}
