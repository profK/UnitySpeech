#!/usr/bin/env python3
"""Download Qwen3-TTS models from Hugging Face.

Usage:
    python download_models.py                  # Download all required models
    python download_models.py --model base     # Download only 0.6B Base (runtime)
    python download_models.py --model voicedesign  # Download only 1.7B VoiceDesign (audition)
    python download_models.py --list            # List available models

Models are downloaded to the 'models/' directory next to this script.
Set HF_TOKEN in your environment if the models require authentication.

Models:
    base (Qwen3-TTS-12Hz-0.6B-Base):
        Runtime synthesis model for text-to-speech with voice cloning.
        Required for the worker.

    voicedesign (Qwen3-TTS-12Hz-1.7B-VoiceDesign):
        Voice design/audition model for creating reference prompts from
        text descriptions. Optional; use for voice creation workflow.

    tokenizer (Qwen3-TTS-Tokenizer-12Hz):
        Speech tokenizer shared by all models. Downloaded automatically
        by qwen-tts library on first use.
"""

import argparse
import os
import shutil
import sys
from pathlib import Path

# Model registry: maps short name -> HF repo ID + metadata
_MODELS = {
    "base": {
        "id": "Qwen/Qwen3-TTS-12Hz-0.6B-Base",
        "name": "Qwen3-TTS 0.6B Base",
        "description": "Runtime synthesis with voice cloning (required)",
        "required": True,
    },
    "voicedesign": {
        "id": "Qwen/Qwen3-TTS-12Hz-1.7B-VoiceDesign",
        "name": "Qwen3-TTS 1.7B VoiceDesign",
        "description": "Voice design/audition from text descriptions (optional)",
        "required": False,
    },
}


def main():
    parser = argparse.ArgumentParser(description="Download Qwen3-TTS models")
    parser.add_argument("--model", choices=["base", "voicedesign"],
                        help="Download a specific model")
    parser.add_argument("--list", action="store_true", help="List available models")
    parser.add_argument("--dir", default="models", help="Output directory")
    parser.add_argument("--huggingface", action="store_true",
                        help="Use HuggingFace Hub (default). Use --modelscope for China mainland.")
    parser.add_argument("--modelscope", action="store_true",
                        help="Use ModelScope (recommended for users in Mainland China)")
    args = parser.parse_args()

    script_dir = Path(__file__).parent
    models_dir = Path(args.dir) if Path(args.dir).is_absolute() else script_dir / args.dir

    if args.list:
        for key, info in _MODELS.items():
            status = _check_local(info["id"], models_dir)
            print(f"  {key:15s}  {'✅' if status else '❌'}  {info['id']:50s}  {info['name']}")
            print(f"                   {info['description']}")
        # Also check tokenizer
        tok_id = "Qwen/Qwen3-TTS-Tokenizer-12Hz"
        tok_status = _check_local(tok_id, models_dir)
        print(f"  tokenizer       {'✅' if tok_status else '❌'}  {tok_id:50s}  Shared tokenizer (auto-downloaded)")
        return

    if args.model:
        _download_one(args.model, models_dir, args.modelscope)
    else:
        for key in _MODELS:
            _download_one(key, models_dir, args.modelscope)


def _check_local(hf_id: str, models_dir: Path) -> bool:
    """Check if a model appears to be present locally."""
    dir_name = hf_id.replace("/", "--")
    local_path = models_dir / dir_name
    return local_path.exists() and (local_path / "config.json").exists()


def _download_one(key: str, models_dir: Path, use_modelscope: bool = False) -> None:
    info = _MODELS[key]
    hf_id = info["id"]
    dir_name = hf_id.replace("/", "--")
    local_path = models_dir / dir_name

    if _check_local(hf_id, models_dir):
        print(f"[SKIP] {info['name']} already at {local_path}")
        return

    print(f"[DOWNLOAD] {info['name']} ({hf_id}) ...")
    models_dir.mkdir(parents=True, exist_ok=True)

    if use_modelscope:
        _download_modelscope(hf_id, local_path)
    else:
        _download_huggingface(hf_id, local_path)


def _download_huggingface(repo_id: str, local_path: Path) -> None:
    """Download using HuggingFace Hub CLI or Python API."""
    try:
        from huggingface_hub import snapshot_download
        print(f"  Downloading from HuggingFace Hub...")
        snapshot_download(
            repo_id=repo_id,
            local_dir=str(local_path),
            local_dir_use_symlinks=False,
        )
        print(f"  ✅ Downloaded to {local_path}")
    except ImportError:
        print("  ❌ huggingface_hub not installed. Run: pip install 'huggingface_hub[cli]'")
        print("  Then run: huggingface-cli download {} --local-dir {}".format(
            repo_id, local_path))
        sys.exit(1)
    except Exception as e:
        print(f"  ❌ Download failed: {e}")
        print(f"  Check HF_TOKEN if authentication is required.")
        sys.exit(1)


def _download_modelscope(repo_id: str, local_path: Path) -> None:
    """Download using ModelScope CLI (recommended for China mainland)."""
    try:
        from modelscope import snapshot_download
        print(f"  Downloading from ModelScope...")
        snapshot_download(
            model_id=repo_id,
            cache_dir=str(local_path.parent),
            local_dir=str(local_path),
        )
        print(f"  ✅ Downloaded to {local_path}")
    except ImportError:
        print("  ❌ modelscope not installed. Run: pip install modelscope")
        print(f"  Then run: modelscope download --model {repo_id} --local_dir {local_path}")
        sys.exit(1)
    except Exception as e:
        print(f"  ❌ Download failed: {e}")
        sys.exit(1)


if __name__ == "__main__":
    main()
