// SPDX-License-Identifier: Apache-2.0
// com.npctts.unity — PlayMode audio and lifecycle tests

using System;
using NUnit.Framework;
using UnityEngine;
using NpcTts;
using NpcTts.Audio;

namespace NpcTts.Tests.PlayMode
{
    /// <summary>PlayMode tests for audio playback and manager lifecycle.</summary>
    public class AudioPlaybackTests
    {
        private GameObject _manager;
        private NpcTtsManager _ttsManager;

        [OneTimeSetUp]
        public void SetUp()
        {
            _manager = new GameObject("NpcTtsManager");
            _ttsManager = _manager.AddComponent<NpcTtsManager>();
            UnityEngine.Object.DontDestroyOnLoad(_manager);
        }

        [OneTimeTearDown]
        public void TearDown()
        {
            if (_manager != null)
                UnityEngine.Object.DestroyImmediate(_manager, true);
        }

        [Test]
        public void Speak_InvalidNpcId_FailsGracefully()
        {
            var request = new SpeechRequest(
                utteranceId: "test-2",
                npcId: "",
                voiceId: "default",
                text: "Hello!");

            var handle = _ttsManager.Speak(request);

            Assert.IsNotNull(handle);
            Assert.AreEqual(SpeechStatus.Failed, handle.Status);
            Assert.AreEqual(TerminalReason.PlaybackFailure, handle.TerminalReason);
        }

        [Test]
        public void Speak_EmptyText_FailsGracefully()
        {
            var request = new SpeechRequest(
                utteranceId: "test-3",
                npcId: "npc-1",
                voiceId: "default",
                text: "");

            var handle = _ttsManager.Speak(request);

            Assert.IsNotNull(handle);
            Assert.AreEqual(SpeechStatus.Failed, handle.Status);
            Assert.AreEqual(TerminalReason.InferenceError, handle.TerminalReason);
        }

        [Test]
        public void PcmBuffer_WritesAndReads()
        {
            var buffer = new PcmBuffer(2);
            var samples = new short[] { 1000, -2000, 3000, -4000 };
            var pcmBytes = new byte[samples.Length * 2];
            for (var i = 0; i < samples.Length; i++)
            {
                var s = samples[i];
                pcmBytes[i * 2] = (byte)(s & 0xFF);
                pcmBytes[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
            }

            var written = buffer.Write(pcmBytes, 0, pcmBytes.Length);
            Assert.AreEqual(4, written);

            var output = new short[4];
            var readCount = buffer.Read(output, 0, 4);
            Assert.AreEqual(4, readCount);
            Assert.AreEqual(1000, output[0]);
            Assert.AreEqual(-2000, output[1]);
            Assert.AreEqual(3000, output[2]);
            Assert.AreEqual(-4000, output[3]);

            buffer.Dispose();
        }

        [Test]
        public void PcmBuffer_Underrun_FillsWithSilence()
        {
            var buffer = new PcmBuffer(1);
            var output = new short[100];
            var readCount = buffer.Read(output, 0, 100);

            Assert.AreEqual(100, readCount);
            for (var i = 0; i < 100; i++)
                Assert.AreEqual(0, output[i], "Underrun should fill with silence");

            buffer.Dispose();
        }

        [Test]
        public void PcmBuffer_ToSamples_ConvertsCorrectly()
        {
            var pcm = new byte[] { 0x10, 0x00, 0x00, 0xFF }; // 16, -256 as PCM16 LE
            var samples = PcmBuffer.ToSamples(pcm);

            Assert.AreEqual(2, samples.Length);
            Assert.AreEqual(16, samples[0]);
            Assert.AreEqual(-256, samples[1]);
        }

        [Test]
        public void PcmBuffer_WrapAround()
        {
            var buffer = new PcmBuffer(1);
            var capacity = buffer.Capacity;

            // Write samples at the beginning
            var firstBatch = new short[] { 111, 222, 333 };
            buffer.Write(firstBatch, 0, firstBatch.Length);

            // Read them back
            var read1 = new short[3];
            buffer.Read(read1, 0, 3);
            Assert.AreEqual(111, read1[0]);
            Assert.AreEqual(222, read1[1]);
            Assert.AreEqual(333, read1[2]);

            // Advance tail near end by reading silence
            var gap = capacity - 6; // Read everything between tail (3) and near-end
            var silence = new short[gap];
            buffer.Read(silence, 0, gap);

            // Now write samples near the end (will wrap to start of array)
            var secondBatch = new short[] { 999, 888 };
            buffer.Write(secondBatch, 0, secondBatch.Length);

            // Read them back (wrapped)
            var read2 = new short[2];
            buffer.Read(read2, 0, 2);
            Assert.AreEqual(999, read2[0]);
            Assert.AreEqual(888, read2[1]);

            buffer.Dispose();
        }

        [Test]
        public void StopNpc_NoActiveHandles_NoError()
        {
            _ttsManager.StopNpc("nonexistent-npc");
            Assert.Pass();
        }
    }

    /// <summary>Tests for the SpeechRequest struct.</summary>
    public class SpeechRequestTests
    {
        [Test]
        public void Constructor_ValidatesRequiredFields()
        {
            Assert.Throws<ArgumentNullException>(() => new SpeechRequest(null, "npc", "voice", "text"));
            Assert.Throws<ArgumentNullException>(() => new SpeechRequest("utter", null, "voice", "text"));
            Assert.Throws<ArgumentNullException>(() => new SpeechRequest("utter", "npc", null, "text"));
            Assert.Throws<ArgumentNullException>(() => new SpeechRequest("utter", "npc", "voice", null));
        }

        [Test]
        public void Constructor_DefaultLanguage()
        {
            var request = new SpeechRequest("utter", "npc", "voice", "Hello!");
            Assert.AreEqual("en", request.Language);
        }

        [Test]
        public void Constructor_CustomLanguage()
        {
            var request = new SpeechRequest("utter", "npc", "voice", "こんにちは!", "ja");
            Assert.AreEqual("ja", request.Language);
        }

        [Test]
        public void Constructor_WithDeadline()
        {
            var deadline = TimeSpan.FromSeconds(30);
            var request = new SpeechRequest("utter", "npc", "voice", "text", "en", deadline);
            Assert.AreEqual(deadline, request.Deadline);
        }
    }

    /// <summary>Tests for the SpeechHandle.</summary>
    public class SpeechHandleTests
    {
        [Test]
        public void Handle_InitialState()
        {
            var handle = new SpeechHandle("utter-1", "npc-1");
            Assert.AreEqual(SpeechStatus.Pending, handle.Status);
            Assert.IsFalse(handle.IsCompleted);
        }

        [Test]
        public void Handle_FailedInitialState()
        {
            var handle = new SpeechHandle("utter-1", "npc-1", TerminalReason.WorkerUnavailable);
            Assert.AreEqual(SpeechStatus.Failed, handle.Status);
            Assert.IsTrue(handle.IsCompleted);
        }

        [Test]
        public void Handle_Cancel_IsIdempotent()
        {
            var handle = new SpeechHandle("utter-1", "npc-1");
            handle.Cancel();
            handle.Cancel();
        }

        [Test]
        public void Handle_SetStatus_MarksCompleted()
        {
            var handle = new SpeechHandle("utter-1", "npc-1");
            handle.SetStatus(SpeechStatus.Finished);
            Assert.IsTrue(handle.IsCompleted);

            handle.SetStatus(SpeechStatus.Cancelled);
            Assert.IsTrue(handle.IsCompleted);
        }
    }
}
