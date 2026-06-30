$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$modelDir = Join-Path $root "data\models"
$modelPath = Join-Path $modelDir "ggml-tiny.bin"
$url = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.bin"

New-Item -ItemType Directory -Force -Path $modelDir | Out-Null

if (Test-Path $modelPath) {
    Write-Host "Whisper model already exists: $modelPath"
    exit 0
}

Write-Host "Downloading Whisper tiny model..."
Invoke-WebRequest -Uri $url -OutFile $modelPath
Write-Host "Saved: $modelPath"
