"""Voice catalog tests: create, load, regenerate, cross-model isolation."""

import os
import pytest
import tempfile
import shutil

import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent.parent / "src"))

from voice_catalog import VoiceCatalog, VoiceProfile


@pytest.fixture
def catalog_dir(tmp_path):
    d = tmp_path / "voices"
    d.mkdir()
    return str(d)


@pytest.fixture
def catalog(catalog_dir):
    return VoiceCatalog(catalog_dir, "qwen3-tts-0.6b")


@pytest.fixture
def ref_wav(tmp_path):
    path = tmp_path / "dwarf.wav"
    path.write_bytes(b"\x00" * 44 + b"\x01" * 1000)  # Fake WAV
    return str(path)


# ── Create ──────────────────────────────────────────────────────────

class TestCreate:
    def test_create_basic_profile(self, catalog, ref_wav):
        profile = catalog.create(
            voice_id="dwarf",
            language="en",
            ref_wav_path=ref_wav,
            transcript="Hello there!",
            prompt_data="some_prompt_data",
        )
        assert profile.voice_id == "dwarf"
        assert profile.revision == 1
        assert profile.language == "en"
        assert profile.model_id == "qwen3-tts-0.6b"
        assert profile.ref_hash != ""  # Hash was computed
        assert profile.transcript_hash != ""

    def test_update_bumps_revision(self, catalog, ref_wav):
        p1 = catalog.create("dwarf", "en", ref_wav, "Hello", "prompt_v1")
        assert p1.revision == 1
        p2 = catalog.create("dwarf", "en", ref_wav, "Hello again", "prompt_v2")
        assert p2.revision == 2
        assert p1.transcript_hash != p2.transcript_hash

    def test_persists_across_reloads(self, catalog_dir):
        c1 = VoiceCatalog(catalog_dir, "model-a")
        c1.create("voice1", "en", "/fake/path", "transcript", "prompt")
        del c1

        c2 = VoiceCatalog(catalog_dir, "model-a")
        profile = c2.get("voice1")
        assert profile is not None
        assert profile.revision == 1
        assert profile.model_id == "model-a"


# ── Get / List ─────────────────────────────────────────────────────

class TestGetList:
    def test_get_existing(self, catalog, ref_wav):
        catalog.create("dwarf", "en", ref_wav, "Hi!", "prompt")
        profile = catalog.get("dwarf")
        assert profile is not None
        assert profile.transcript == "Hi!"

    def test_get_missing(self, catalog):
        assert catalog.get("nonexistent") is None

    def test_list_empty(self, catalog):
        assert catalog.list() == []

    def test_list_multiple(self, catalog, ref_wav):
        catalog.create("dwarf", "en", ref_wav, "Hi", "p1")
        catalog.create("elf", "fr", ref_wav, "Salut", "p2")
        assert len(catalog.list()) == 2


# ── Cross-model isolation ──────────────────────────────────────────

class TestCrossModel:
    def test_different_models_separate_profiles(self, catalog_dir):
        c_06 = VoiceCatalog(catalog_dir, "model-0.6b")
        c_17 = VoiceCatalog(catalog_dir, "model-1.7b")

        c_06.create("dwarf", "en", "/ref.wav", "transcript", "prompt_06")

        # The 1.7B catalog should NOT see the 0.6B profile
        assert c_17.get("dwarf") is None
        assert len(c_17.list()) == 0

        # But the 0.6B catalog still sees it
        assert c_06.get("dwarf") is not None

    def test_regeneration_uses_stored_hashes(self, catalog, ref_wav):
        catalog.create("dwarf", "en", ref_wav, "transcript", "prompt_v1")
        p1 = catalog.get("dwarf")
        catalog.create("dwarf", "en", ref_wav, "transcript", "prompt_v2")
        p2 = catalog.get("dwarf")
        assert p1.ref_hash == p2.ref_hash  # Same reference file
        assert p1.transcript_hash == p2.transcript_hash  # Same transcript
        assert p1.prompt_data != p2.prompt_data  # New prompt


# ── Remove ──────────────────────────────────────────────────────────

class TestRemove:
    def test_remove_existing(self, catalog, ref_wav):
        catalog.create("dwarf", "en", ref_wav, "Hi", "prompt")
        assert catalog.remove("dwarf")
        assert catalog.get("dwarf") is None

    def test_remove_nonexistent(self, catalog):
        assert not catalog.remove("no-such-voice")

    def test_remove_preserves_assets(self, catalog, ref_wav):
        catalog.create("dwarf", "en", ref_wav, "Hi", "prompt")
        catalog.remove("dwarf")
        # Reference WAV should still exist
        assert os.path.exists(ref_wav)
