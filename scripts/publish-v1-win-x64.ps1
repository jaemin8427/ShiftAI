$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$publishRoot = Join-Path $root "artifacts\publish\win-x64"
$shiftOut = Join-Path $publishRoot "ShiftAI.App"
$getoOut = Join-Path $publishRoot "ShiftAI.GetoMock"

Get-Process ShiftAI.App -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process ShiftAI.GetoMock -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

dotnet publish "src\ShiftAI.App\ShiftAI.App.csproj" `
  -c Release `
  -r win-x64 `
  --self-contained false `
  -p:PublishSingleFile=false `
  -o $shiftOut

dotnet publish "src\ShiftAI.GetoMock\ShiftAI.GetoMock.csproj" `
  -c Release `
  -r win-x64 `
  --self-contained false `
  -p:PublishSingleFile=false `
  -o $getoOut

Write-Host "Shift AI published: $shiftOut\ShiftAI.App.exe"
Write-Host "Geto Mock published: $getoOut\ShiftAI.GetoMock.exe"
