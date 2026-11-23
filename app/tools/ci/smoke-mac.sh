#!/usr/bin/env bash
# Starts Mirage with --smoke on the macOS runner, screenshots the screen while
# it runs, and waits for its report.
set -euo pipefail
APP="$1"; OUT="$2"
mkdir -p "$OUT"
EXE="$(find "$APP/Contents/MacOS" -type f -perm -u+x | head -1)"
export MIRAGE_SMOKE_OUT="$OUT/smoke-macos.json"
"$EXE" --smoke > "$OUT/mac-stdout.txt" 2>&1 &
PID=$!
sleep 12; screencapture -x "$OUT/macos-runner-1.png" || true
sleep 8;  screencapture -x "$OUT/macos-runner-2.png" || true
for i in $(seq 1 60); do [ -f "$MIRAGE_SMOKE_OUT" ] && break; sleep 2; done
sleep 3
kill "$PID" 2>/dev/null || true
if [ ! -f "$MIRAGE_SMOKE_OUT" ]; then echo "Mirage did not write its smoke report"; cat "$OUT/mac-stdout.txt"; exit 1; fi
cat "$MIRAGE_SMOKE_OUT"
