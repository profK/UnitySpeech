#!/bin/bash
# Launch the TTS worker for integration tests.
# Usage: start_worker.sh [--port PORT] [--model MODEL_PATH]
# Returns: PID file at Worker/worker.pid

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"

PORT=8080
MODEL=""

while [[ $# -gt 0 ]]; do
    case $1 in
        --port) PORT="$2"; shift 2 ;;
        --model) MODEL="$2"; shift 2 ;;
        --help)
            echo "Usage: $0 [--port PORT] [--model MODEL_PATH]"
            echo "  --port PORT   Listen port (default: 8080)"
            echo "  --model PATH  Model path (auto-detected if omitted)"
            exit 0
            ;;
        *) echo "Unknown option: $1"; exit 1 ;;
    esac
done

# Kill any existing worker
if [ -f "$SCRIPT_DIR/worker.pid" ]; then
    EXISTING_PID=$(cat "$SCRIPT_DIR/worker.pid" 2>/dev/null)
    if [ -n "$EXISTING_PID" ] && kill -0 "$EXISTING_PID" 2>/dev/null; then
        echo "Killing existing worker (PID $EXISTING_PID)..."
        kill "$EXISTING_PID" 2>/dev/null || true
        sleep 2
    fi
    rm -f "$SCRIPT_DIR/worker.pid"
fi

# Start the worker
cd "$SCRIPT_DIR"

if [ -n "$MODEL" ]; then
    MODEL_ARG="--model $MODEL"
else
    MODEL_ARG=""
fi

echo "Starting TTS worker on port $PORT..."
"$SCRIPT_DIR/.venv/bin/python" "$SCRIPT_DIR/main.py" \
    --port "$PORT" \
    $MODEL_ARG \
    --device auto \
    --log-level info \
    > "$SCRIPT_DIR/worker.log" 2>&1 &

WORKER_PID=$!
echo "$WORKER_PID" > "$SCRIPT_DIR/worker.pid"

echo "Worker started (PID $WORKER_PID)"

# Wait for worker to be ready (max 30s)
MAX_WAIT=30
for i in $(seq 1 $MAX_WAIT); do
    if curl -s "http://127.0.0.1:$PORT/v1/health" > /dev/null 2>&1; then
        echo "Worker is ready!"
        exit 0
    fi
    sleep 1
done

echo "ERROR: Worker did not become ready within ${MAX_WAIT}s"
kill "$WORKER_PID" 2>/dev/null || true
rm -f "$SCRIPT_DIR/worker.pid"
exit 1
