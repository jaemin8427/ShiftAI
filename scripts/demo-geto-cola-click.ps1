$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

dotnet build "src\ShiftAI.GetoMock\ShiftAI.GetoMock.csproj" | Out-Host

$exe = Join-Path $root "src\ShiftAI.GetoMock\bin\Debug\net8.0-windows\ShiftAI.GetoMock.exe"
$process = Start-Process -FilePath $exe -PassThru

Start-Sleep -Seconds 2

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Wait-ElementById {
    param(
        [System.Windows.Automation.AutomationElement] $Root,
        [string] $AutomationId,
        [int] $TimeoutSeconds = 8
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId
    )

    while ((Get-Date) -lt $deadline) {
        $element = $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($null -ne $element) {
            return $element
        }

        Start-Sleep -Milliseconds 200
    }

    throw "UI element not found: $AutomationId"
}

function Invoke-Element {
    param([System.Windows.Automation.AutomationElement] $Element)

    $pattern = $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}

$windowCondition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty,
    "Geto Mock"
)

$desktop = [System.Windows.Automation.AutomationElement]::RootElement
$window = $null
$deadline = (Get-Date).AddSeconds(8)

while ((Get-Date) -lt $deadline) {
    $window = $desktop.FindFirst([System.Windows.Automation.TreeScope]::Children, $windowCondition)
    if ($null -ne $window) {
        break
    }

    Start-Sleep -Milliseconds 200
}

if ($null -eq $window) {
    throw "Geto Mock window was not found."
}

$foodOrderButton = Wait-ElementById -Root $window -AutomationId "FoodOrderButton"
Invoke-Element -Element $foodOrderButton
Start-Sleep -Milliseconds 900

$colaButton = Wait-ElementById -Root $window -AutomationId "ColaItemButton"
Invoke-Element -Element $colaButton
Start-Sleep -Milliseconds 900

Write-Host "Demo complete: clicked 음식주문 -> 콜라. The Geto Mock window is left open for inspection."
