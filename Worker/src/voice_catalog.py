"""Voice catalog: manages voice profiles and reference assets.

A voice profile is a named entry that maps to:
- voice_id: canonical identifier used in requests
- revision: monotonic version bump on regeneration
- language: default language (e.g. "en")
- ref_wav_path: path to the original reference audio file
- transcript: exact transcript used to generate the voice clone prompt
- ref_hash: content hash of the reference WAV (integrity check)
- transcript_hash: hash of the transcript text
- prompt_data: the derived voice prompt (model-specific) for runtime synthesis

Profiles are model-specific. The model_id tags each profile so that
changing the runtime model does not silently use stale prompts.

Original reference assets are preserved so profiles can be regenerated
when changing runtime or model.
"""

from __future__ import annotations

import hashlib
import json
import logging
import os
from dataclasses import dataclass, field, asdict
from pathlib import Path
from typing import Dict, List, Optional

log = logging.getLogger("voice_catalog")


@dataclass
class VoiceProfile:
    voice_id: str
    model_id: str            # e.g. "qwen3-tts-0.6b-base"
    revision: int = 1
    language: str = "en"
    ref_wav_path: str = ""   # Original asset (preserved)
    transcript: str = ""     # Exact transcript used for clone
    ref_hash: str = ""       # SHA-256 of reference WAV
    transcript_hash: str = ""
    prompt_data: str = ""    # Model-specific cached voice prompt
    metadata: Dict = field(default_factory=dict)

    def to_dict(self) -> dict:
        return asdict(self)

    @classmethod
    def from_dict(cls, d: dict) -> "VoiceProfile":
        return cls(**d)


class VoiceCatalog:
    """Manages voice profiles on disk. Thread-safe for concurrent reads."""

    def __init__(self, catalog_dir: str, model_id: str):
        self._dir = Path(catalog_dir)
        self._model_id = model_id
        self._profiles: Dict[str, VoiceProfile] = {}
        self._manifest_path = self._dir / "manifest.json"
        self._ensure_dir()
        self._load()

    def _ensure_dir(self) -> None:
        self._dir.mkdir(parents=True, exist_ok=True)

    def _load(self) -> None:
        """Load manifest from disk."""
        if not self._manifest_path.exists():
            log.info("No existing catalog at %s; starting empty", self._manifest_path)
            self._save()
            return
        try:
            with open(self._manifest_path, "r") as f:
                data = json.load(f)
            for entry in data.get("voices", []):
                profile = VoiceProfile.from_dict(entry)
                # Only load profiles matching current model
                if profile.model_id == self._model_id:
                    self._profiles[profile.voice_id] = profile
            log.info("Loaded %d profiles for model %s", len(self._profiles), self._model_id)
        except Exception:
            log.exception("Failed to load voice catalog; starting fresh")
            self._profiles.clear()
            self._save()

    def _save(self) -> None:
        """Persist all profiles (including cross-model) for regeneration."""
        all_data = {"voices": [p.to_dict() for p in self._profiles.values()]}
        # Write atomically via temp file
        tmp = self._manifest_path.with_suffix(".tmp")
        with open(tmp, "w") as f:
            json.dump(all_data, f, indent=2)
        tmp.replace(self._manifest_path)

    def get(self, voice_id: str) -> Optional[VoiceProfile]:
        """Get a voice profile by ID. Only returns matching-model profiles."""
        return self._profiles.get(voice_id)

    def list(self) -> List[VoiceProfile]:
        """List all available profiles."""
        return list(self._profiles.values())

    def create(self, voice_id: str, language: str,
               ref_wav_path: str, transcript: str,
               prompt_data: str) -> VoiceProfile:
        """Create or update a voice profile. Bumps revision on update.

        Preserves the original reference WAV. Computes hashes for integrity.
        """
        # Compute hashes
        ref_hash = _file_hash(ref_wav_path) if os.path.exists(ref_wav_path) else ""
        transcript_hash = _text_hash(transcript)

        existing = self._profiles.get(voice_id)
        if existing and existing.model_id == self._model_id:
            revision = existing.revision + 1
        else:
            revision = 1

        profile = VoiceProfile(
            voice_id=voice_id,
            model_id=self._model_id,
            revision=revision,
            language=language,
            ref_wav_path=ref_wav_path,
            transcript=transcript,
            ref_hash=ref_hash,
            transcript_hash=transcript_hash,
            prompt_data=prompt_data,
        )
        self._profiles[voice_id] = profile
        self._save()
        log.info("Created/updated voice %s r%d", voice_id, revision)
        return profile

    def remove(self, voice_id: str) -> bool:
        """Remove a profile. Reference assets are preserved."""
        if voice_id in self._profiles:
            del self._profiles[voice_id]
            self._save()
            log.info("Removed voice %s (assets preserved)", voice_id)
            return True
        return False


# ── Helpers ─────────────────────────────────────────────────────────

def _file_hash(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(8192), b""):
            h.update(chunk)
    return h.hexdigest()


def _text_hash(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest()
