#!/usr/bin/env python3
"""Runnable smoke benchmark for the TTS worker.

Measures client-observed latency from request submission to:
  1. First PCM record received
  2. First sustained non-silent audio
  3. Complete response

Run against the worker's raw PCM streaming endpoint:
    python probe_tts.py http://127.0.0.1:8080/v1/tts/stream

Use identical text and worker settings across runs. Run with Unity
closed first, then with the scene rendering.
"""

import csv
import json
import struct
import sys
import time
import urllib.request

url = sys.argv[1]
text = "Mind the step. I put it there forty years ago."
writer = csv.writer(sys.stdout)
writer.writerow(("trial", "first_pcm_ms", "onset_received_ms", "complete_ms",
                 "audio_seconds", "rtf_wall"))
for trial in range(-3, 20):
    request = urllib.request.Request(
        url, json.dumps({"text": text}).encode(),
        {"Content-Type": "application/json"},
    )
    started = time.perf_counter()
    first = onset = None
    count = 0
    pending = b""
    sum_squares = window_samples = 0
    with urllib.request.urlopen(request, timeout=60) as response:
        while True:
            block = response.read1(4096)
            arrived = time.perf_counter()
            if not block:
                break
            pending += block
            aligned = len(pending) & ~1
            if aligned and first is None:
                first = arrived - started
            count += aligned // 2
            if onset is None:
                for (sample,) in struct.iter_unpack("<h", pending[:aligned]):
                    sum_squares += sample * sample
                    window_samples += 1
                    if window_samples == 240:
                        if sum_squares / 240 >= (32768 * 10 ** (-45 / 20)) ** 2:
                            onset = arrived - started
                            break
                        sum_squares = window_samples = 0
            pending = pending[aligned:]
    complete = time.perf_counter() - started
    if pending or not count:
        raise ValueError("Invalid or empty PCM stream")
    audio_seconds = count / 24000
    if trial >= 0:
        writer.writerow((trial, round(first * 1000, 2),
                         "" if onset is None else round(onset * 1000, 2),
                         round(complete * 1000, 2), round(audio_seconds, 3),
                         round(complete / audio_seconds, 3)))
