@echo off
setlocal

set "ROOT_DIR=%~dp0"
cd /d "%ROOT_DIR%"

if "%LWM_HOST%"=="" set "LWM_HOST=127.0.0.1"
if "%LWM_PORT%"=="" set "LWM_PORT=4177"

where node >nul 2>nul
if errorlevel 1 (
  echo Node.js is missing. Please install Node.js 20+ first.
  echo You can run install_windows.ps1 from PowerShell, or install from https://nodejs.org/
  pause
  exit /b 1
)

if not "%LWM_NO_OPEN%"=="1" start "" "http://%LWM_HOST%:%LWM_PORT%"
echo Starting LANWebTerminalManager Web: http://%LWM_HOST%:%LWM_PORT%
node Web\server.js
