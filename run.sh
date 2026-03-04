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

# Write a Caddyfile that only listens on port 4000 (no port 80 needed)
CADDYFILE=$(mktemp)
cat > "$CADDYFILE" <<EOF
{
    http_port 0
    https_port 4000
}

bot.egorbo.com:4000 {
    reverse_proxy localhost:$PORT
}
EOF

echo "Starting Caddy on https://bot.egorbo.com:4000..."
caddy run --config "$CADDYFILE" --adapter caddyfile &
CADDY_PID=$!

echo "Running at https://bot.egorbo.com:4000"
echo "Press Ctrl+C to stop."

cleanup() {
    echo "Shutting down..."
    kill $APP_PID 2>/dev/null || true
    kill $CADDY_PID 2>/dev/null || true
    rm -f "$CADDYFILE"
    wait
}
trap cleanup EXIT INT TERM

wait
