#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
RUNTIME_PATH="${1:?Usage: ./run.sh /path/to/dotnet/runtime}"
PORT=4000

# Start the .NET app
echo "Starting Unsafe API Browser on port $PORT..."
cd "$SCRIPT_DIR"
PORT=$PORT dotnet run --project "$SCRIPT_DIR" -- "$RUNTIME_PATH" &
APP_PID=$!

# Wait for the app to be ready
echo "Waiting for app to start..."
for i in $(seq 1 30); do
    if curl -s http://localhost:$PORT/ > /dev/null 2>&1; then
        echo "App is ready."
        break
    fi
    sleep 1
done

# Start Caddy reverse proxy
echo "Starting Caddy reverse proxy for bot.egorbo.com:4000 -> localhost:$PORT..."
caddy reverse-proxy --from bot.egorbo.com:4000 --to localhost:$PORT &
CADDY_PID=$!

echo "Running at https://bot.egorbo.com:4000"
echo "Press Ctrl+C to stop."

cleanup() {
    echo "Shutting down..."
    kill $APP_PID 2>/dev/null || true
    kill $CADDY_PID 2>/dev/null || true
    wait
}
trap cleanup EXIT INT TERM

wait
