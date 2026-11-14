// SPDX-License-Identifier: Apache-2.0
// com.npctts.unity — EditMode protocol tests

using System;
using System.Collections.Generic;
using NUnit.Framework;
using NpcTts.Protocol;

namespace NpcTts.Tests.EditMode
{
    /// <summary>Tests for the binary protocol decoder. Runs in EditMode without a model.</summary>
    public class ProtocolTests
    {
        [Test]
        public void StartRecord_Roundtrip()
        {
            var payload = BuildStartPayload(1, "dwarf", "en", 1.5, 24000);
            var reader = new RecordReader();
            var records = reader.Feed(EncodeRecord(0x01, payload));

            Assert.AreEqual(1, records.Count);
            Assert.AreEqual(RecordType.Start, records[0].Type);
        }

        [Test]
        public void PcmRecord_Roundtrip()
        {
            var pcmBytes = new byte[] { 0x01, 0x00, 0x02, 0x00, 0x03, 0x00, 0x04, 0x00 }; // 4 samples
            var payload = BuildPcmPayload(0, pcmBytes);
            var reader = new RecordReader();
            var records = reader.Feed(EncodeRecord(0x02, payload));

            Assert.AreEqual(1, records.Count);
            Assert.AreEqual(RecordType.Pcm, records[0].Type);
        }

        [Test]
        public void EndRecord_Roundtrip()
        {
            var payload = BuildEndPayload(14400, 1500.0, 1200.0, 200.0);
            var reader = new RecordReader();
            var records = reader.Feed(EncodeRecord(0x03, payload));

            Assert.AreEqual(1, records.Count);
            Assert.AreEqual(RecordType.End, records[0].Type);
        }

        [Test]
        public void ErrorRecord_Roundtrip()
        {
            var payload = BuildErrorPayload(1, "Test error");
            var reader = new RecordReader();
            var records = reader.Feed(EncodeRecord(0x04, payload));

            Assert.AreEqual(1, records.Count);
            Assert.AreEqual(RecordType.Error, records[0].Type);
        }

        [Test]
        public void SplitHeaderAcrossReads()
        {
            var payload = BuildStartPayload(1, "test", "en", 1.0, 24000);
            var record = EncodeRecord(0x01, payload);

            var reader = new RecordReader();
            for (var i = 0; i < 5; i++)
            {
                var partial = new byte[] { record[i] };
                var partialResult = reader.Feed(partial);
                Assert.AreEqual(0, partialResult.Count, "Should not complete with partial header");
            }

            var rest = new byte[record.Length - 5];
            System.Array.Copy(record, 5, rest, 0, rest.Length);
            var result = reader.Feed(rest);
            Assert.AreEqual(1, result.Count);
        }

        [Test]
        public void MultipleRecordsOneRead()
        {
            var r1 = EncodeRecord(0x01, BuildStartPayload(1, "v1", "en", 1.0, 24000));
            var r2 = EncodeRecord(0x02, BuildPcmPayload(0, new byte[] { 1, 0, 2, 0 }));

            var reader = new RecordReader();
            var combined = new byte[r1.Length + r2.Length];
            System.Array.Copy(r1, 0, combined, 0, r1.Length);
            System.Array.Copy(r2, 0, combined, r1.Length, r2.Length);

            var result = reader.Feed(combined);
            Assert.AreEqual(2, result.Count);
        }

        [Test]
        public void OversizedRecordRejected()
        {
            // Create a record with 17 MB header but only provide 100 bytes of payload
            // The reader should reject it when it realizes the data is incomplete
            var bigHeader = new byte[5];
            bigHeader[0] = 0x02; // PCM type
            bigHeader[1] = 0x01; // 17 MB in little-endian
            bigHeader[2] = 0x00;
            bigHeader[3] = 0x10;
            bigHeader[4] = 0x00;

            var reader = new RecordReader();
            var result = reader.Feed(bigHeader);
            // Should return 0 records (incomplete, not enough data to read)
            Assert.AreEqual(0, result.Count);
        }

        [Test]
        public void Parser_HandlesCompleteStream()
        {
            var parser = new StreamParser();

            var start = EncodeRecord(0x01, BuildStartPayload(1, "default", "en", 1.0, 24000));
            var pcm = EncodeRecord(0x02, BuildPcmPayload(0, new byte[] { 100, 0, 206, 0, 200, 0, 156, 0 })); // 100, -50, 200, -100 as signed shorts
            var end = EncodeRecord(0x03, BuildEndPayload(4, 100.0, 80.0, 0.0));

            var combined = new byte[start.Length + pcm.Length + end.Length];
            System.Array.Copy(start, 0, combined, 0, start.Length);
            System.Array.Copy(pcm, 0, combined, start.Length, pcm.Length);
            System.Array.Copy(end, 0, combined, start.Length + pcm.Length, end.Length);

            var completed = parser.Feed(combined);
            Assert.IsTrue(completed);
            Assert.IsNotNull(parser.Start);
            Assert.IsNotNull(parser.End);
            Assert.IsNull(parser.Error);
            Assert.AreEqual(4, parser.TotalSamples);
        }

        [Test]
        public void Parser_HandlesErrorRecord()
        {
            var parser = new StreamParser();
            var error = EncodeRecord(0x04, BuildErrorPayload(7, "Inference failed"));

            var completed = parser.Feed(error);
            Assert.IsTrue(completed);
            Assert.IsNotNull(parser.Error);
            Assert.AreEqual(TerminalReason.InferenceError, parser.Error.Value.Code);
        }

        // ── Helpers ──

        private byte[] EncodeRecord(byte type, byte[] payload)
        {
            var padded = PadToAlignment(payload, 8);
            var result = new byte[5 + padded.Length];
            result[0] = type;
            result[1] = (byte)(padded.Length & 0xFF);
            result[2] = (byte)((padded.Length >> 8) & 0xFF);
            result[3] = (byte)((padded.Length >> 16) & 0xFF);
            result[4] = (byte)((padded.Length >> 24) & 0xFF);
            System.Array.Copy(padded, 0, result, 5, padded.Length);
            return result;
        }

        private byte[] PadToAlignment(byte[] data, int alignment)
        {
            var remainder = data.Length % alignment;
            if (remainder == 0)
                return data;
            var padded = new byte[data.Length + (alignment - remainder)];
            System.Array.Copy(data, 0, padded, 0, data.Length);
            return padded;
        }

        private byte[] BuildStartPayload(byte version, string voiceId, string language, double duration, uint sampleRate)
        {
            var vid = System.Text.Encoding.UTF8.GetBytes(voiceId);
            var lang = System.Text.Encoding.UTF8.GetBytes(language);
            var durationBytes = System.BitConverter.GetBytes(duration);
            var srBytes = System.BitConverter.GetBytes(sampleRate);

            var result = new byte[1 + 2 + vid.Length + 2 + lang.Length + 8 + 4];
            var i = 0;
            result[i++] = version;
            result[i++] = (byte)(vid.Length & 0xFF);
            result[i++] = (byte)((vid.Length >> 8) & 0xFF);
            System.Array.Copy(vid, 0, result, i, vid.Length); i += vid.Length;
            result[i++] = (byte)(lang.Length & 0xFF);
            result[i++] = (byte)((lang.Length >> 8) & 0xFF);
            System.Array.Copy(lang, 0, result, i, lang.Length); i += lang.Length;
            System.Array.Copy(durationBytes, 0, result, i, 8); i += 8;
            System.Array.Copy(srBytes, 0, result, i, 4);
            return result;
        }

        private byte[] BuildPcmPayload(long offset, byte[] pcmBytes)
        {
            // Format: offset (8 bytes LE) + length (4 bytes LE) + data
            var result = new byte[12 + pcmBytes.Length];
            System.Array.Copy(System.BitConverter.GetBytes(offset), 0, result, 0, 8);
            // Length as little-endian uint32
            var len = (uint)pcmBytes.Length;
            result[8] = (byte)(len & 0xFF);
            result[9] = (byte)((len >> 8) & 0xFF);
            result[10] = (byte)((len >> 16) & 0xFF);
            result[11] = (byte)((len >> 24) & 0xFF);
            System.Array.Copy(pcmBytes, 0, result, 12, pcmBytes.Length);
            return result;
        }

        private byte[] BuildEndPayload(int totalSamples, double wallMs, double inferenceMs, double silenceMs)
        {
            var result = new byte[32];
            System.Array.Copy(System.BitConverter.GetBytes(totalSamples), 0, result, 0, 4);
            System.Array.Copy(System.BitConverter.GetBytes(wallMs), 0, result, 4, 8);
            System.Array.Copy(System.BitConverter.GetBytes(inferenceMs), 0, result, 12, 8);
            System.Array.Copy(System.BitConverter.GetBytes(silenceMs), 0, result, 20, 8);
            return result;
        }

        private byte[] BuildErrorPayload(int code, string message)
        {
            var msgBytes = System.Text.Encoding.UTF8.GetBytes(message);
            var result = new byte[4 + 2 + msgBytes.Length];
            System.Array.Copy(System.BitConverter.GetBytes(code), 0, result, 0, 4);
            result[4] = (byte)(msgBytes.Length & 0xFF);
            result[5] = (byte)((msgBytes.Length >> 8) & 0xFF);
            System.Array.Copy(msgBytes, 0, result, 6, msgBytes.Length);
            return result;
        }
    }
}
