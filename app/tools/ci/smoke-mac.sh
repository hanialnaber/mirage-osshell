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
if [ ! -f "$MIRAGE_SMOKE_OUT" ] && kill -0 "$PID" 2>/dev/null; then
  # Still running but stuck: record where every thread is waiting.
  sample "$PID" 3 -file "$OUT/mac-sample.txt" > /dev/null 2>&1 || true
fi
kill "$PID" 2>/dev/null || true
if [ ! -f "$MIRAGE_SMOKE_OUT" ]; then
  echo "Mirage did not write its smoke report"; cat "$OUT/mac-stdout.txt"
  [ -f "$OUT/mac-sample.txt" ] && grep -E "Thread_|^ +[0-9]+ .*(mirage|osascript|Mutex|recv|wait)" "$OUT/mac-sample.txt" | head -120
  exit 1
fi
cat "$MIRAGE_SMOKE_OUT"
