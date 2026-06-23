#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$ROOT_DIR"

HOST="${LWM_HOST:-127.0.0.1}"
PORT="${LWM_PORT:-4177}"

echo "启动 Web 版本: http://${HOST}:${PORT}"
if [ "${LWM_NO_OPEN:-0}" != "1" ]; then
  if command -v open >/dev/null 2>&1; then
    (sleep 1; open "http://${HOST}:${PORT}") >/dev/null 2>&1 &
  elif command -v xdg-open >/dev/null 2>&1; then
    (sleep 1; xdg-open "http://${HOST}:${PORT}") >/dev/null 2>&1 &
  fi
fi
exec node Web/server.js
