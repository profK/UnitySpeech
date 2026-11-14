// SPDX-License-Identifier: Apache-2.0
// com.npctts.unity — Binary protocol decoder

using System;
using System.Collections.Generic;

namespace NpcTts.Protocol
{
    /// <summary>Record types in the binary protocol.</summary>
    internal enum RecordType : byte
    {
        Start = 0x01,
        Pcm = 0x02,
        End = 0x03,
        Error = 0x04,
    }

    /// <summary>Little-endian binary reading helpers (portable, no BinaryPrimitives dependency).</summary>
    internal static class BinaryReader
    {
        internal static uint ReadUInt16LE(byte[] data, int offset)
        {
            return (uint)((data[offset]) | (data[offset + 1] << 8));
        }

        internal static uint ReadUInt32LE(byte[] data, int offset)
        {
            return (uint)((data[offset]) | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
        }

        internal static int ReadInt32LE(byte[] data, int offset)
        {
            return (int)ReadUInt32LE(data, offset);
        }

        internal static long ReadInt64LE(byte[] data, int offset)
        {
            return (long)ReadUInt64LE(data, offset);
        }

        internal static ulong ReadUInt64LE(byte[] data, int offset)
        {
            ulong lo = ReadUInt32LE(data, offset);
            ulong hi = ReadUInt32LE(data, offset + 4);
            return lo | (hi << 32);
        }

        internal static double ReadDoubleLE(byte[] data, int offset)
        {
            return System.BitConverter.ToDouble(data, offset);
        }
    }

    /// <summary>Parsed START record.</summary>
    internal struct StartRecord
    {
        public byte Version;
        public string VoiceId;
        public string Language;
        public double DurationSeconds;
        public int SampleRate;

        internal StartRecord(byte version, string voiceId, string language, double duration, int sampleRate)
        {
            Version = version;
            VoiceId = voiceId;
            Language = language;
            DurationSeconds = duration;
            SampleRate = sampleRate;
        }
    }

    /// <summary>Parsed PCM record.</summary>
    internal struct PcmRecord
    {
        public long SampleOffset;
        public byte[] Bytes;

        public int SampleCount => Bytes != null ? Bytes.Length / 2 : 0;

        internal PcmRecord(long offset, byte[] bytes)
        {
            SampleOffset = offset;
            Bytes = bytes;
        }
    }

    /// <summary>Parsed END record.</summary>
    internal struct EndRecord
    {
        public int TotalSamples;
        public double WallMs;
        public double InferenceMs;
        public double SilenceHeadMs;

        internal EndRecord(int total, double wall, double inference, double silence)
        {
            TotalSamples = total;
            WallMs = wall;
            InferenceMs = inference;
            SilenceHeadMs = silence;
        }
    }

    /// <summary>Parsed ERROR record.</summary>
    internal struct ErrorRecord
    {
        public TerminalReason Code;
        public string Message;

        internal ErrorRecord(TerminalReason code, string message)
        {
            Code = code;
            Message = message;
        }
    }

    /// <summary>Reads typed records from a byte stream, tolerating arbitrary read boundaries.</summary>
    internal sealed class RecordReader
    {
        private const int HeaderSize = 5; // 1 (type) + 4 (length)
        private const int Alignment = 8;
        private const int MaxRecordBytes = 16 * 1024 * 1024; // 16 MB guard

        private readonly List<byte> _buffer = new List<byte>(4096);

        internal List<RecordEnvelope> Feed(byte[] data)
        {
            var records = new List<RecordEnvelope>();
            _buffer.AddRange(data);

            while (true)
            {
                if (_buffer.Count < HeaderSize)
                    break;

                var type = (RecordType)_buffer[0];
                var length = BinaryReader.ReadUInt32LE(_buffer.ToArray(), 1);

                var totalNeeded = HeaderSize + length;
                if (_buffer.Count < totalNeeded)
                    break;

                if (length > MaxRecordBytes)
                    throw new ProtocolException("Record length " + length + " exceeds " + MaxRecordBytes + " guard");

                var payload = new byte[length];
                System.Array.Copy(_buffer.ToArray(), HeaderSize, payload, 0, length);
                records.Add(new RecordEnvelope(type, payload));

                var alignedLength = (int)length;
                var remainder = alignedLength % Alignment;
                if (remainder != 0)
                    alignedLength += Alignment - remainder;
                var removeCount = HeaderSize + alignedLength;
                _buffer.RemoveRange(0, Math.Min(removeCount, _buffer.Count));
            }

            return records;
        }

        internal int BufferedBytes => _buffer.Count;
        internal void Clear() => _buffer.Clear();
    }

    /// <summary>Envelope for a parsed record.</summary>
    internal struct RecordEnvelope
    {
        public RecordType Type;
        public byte[] Payload;

        internal RecordEnvelope(RecordType type, byte[] payload)
        {
            Type = type;
            Payload = payload;
        }
    }

    /// <summary>Exception for protocol errors.</summary>
    public sealed class ProtocolException : Exception
    {
        internal ProtocolException(string message) : base(message) { }
    }

    /// <summary>High-level parser for a complete response stream.</summary>
    internal sealed class StreamParser
    {
        private readonly RecordReader _reader = new RecordReader();
        private StartRecord? _start;
        private readonly List<PcmRecord> _pcmChunks = new List<PcmRecord>();
        private EndRecord? _end;
        private ErrorRecord? _error;
        private bool _terminated;

        public bool IsComplete => _terminated;
        public StartRecord? Start => _start;
        public EndRecord? End => _end;
        public ErrorRecord? Error => _error;

        public int TotalSamples
        {
            get
            {
                var count = 0;
                foreach (var chunk in _pcmChunks)
                    count += chunk.SampleCount;
                return count;
            }
        }

        public bool Feed(byte[] data)
        {
            if (_terminated)
                return true;

            var records = _reader.Feed(data);
            foreach (var envelope in records)
            {
                switch (envelope.Type)
                {
                    case RecordType.Start:
                        _start = ParseStart(envelope.Payload);
                        break;
                    case RecordType.Pcm:
                        _pcmChunks.Add(ParsePcm(envelope.Payload));
                        break;
                    case RecordType.End:
                        _end = ParseEnd(envelope.Payload);
                        _terminated = true;
                        break;
                    case RecordType.Error:
                        _error = ParseError(envelope.Payload);
                        _terminated = true;
                        break;
                    default:
                        throw new ProtocolException("Unknown record type: " + (byte)envelope.Type);
                }
            }

            return _terminated;
        }

        public byte[] GetPcmBytes()
        {
            if (_pcmChunks.Count == 0)
                return new byte[0];

            var total = 0;
            foreach (var chunk in _pcmChunks)
                total += chunk.Bytes.Length;

            var result = new byte[total];
            var offset = 0;
            foreach (var chunk in _pcmChunks)
            {
                System.Array.Copy(chunk.Bytes, 0, result, offset, chunk.Bytes.Length);
                offset += chunk.Bytes.Length;
            }
            return result;
        }

        private static StartRecord ParseStart(byte[] payload)
        {
            var i = 0;
            var version = payload[i++];

            var vidLen = (int)BinaryReader.ReadUInt16LE(payload, i); i += 2;
            var voiceId = System.Text.Encoding.UTF8.GetString(payload, i, vidLen); i += vidLen;

            var langLen = (int)BinaryReader.ReadUInt16LE(payload, i); i += 2;
            var language = System.Text.Encoding.UTF8.GetString(payload, i, langLen); i += langLen;

            var duration = BinaryReader.ReadDoubleLE(payload, i); i += 8;
            var sampleRate = (int)BinaryReader.ReadUInt32LE(payload, i);

            return new StartRecord(version, voiceId, language, duration, sampleRate);
        }

        private static PcmRecord ParsePcm(byte[] payload)
        {
            if (payload.Length < 12)
                throw new ProtocolException("PCM payload too short");

            var offset = BinaryReader.ReadInt64LE(payload, 0);
            var length = (int)BinaryReader.ReadUInt32LE(payload, 8);

            if (payload.Length < 12 + length)
                throw new ProtocolException("PCM payload truncated");

            var pcmBytes = new byte[length];
            System.Array.Copy(payload, 12, pcmBytes, 0, length);
            return new PcmRecord(offset, pcmBytes);
        }

        private static EndRecord ParseEnd(byte[] payload)
        {
            if (payload.Length < 28)
                throw new ProtocolException("End payload too short");

            var total = (int)BinaryReader.ReadUInt32LE(payload, 0);
            var wall = BinaryReader.ReadDoubleLE(payload, 4);
            var inference = BinaryReader.ReadDoubleLE(payload, 12);
            var silence = BinaryReader.ReadDoubleLE(payload, 20);

            return new EndRecord(total, wall, inference, silence);
        }

        private static ErrorRecord ParseError(byte[] payload)
        {
            if (payload.Length < 6)
                throw new ProtocolException("Error payload too short");

            var code = BinaryReader.ReadInt32LE(payload, 0);
            var msgLen = (int)BinaryReader.ReadUInt16LE(payload, 4);
            var message = msgLen > 0
                ? System.Text.Encoding.UTF8.GetString(payload, 6, Math.Min(msgLen, payload.Length - 6))
                : string.Empty;

            return new ErrorRecord((TerminalReason)code, message);
        }

        public void Reset()
        {
            _reader.Clear();
            _start = null;
            _pcmChunks.Clear();
            _end = null;
            _error = null;
            _terminated = false;
        }
    }
}
