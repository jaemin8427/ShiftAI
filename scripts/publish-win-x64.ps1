$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

dotnet publish "src\ShiftAI.App\ShiftAI.App.csproj" `
  -c Release `
  -r win-x64 `
  --self-contained false `
  -p:PublishSingleFile=false `
  -o "artifacts\publish\win-x64"
