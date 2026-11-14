#!/bin/bash
# Stop the TTS worker.
# Usage: stop_worker.sh

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [ -f "$SCRIPT_DIR/worker.pid" ]; then
    WORKER_PID=$(cat "$SCRIPT_DIR/worker.pid" 2>/dev/null)
    if [ -n "$WORKER_PID" ] && kill -0 "$WORKER_PID" 2>/dev/null; then
        echo "Stopping worker (PID $WORKER_PID)..."
        kill "$WORKER_PID" 2>/dev/null || true
        sleep 1
        # Force kill if still running
        if kill -0 "$WORKER_PID" 2>/dev/null; then
            kill -9 "$WORKER_PID" 2>/dev/null || true
        fi
        echo "Worker stopped."
    else
        echo "Worker is not running."
    fi
    rm -f "$SCRIPT_DIR/worker.pid"
else
    echo "No PID file found. Worker is not running."
fi

# Also kill any stray worker processes
pkill -f "python.*main.py.*port" 2>/dev/null || true
