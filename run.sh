#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
RUNTIME_PATH="${1:?Usage: ./run.sh /path/to/dotnet/runtime}"
INTERNAL_PORT=5100
PUBLIC_PORT=4000
export DOTNET_TieredCompilation=0

# Start the .NET app on internal port
echo "Starting Unsafe API Browser on internal port $INTERNAL_PORT..."
cd "$SCRIPT_DIR"
PORT=$INTERNAL_PORT dotnet run -c Release --project "$SCRIPT_DIR" -- "$RUNTIME_PATH" &
APP_PID=$!

# Wait for the app to be ready
echo "Waiting for app to start..."
for i in $(seq 1 30); do
    if curl -s http://localhost:$INTERNAL_PORT/ > /dev/null 2>&1; then
        echo "App is ready."
        break
    fi
    sleep 1
done

# Write a Caddyfile that only listens on public port (no port 80 needed)
CADDYFILE=$(mktemp)
cat > "$CADDYFILE" <<EOF
{
    admin off
    auto_https off
}

bot.egorbo.com:$PUBLIC_PORT {
    tls internal
    reverse_proxy localhost:$INTERNAL_PORT
}
EOF

echo "Starting Caddy on https://bot.egorbo.com:$PUBLIC_PORT..."
caddy run --config "$CADDYFILE" --adapter caddyfile &
CADDY_PID=$!

echo "Running at https://bot.egorbo.com:$PUBLIC_PORT"
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
