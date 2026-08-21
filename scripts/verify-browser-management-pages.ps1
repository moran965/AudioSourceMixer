param(
    [string] $ExecutablePath = (Join-Path $PSScriptRoot '..\artifacts\portable\AudioSourceMixer-0.2.2\AudioSourceMixer.exe'),
    [string] $ReportPath = (Join-Path $PSScriptRoot '..\artifacts\browser-management-pages.json')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Wait-For([scriptblock] $Probe, [string] $Description = 'UI Automation element', [int] $TimeoutSeconds = 15) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $value = & $Probe
        if ($null -ne $value) { return $value }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for $Description."
}

function Get-ApplicationWindow([int] $ProcessId) {
    $condition = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
    return [Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [Windows.Automation.TreeScope]::Children, $condition)
}

function Invoke-ApplicationButton($Window, [string] $AutomationName) {
    $name = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::NameProperty, $AutomationName)
    $type = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::ControlTypeProperty,
        [Windows.Automation.ControlType]::Button)
    $condition = New-Object Windows.Automation.AndCondition($name, $type)
    $button = $Window.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
    if ($null -eq $button) { throw "Button was not found: $AutomationName" }
    $pattern = $button.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}

function Find-BrowserAddress([string] $ProcessName, [string] $ExpectedAddress) {
    $expected = $ExpectedAddress.TrimEnd('/')
    $windows = [Windows.Automation.AutomationElement]::RootElement.FindAll(
        [Windows.Automation.TreeScope]::Children,
        [Windows.Automation.Condition]::TrueCondition)
    foreach ($window in $windows) {
        try {
            $process = Get-Process -Id $window.Current.ProcessId -ErrorAction Stop
            if (-not $process.ProcessName.Equals($ProcessName, [StringComparison]::OrdinalIgnoreCase)) { continue }
            $edits = $window.FindAll([Windows.Automation.TreeScope]::Descendants,
                (New-Object Windows.Automation.PropertyCondition(
                    [Windows.Automation.AutomationElement]::ControlTypeProperty,
                    [Windows.Automation.ControlType]::Edit)))
            foreach ($edit in $edits) {
                $valuePattern = $null
                if (-not $edit.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref] $valuePattern)) { continue }
                $value = ([Windows.Automation.ValuePattern] $valuePattern).Current.Value
                if ($value.TrimEnd('/').Equals($expected, [StringComparison]::OrdinalIgnoreCase)) { return $value }
            }
        } catch { }
    }
    return $null
}

$resolvedExecutable = [IO.Path]::GetFullPath($ExecutablePath)
if (-not (Test-Path -LiteralPath $resolvedExecutable)) { throw "Desktop executable not found: $resolvedExecutable" }
$initialChromeRunning = $null -ne (Get-Process chrome -ErrorAction SilentlyContinue | Select-Object -First 1)
$initialEdgeRunning = $null -ne (Get-Process msedge -ErrorAction SilentlyContinue | Select-Object -First 1)
$desktop = Start-Process -FilePath $resolvedExecutable -ArgumentList '--browser-setup' -PassThru

try {
    $window = Wait-For { Get-ApplicationWindow $desktop.Id } 'Audio Source Mixer window'
    $results = @()
    foreach ($browser in @(
        @{ Id='chrome'; Process='chrome'; Button='打开 Chrome 扩展管理页'; Address='chrome://extensions/'; InitiallyRunning=$initialChromeRunning },
        @{ Id='edge'; Process='msedge'; Button='打开 Edge 扩展管理页'; Address='edge://extensions/'; InitiallyRunning=$initialEdgeRunning }
    )) {
        Invoke-ApplicationButton $window $browser.Button
        $first = Wait-For { Find-BrowserAddress $browser.Process $browser.Address } "$($browser.Id) first management page"
        Write-Output "$($browser.Id) first address: $first"
        Invoke-ApplicationButton $window $browser.Button
        $second = Wait-For { Find-BrowserAddress $browser.Process $browser.Address } "$($browser.Id) second management page"
        Write-Output "$($browser.Id) second address: $second"
        $results += [ordered]@{
            browser = $browser.Id
            initiallyRunning = $browser.InitiallyRunning
            expectedAddress = $browser.Address
            firstAddressBarValue = $first
            secondAddressBarValue = $second
            firstNormalizedMatch = $first.TrimEnd('/').Equals($browser.Address.TrimEnd('/'), [StringComparison]::OrdinalIgnoreCase)
            secondNormalizedMatch = $second.TrimEnd('/').Equals($browser.Address.TrimEnd('/'), [StringComparison]::OrdinalIgnoreCase)
        }
    }
    $report = [ordered]@{
        timestamp = [DateTimeOffset]::Now.ToString('O')
        executable = $resolvedExecutable
        method = 'Invoked the real WPF buttons through UI Automation and read Chromium address-bar ValuePattern.'
        results = $results
    }
    $reportDirectory = Split-Path -Parent ([IO.Path]::GetFullPath($ReportPath))
    [IO.Directory]::CreateDirectory($reportDirectory) | Out-Null
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ReportPath -Encoding UTF8
    $report | ConvertTo-Json -Depth 5
}
finally {
    try {
        $signal = [Threading.EventWaitHandle]::OpenExisting('Local\AudioSourceMixer.Exit')
        $signal.Set() | Out-Null
        $signal.Dispose()
        $desktop.WaitForExit(10000) | Out-Null
    } catch { }
}
