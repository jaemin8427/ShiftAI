$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

dotnet run --project "tests\ShiftAI.Tests\ShiftAI.Tests.csproj"
