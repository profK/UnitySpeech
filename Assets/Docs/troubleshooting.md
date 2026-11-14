# Troubleshooting

Common issues and their solutions.

## Worker Issues

### Worker Won't Start

**Symptom:** `python main.py` hangs or errors immediately.

**Checks:**
1. Verify Python version: `python --version` (need 3.10+)
2. Verify virtual environment is activated: `which python` should show `.venv`
3. Check for port conflicts: `lsof -i :8080`
4. Check for missing dependencies: `pip install -r requirements.txt`

**Fix:**
```bash
# Kill any existing worker on port 8080
lsof -ti:8080 | xargs kill

# Reinstall dependencies
pip install -r requirements.txt

# Try stub mode first (no model)
python main.py
```

### Health Check Returns "degraded"

**Symptom:** `/v1/health` returns `{"status": "degraded"}`.

**Cause:** The model failed to load.

**Check the logs:**
```bash
# If started via start_worker.sh
cat Worker/worker.log

# If started manually, check console output
```

**Common causes:**
- Model path is wrong or model files are missing
- MPS device not available (try `--device cpu`)
- Out of GPU memory (try `--quantize 4bit`)
- Network issue downloading the model

**Fix:**
```bash
# Check if model exists
ls Worker/models/Qwen--Qwen3-TTS-12Hz-0.6B-Base/config.json

# Re-download if needed
python download_models.py --model base

# Try with explicit path
python main.py --model ./models/Qwen--Qwen3-TTS-12Hz-0.6B-Base --device mps
```

### "CUDA out of memory" Error

**Cause:** The model doesn't fit in GPU memory.

**Fix:**
```bash
# Try 4-bit quantization (uses ~4GB instead of ~8GB)
python main.py --quantize 4bit --device cuda --model ./models/...

# Or fall back to CPU
python main.py --device cpu --threads 8
```

## Unity Issues

### "Worker Unavailable" in Unity Console

**Symptom:** `OnFailed` fires with `WorkerUnavailable`.

**Cause:** Unity cannot reach the worker.

**Checks:**
1. Is the worker running? `curl http://127.0.0.1:8080/v1/health`
2. Does the endpoint match? Check `NpcTtsManager` Inspector
3. Is the correct port configured?

**Fix:**
- Start the worker before playing the scene
- Verify the endpoint URL in the Inspector
- For remote workers, ensure `--host 0.0.0.0` is set

### No Audio Playing

**Checks:**
1. Unity audio settings: **Edit → Project Settings → Audio**
   - Ensure the Audio Manager is enabled
   - Check that the default mixer is not muted
2. AudioListener exists in the scene
3. Worker is returning data (check console logs)
4. Spatial audio settings:
   - `minDistance` is 1m by default
   - `maxDistance` is 50m by default
   - Ensure the camera/AudioListener is within range

**Fix:**
- Place an AudioListener on the main camera
- Reduce `minDistance` or move the NPC closer
- Check that `Source.spatialBlend` is 1.0 (full 3D)

### Audio Sounds Distorted

**Cause:** Sample rate mismatch or buffer overflow.

**Checks:**
1. Worker is configured for 24 kHz (`--sample-rate 24000`, the default)
2. AudioClip is created with `24000` sample rate
3. PCM buffer is not overflowing

**Fix:**
- Verify the PcmBuffer capacity is sufficient (default: 5 seconds)
- Increase buffer capacity in the NpcTtsManager Inspector
- Check for underrun events in telemetry

### "UnknownVoice" Error

**Cause:** The voice ID doesn't exist in the worker's catalog.

**Fix:**
```bash
# List available voices
curl http://127.0.0.1:8080/v1/voices

# Use "default" for the built-in voice
# Or create a new voice
curl -X POST http://127.0.0.1:8080/v1/voices \
  -H "Content-Type: application/json" \
  -d '{"voice_id":"my-voice", "language":"en", "transcript":"Hello!", "ref_audio_path":"/path.wav"}'
```

## Model Issues

### Model Fails to Download

**Cause:** Network issues or authentication required.

**Fix:**
```bash
# Set your HuggingFace token
export HF_TOKEN="hf_..."

# Try again
python download_models.py --model base

# For users in China, use ModelScope instead
python download_models.py --model base --modelscope
```

### Model Load Takes Too Long

**Expected:** First load is ~25 seconds. Subsequent loads (warm) are ~5 seconds.

**Fix:**
- Keep the worker running between Unity sessions
- Use the `start_worker.sh` script for easy management

### Speech Sounds Robotic or Low Quality

**Check:**
1. Are you using the 0.6B or 1.7B model?
2. Is the cloned voice based on a good-quality recording?
3. Is the language code correct?

**Fix:**
- Try the 1.7B Base model for higher quality: `python download_models.py --model voicedesign`
- Re-record reference audio in a quieter environment
- Verify the language matches the spoken content

## Performance Issues

### High Latency

**Measure first:**
```bash
python probe_tts.py http://127.0.0.1:8080/v1/tts/stream
```

**If RTF > 1.0 (slower than real-time):**
- Try GPU inference: `--device mps` (Mac) or `--device cuda` (NVIDIA)
- Reduce text length per utterance
- Increase queue size: `--queue 16`

**If p95 latency is high:**
- The model may be under heavy load from multiple NPCs
- Consider using a larger queue or staggered request scheduling
- Check for CPU throttling on laptop hardware

### FPS Drops During Synthesis

**Cause:** CPU inference competes with rendering threads.

**Fix:**
- Use GPU inference (`--device mps` or `--device cuda`)
- Limit CPU threads: `--threads 2` (leaves cores for Unity)
- Reduce Unity's thread count in **Edit → Project Settings → Player**

## Editor-Specific

### Editor Window Is Blank or Missing

**Cause:** The package is not imported or assembly definitions are broken.

**Fix:**
1. Check **Packages → Project Dependencies** for `com.npctts.unity`
2. Verify `com.npctts.unity.asmdef` and `com.npctts.unity.editor.asmdef` exist
3. Try **Assets → Reserialize All**
4. Delete `Library/` and reimport

### Tests Fail in EditMode

**Cause:** The protocol decoder has a bug.

**Fix:**
- Run `python -m pytest tests/test_protocol.py -v` to check the Python side
- Run EditMode tests in Unity: **Window → General → Test Runner → EditMode → Run All**

### Tests Fail in PlayMode with Integration

**Cause:** The worker is not running or not reachable.

**Fix:**
- The integration tests try to start the worker automatically
- Ensure `Worker/.venv` exists and `Worker/main.py` is present
- Set a longer timeout in the test fixture if needed
- For manual testing, start the worker before running tests
