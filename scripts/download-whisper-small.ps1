# Downloads the Korean-capable Whisper model (ggml-small, ~465MB) into data/models.
# The model binary is intentionally NOT committed to git (see .gitignore: data/models/*.bin) because
# it exceeds GitHub's 100MB per-file limit. Run this once after cloning if you want Whisper STT.
# Without a model, the app falls back to the built-in Windows STT (ko-KR), then a demo provider.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $root 'data\models\ggml-small.bin'
$dir = Split-Path -Parent $dest
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }

if ((Test-Path $dest) -and ((Get-Item $dest).Length -gt 400MB)) {
    Write-Host "Already present: $dest ($([math]::Round((Get-Item $dest).Length/1MB,1)) MB)"
    return
}

$url = 'https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin?download=true'
Write-Host "Downloading ggml-small.bin (~465MB) ..."
$ProgressPreference = 'SilentlyContinue'
Invoke-WebRequest -Uri $url -OutFile $dest -UseBasicParsing
Write-Host "Saved: $dest ($([math]::Round((Get-Item $dest).Length/1MB,1)) MB)"
