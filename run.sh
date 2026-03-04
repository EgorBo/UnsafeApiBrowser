#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
RUNTIME_PATH="${1:?Usage: ./run.sh /path/to/dotnet/runtime}"
PORT=4000
export DOTNET_TieredCompilation=0

echo "Starting Unsafe API Browser on port $PORT..."
cd "$SCRIPT_DIR"
nohup env PORT=$PORT dotnet run -c Release --project "$SCRIPT_DIR" -- "$RUNTIME_PATH" \
    > "$SCRIPT_DIR/app.log" 2>&1 &
echo $! > "$SCRIPT_DIR/app.pid"
echo "PID: $(cat "$SCRIPT_DIR/app.pid")"
echo "Logs: $SCRIPT_DIR/app.log"
echo "Running at http://bot.egorbo.com:$PORT"
