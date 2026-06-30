$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

dotnet build "ShiftAI.sln"
dotnet run --project "tests\ShiftAI.Tests\ShiftAI.Tests.csproj"

Get-Process ShiftAI.GetoMock -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

dotnet build "src\ShiftAI.GetoMock\ShiftAI.GetoMock.csproj"
$getoExe = Join-Path $root "src\ShiftAI.GetoMock\bin\Debug\net8.0-windows\ShiftAI.GetoMock.exe"
$geto = Start-Process -FilePath $getoExe -PassThru

try {
    Start-Sleep -Seconds 2
    dotnet run --project "tests\ShiftAI.HandSmoke\ShiftAI.HandSmoke.csproj"
}
finally {
    if ($null -ne $geto -and -not $geto.HasExited) {
        $geto | Stop-Process -Force
    }
}

Write-Host "Shift AI v1 checks passed."
