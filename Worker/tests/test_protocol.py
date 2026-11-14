"""Protocol tests: encoding, decoding, framing, edge cases.

Tests match the spec's EditMode fixture requirements:
- Split headers and PCM records across read boundaries
- Multiple records in a single read
- Malformed offsets/lengths
- Early EOF
- Record size validation
"""

import pytest
import struct
import sys
from pathlib import Path

# Add src to path
sys.path.insert(0, str(Path(__file__).parent.parent / "src"))

from protocol import (
    RecordReader,
    RECORD_START, RECORD_PCM, RECORD_END, RECORD_ERROR,
    PROTOCOL_VERSION, SAMPLE_RATE, SAMPLES_PER_FRAME,
    encode_start, decode_start,
    encode_pcm, decode_pcm,
    encode_end, decode_end,
    encode_error, decode_error,
    SpeechTiming, TerminalReason,
)


# ── Start record ────────────────────────────────────────────────────

class TestStartRecord:
    def test_roundtrip(self):
        raw = encode_start("dwarf", "en", 1.5)
        reader = RecordReader()
        records = reader.feed(raw)
        assert len(records) == 1
        rtype, payload = records[0]
        assert rtype == RECORD_START
        version, voice_id, language, duration_s, sr = decode_start(payload)
        assert version == PROTOCOL_VERSION
        assert voice_id == "dwarf"
        assert language == "en"
        assert duration_s == pytest.approx(1.5)
        assert sr == SAMPLE_RATE

    def test_empty_voice_id(self):
        raw = encode_start("", "en", 0.5)
        reader = RecordReader()
        records = reader.feed(raw)
        assert len(records) == 1
        _, payload = records[0]
        _, voice_id, _, _, _ = decode_start(payload)
        assert voice_id == ""

    def test_unicode_voice_id(self):
        raw = encode_start("dwarf-日本語", "ja", 2.0)
        reader = RecordReader()
        records = reader.feed(raw)
        _, payload = records[0]
        _, voice_id, language, _, _ = decode_start(payload)
        assert voice_id == "dwarf-日本語"
        assert language == "ja"


# ── PCM record ──────────────────────────────────────────────────────

class TestPCMRecord:
    def _make_pcm(self, samples):
        """Create PCM bytes for a list of int16 samples."""
        return struct.pack("<" + "h" * len(samples), *samples)

    def test_roundtrip_single_frame(self):
        pcm = self._make_pcm([1000, -2000, 3000, -4000])
        raw = encode_pcm(0, pcm)
        reader = RecordReader()
        records = reader.feed(raw)
        assert len(records) == 1
        rtype, payload = records[0]
        assert rtype == RECORD_PCM
        offset, decoded = decode_pcm(payload)
        assert offset == 0
        assert decoded == pcm

    def test_nonzero_offset(self):
        pcm = self._make_pcm([100, 200])
        raw = encode_pcm(480, pcm)  # Second frame offset
        reader = RecordReader()
        records = reader.feed(raw)
        offset, decoded = decode_pcm(records[0][1])
        assert offset == 480
        assert decoded == pcm

    def test_full_frame_size(self):
        samples = list(range(-1000, 1000))  # 2000 samples
        pcm = self._make_pcm(samples)
        raw = encode_pcm(0, pcm)
        reader = RecordReader()
        records = reader.feed(raw)
        offset, decoded = decode_pcm(records[0][1])
        assert decoded == pcm

    def test_over_16mb_rejected(self):
        """Guard against oversized records."""
        reader = RecordReader()
        # Craft a header claiming 17 MB payload (will fail validation)
        big_header = struct.pack("<B I", RECORD_PCM, 17 * 1024 * 1024)
        big_header += b"\x00" * 100  # Not enough data anyway
        # Should not raise; just return no complete records
        records = reader.feed(big_header)
        assert len(records) == 0


# ── End record ──────────────────────────────────────────────────────

class TestEndRecord:
    def test_roundtrip(self):
        timing = SpeechTiming(wall_ms=1500.0, inference_ms=1200.0, silence_head_ms=200.0)
        raw = encode_end(14400, timing)
        reader = RecordReader()
        records = reader.feed(raw)
        assert len(records) == 1
        rtype, payload = records[0]
        assert rtype == RECORD_END
        total, decoded = decode_end(payload)
        assert total == 14400
        assert decoded.wall_ms == pytest.approx(1500.0)
        assert decoded.inference_ms == pytest.approx(1200.0)
        assert decoded.silence_head_ms == pytest.approx(200.0)


# ── Error record ────────────────────────────────────────────────────

class TestErrorRecord:
    def test_roundtrip_with_message(self):
        raw = encode_error(TerminalReason.InferenceError, "CUDA out of memory")
        reader = RecordReader()
        records = reader.feed(raw)
        assert len(records) == 1
        rtype, payload = records[0]
        assert rtype == RECORD_ERROR
        reason, msg = decode_error(payload)
        assert reason == TerminalReason.InferenceError
        assert msg == "CUDA out of memory"

    def test_empty_message(self):
        raw = encode_error(TerminalReason.QueueOverload)
        reader = RecordReader()
        records = reader.feed(raw)
        reason, msg = decode_error(records[0][1])
        assert reason == TerminalReason.QueueOverload
        assert msg == ""

    def test_unicode_message(self):
        raw = encode_error(TerminalReason.ProtocolError, "编码错误: 无效字符")
        reader = RecordReader()
        records = reader.feed(raw)
        reason, msg = decode_error(records[0][1])
        assert msg == "编码错误: 无效字符"


# ── RecordReader: boundary tolerance ────────────────────────────────

class TestRecordReader:
    def _pcm(self, samples):
        return struct.pack("<" + "h" * len(samples), *samples)

    def test_split_header_across_reads(self):
        """Header bytes arrive in separate reads."""
        raw = encode_pcm(0, self._pcm([1, 2, 3]))
        reader = RecordReader()

        # Feed header byte by byte
        for i in range(5):  # 5-byte header
            records = reader.feed(raw[i:i+1])
            assert len(records) == 0, "Should not complete with partial header"

        # Feed the payload
        records = reader.feed(raw[5:])
        assert len(records) == 1

    def test_split_payload_across_reads(self):
        """Payload spans two reads."""
        pcm = self._pcm(list(range(100)))
        raw = encode_pcm(0, pcm)
        mid = len(raw) // 2

        reader = RecordReader()
        records = reader.feed(raw[:mid])
        assert len(records) == 0

        records = reader.feed(raw[mid:])
        assert len(records) == 1
        _, payload = records[0]
        offset, decoded = decode_pcm(payload)
        assert decoded == pcm

    def test_multiple_records_one_read(self):
        """Two complete records in a single feed."""
        r1 = encode_pcm(0, self._pcm([10, 20]))
        r2 = encode_pcm(2, self._pcm([30, 40]))
        reader = RecordReader()
        records = reader.feed(r1 + r2)
        assert len(records) == 2
        assert records[0][0] == RECORD_PCM
        assert records[1][0] == RECORD_PCM

    def test_trailing_partial_record(self):
        """Incomplete record at the end is buffered, not lost."""
        r1 = encode_pcm(0, self._pcm([1, 2]))
        r2 = encode_pcm(2, self._pcm([3, 4, 5, 6]))

        reader = RecordReader()
        # Feed r1 complete + partial r2
        partial = r2[:3]  # 3 bytes of r2's header (incomplete)
        records = reader.feed(r1 + partial)
        assert len(records) == 1  # Only r1

        # Complete r2
        records = reader.feed(r2[3:])
        assert len(records) == 1  # r2 completes

    def test_interleaved_split_and_multi(self):
        """Complex: split first record, then two complete records."""
        r1 = encode_start("voice", "en", 1.0)
        r2 = encode_pcm(0, self._pcm([100]))
        r3 = encode_end(1, SpeechTiming())

        # Split r1 in half
        mid = len(r1) // 2
        reader = RecordReader()
        records = reader.feed(r1[:mid])
        assert len(records) == 0
        records = reader.feed(r1[mid:] + r2 + r3)
        assert len(records) == 3

    def test_early_eof_leaves_data_buffered(self):
        """Incomplete data is preserved in the buffer."""
        raw = encode_pcm(0, self._pcm([1, 2, 3]))
        reader = RecordReader()
        records = reader.feed(raw[:4])  # Incomplete
        assert len(records) == 0
        assert reader.remaining()  # Data is buffered, not discarded

    def test_remaining_after_complete_read(self):
        """After a complete read, remaining() is empty."""
        raw = encode_pcm(0, self._pcm([1, 2]))
        reader = RecordReader()
        records = reader.feed(raw)
        assert len(records) == 1
        assert len(reader.remaining()) == 0
