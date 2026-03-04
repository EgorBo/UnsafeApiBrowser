#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
RUNTIME_PATH="${1:?Usage: ./run.sh /path/to/dotnet/runtime}"
PORT=4000
export DOTNET_TieredCompilation=0

echo "Starting Unsafe API Browser on port $PORT..."
cd "$SCRIPT_DIR"
PORT=$PORT dotnet run -c Release --project "$SCRIPT_DIR" -- "$RUNTIME_PATH" &
APP_PID=$!

echo "Running at http://bot.egorbo.com:$PORT"
echo "Press Ctrl+C to stop."

cleanup() {
    echo "Shutting down..."
    kill $APP_PID 2>/dev/null || true
    wait
}
trap cleanup EXIT INT TERM

wait
