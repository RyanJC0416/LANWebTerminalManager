#!/usr/bin/env bash
set -euo pipefail

echo "LANWebTerminalManager Web dependency installer (macOS)"

if ! command -v brew >/dev/null 2>&1; then
  echo "Homebrew is missing. Installing Homebrew first..."
  /bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"
fi

if ! command -v node >/dev/null 2>&1; then
  echo "Installing Node.js..."
  brew install node
else
  echo "Node.js already installed: $(node -v)"
fi

if ! command -v python3 >/dev/null 2>&1; then
  echo "Installing Python..."
  brew install python
else
  echo "Python already installed: $(python3 --version)"
fi

echo
echo "Dependencies are ready. Start the web version with:"
echo "./web_start.sh"
