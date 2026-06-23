$ErrorActionPreference = "Stop"

Write-Host "LANWebTerminalManager Web dependency installer (Windows)"

function Test-Command($Name) {
    $null -ne (Get-Command $Name -ErrorAction SilentlyContinue)
}

if (-not (Test-Command winget)) {
    Write-Host "winget is not available on this Windows installation."
    Write-Host "Please install Node.js from https://nodejs.org/ and Python from https://www.python.org/downloads/"
    exit 1
}

if (-not (Test-Command node)) {
    Write-Host "Installing Node.js LTS..."
    winget install --id OpenJS.NodeJS.LTS -e --accept-package-agreements --accept-source-agreements
} else {
    Write-Host "Node.js already installed: $(node --version)"
}

$hasPython = (Test-Command py) -or (Test-Command python) -or (Test-Command python3)
if (-not $hasPython) {
    Write-Host "Installing Python 3..."
    winget install --id Python.Python.3.12 -e --accept-package-agreements --accept-source-agreements
} else {
    Write-Host "Python already installed."
}

Write-Host ""
Write-Host "Dependencies are ready. Start the web version with:"
Write-Host ".\web_start.bat"
