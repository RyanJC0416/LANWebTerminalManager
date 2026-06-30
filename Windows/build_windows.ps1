param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$RootDir = Split-Path -Parent $PSScriptRoot
$ProjectDir = Join-Path $RootDir "Windows\LANWebTerminalManager"
$ReleaseDir = Join-Path $RootDir "release"
$AppVersion = "2.0.0"
$ProductName = "LANWebTerminalManager"
$ReleaseZip = Join-Path $ReleaseDir "$ProductName-v$AppVersion-Windows.zip"
$ReleaseStableZip = Join-Path $ReleaseDir "windows.zip"

Write-Host "Building LANWebTerminalManager Windows ($Configuration)..."

Push-Location $ProjectDir
try {
    dotnet publish -c $Configuration -r win-x64 --self-contained false -p:PublishSingleFile=false
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
}
finally {
    Pop-Location
}

$PublishDir = Join-Path $ProjectDir "bin\$Configuration\net8.0-windows\win-x64\publish"
if (-not (Test-Path $PublishDir)) {
    throw "Publish output not found: $PublishDir"
}

New-Item -ItemType Directory -Force -Path $ReleaseDir | Out-Null
if (Test-Path $ReleaseZip) { Remove-Item $ReleaseZip -Force }
if (Test-Path $ReleaseStableZip) { Remove-Item $ReleaseStableZip -Force }

Compress-Archive -Path (Join-Path $PublishDir "*") -DestinationPath $ReleaseZip -Force
Copy-Item $ReleaseZip $ReleaseStableZip -Force

Write-Host "Built: $PublishDir"
Write-Host "Release package: $ReleaseZip"
Write-Host "Release package: $ReleaseStableZip"
