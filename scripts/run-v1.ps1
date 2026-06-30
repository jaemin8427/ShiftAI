$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

dotnet build "ShiftAI.sln"

$getoExe = Join-Path $root "src\ShiftAI.GetoMock\bin\Debug\net8.0-windows\ShiftAI.GetoMock.exe"
$shiftExe = Join-Path $root "src\ShiftAI.App\bin\Debug\net8.0-windows\ShiftAI.App.exe"

if (-not (Test-Path $getoExe)) {
    throw "Geto Mock executable not found: $getoExe"
}

if (-not (Test-Path $shiftExe)) {
    throw "Shift AI executable not found: $shiftExe"
}

Get-Process ShiftAI.GetoMock -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process ShiftAI.App -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

Start-Process -FilePath $getoExe | Out-Null
Start-Sleep -Milliseconds 700
Start-Process -FilePath $shiftExe | Out-Null

Write-Host "Shift AI v1 started."
Write-Host "Geto Mock: $getoExe"
Write-Host "Shift AI : $shiftExe"
