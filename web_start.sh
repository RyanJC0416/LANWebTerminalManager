#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$ROOT_DIR"

HOST="${LWM_HOST:-127.0.0.1}"
PORT="${LWM_PORT:-4177}"

echo "启动 Web 版本: http://${HOST}:${PORT}"
exec node Web/server.js
