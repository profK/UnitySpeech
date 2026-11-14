#!/usr/bin/env python3
"""Qwen3-TTS Worker – standalone HTTP server.

A persistent text-to-speech synthesis server for Unity NPC dialogue.
Supports CPU, MPS (Apple Silicon GPU), and CUDA inference.

Usage:
    python main.py                              # Stub mode, port 8080
    python main.py --model Qwen/Qwen3-TTS-12Hz-0.6B-Base --device mps
    python main.py --model ./models/Qwen--Qwen3-TTS-12Hz-0.6B-Base
    python main.py --device cpu --threads 8
    python main.py --quantize 4bit --model ./models/... --device cpu

The worker binds to 127.0.0.1 by default (loopback only).
For remote deployments, set --host and use authenticated transport.
"""

import argparse
import logging
import sys
from pathlib import Path

# Ensure src/ is on the path (for both `python main.py` and module imports)
_sys_src = str(Path(__file__).parent / "src")
if _sys_src not in sys.path:
    sys.path.insert(0, _sys_src)

import uvicorn

from server import create_app

_DEFAULT_PORT = 8080
_DEFAULT_HOST = "127.0.0.1"


def main():
    parser = argparse.ArgumentParser(
        description="Qwen3-TTS Worker – persistent speech synthesis server",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument("--host", default=_DEFAULT_HOST,
                        help="Listen address (default: 127.0.0.1 loopback)")
    parser.add_argument("--port", type=int, default=_DEFAULT_PORT,
                        help="Listen port (default: 8080)")
    parser.add_argument("--device", default="auto",
                        choices=["auto", "cpu", "mps", "cuda"],
                        help="Inference device (default: auto-detect)")
    parser.add_argument("--threads", type=int, default=4,
                        help="CPU threads for inference (default: 4)")
    parser.add_argument("--model", default="",
                        help="Model path or HF repo ID (e.g. Qwen/Qwen3-TTS-12Hz-0.6B-Base). "
                             "Omit for stub mode.")
    parser.add_argument("--quantize", default="",
                        choices=["", "4bit", "8bit"],
                        help="Quantization mode (requires bitsandbytes for 4bit)")
    parser.add_argument("--queue", type=int, default=8,
                        help="Max queued requests (default: 8)")
    parser.add_argument("--voices", default="voices",
                        help="Voice catalog directory (default: voices)")
    parser.add_argument("--log-level", default="info",
                        choices=["debug", "info", "warning", "error"],
                        help="Logging level (default: info)")
    args = parser.parse_args()

    # Configure logging
    logging.basicConfig(
        level=getattr(logging, args.log_level.upper(), logging.INFO),
        format="%(asctime)s [%(name)s] %(levelname)s: %(message)s",
        datefmt="%H:%M:%S",
    )

    # Resolve model path
    model_path = args.model
    if not model_path:
        # Auto-detect: look for the 0.6B model in the local models/ directory
        script_dir = Path(__file__).parent
        candidates = [
            script_dir / "models" / "Qwen--Qwen3-TTS-12Hz-0.6B-Base",
            script_dir / "models" / "Qwen--Qwen3-TTS-12Hz-1.7B-Base",
        ]
        for c in candidates:
            if (c / "config.json").exists():
                model_path = str(c)
                break
        if not model_path:
            logging.getLogger("root").warning(
                "No model found. Using stub mode. Run download_models.py first.")

    # Create app
    app = create_app(
        model_path=model_path,
        device=args.device,
        cpu_threads=args.threads,
        quantize=args.quantize,
        listen_host=args.host,
        listen_port=args.port,
        max_queue=args.queue,
        voices_dir=args.voices,
    )

    # Start server
    log = logging.getLogger("main")
    log.info("Starting Qwen3-TTS Worker on %s:%d", args.host, args.port)
    log.info("Device: %s | Threads: %d | Queue: %d", args.device, args.threads, args.queue)
    if model_path:
        log.info("Model: %s", model_path)
    else:
        log.info("Mode: STUB (no model loaded)")

    uvicorn.run(
        app,
        host=args.host,
        port=args.port,
        log_level=args.log_level,
    )


if __name__ == "__main__":
    main()
