#!/bin/bash
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PID_FILE="$SCRIPT_DIR/app.pid"

if [ -f "$PID_FILE" ]; then
    PID=$(cat "$PID_FILE")
    echo "Killing PID $PID..."
    kill "$PID" 2>/dev/null || true
    rm -f "$PID_FILE"
fi

# Also kill any remaining dotnet processes for this project
pkill -f "UnsafeApiBrowser" 2>/dev/null || true
echo "Done."
