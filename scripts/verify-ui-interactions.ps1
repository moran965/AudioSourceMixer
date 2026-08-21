param(
    [Parameter(Mandatory = $true)][string] $Executable,
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$root = Get-RepositoryRoot
$Executable = [IO.Path]::GetFullPath($Executable)
if (-not (Test-Path -LiteralPath $Executable -PathType Leaf)) { throw "Executable not found: $Executable" }
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $root 'artifacts\ui-interaction-v0.2.2'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
Assert-PathInsideRepository $OutputDirectory
if (Test-Path -LiteralPath $OutputDirectory) { Remove-Item -LiteralPath $OutputDirectory -Recurse -Force }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class AudioMixerInteractionInput {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
    [DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extraInfo);
    public const uint LeftDown = 0x0002;
    public const uint LeftUp = 0x0004;
    public const uint Wheel = 0x0800;
    public const uint KeyUp = 0x0002;
}
'@

function Decode-UiName([string] $Value) {
    return [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Value))
}
$uiName = @{
    drag = Decode-UiName '5ouW5Yqo5Lya6K+d5o6S5bqP'
    hide = Decode-UiName '6ZqQ6JeP5q2k5Lya6K+d'
    hidden = Decode-UiName '5p+l55yL6ZqQ6JeP5Lya6K+d'
    restore = Decode-UiName '5oGi5aSN5pi+56S6'
    restoreAll = Decode-UiName '5YWo6YOo5oGi5aSN5pi+56S6'
}

function Wait-Until([scriptblock] $Predicate, [string] $Message, [int] $TimeoutSeconds = 12) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $value = & $Predicate
        if ($null -ne $value -and $value -ne $false) { return $value }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw $Message
}

function Find-One([System.Windows.Automation.AutomationElement] $Parent, [string] $Name) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    return $Parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Find-All([System.Windows.Automation.AutomationElement] $Parent, [string] $Name) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    return @($Parent.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition))
}

function Find-ProcessOne([int] $ProcessId, [string] $Name) {
    $conditions = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId),
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty, $Name))
    return [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants, $conditions)
}

function Find-ProcessWindow([int] $ProcessId) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
    return [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Children, $condition)
}

function Find-VisibleHandles([System.Windows.Automation.AutomationElement] $Window, [string] $Name) {
    $windowBounds = $Window.Current.BoundingRectangle
    return @(Find-All $Window $Name | Where-Object {
        $bounds = $_.Current.BoundingRectangle
        -not $_.Current.IsOffscreen -and $bounds.Width -gt 0 -and $bounds.Height -gt 0 -and
            $bounds.Top -ge $windowBounds.Top -and $bounds.Bottom -le $windowBounds.Bottom
    } | Sort-Object { $_.Current.BoundingRectangle.Top })
}

function Invoke-Element([System.Windows.Automation.AutomationElement] $Element) {
    if ($null -eq $Element) { throw 'Cannot invoke a missing UI Automation element.' }
    $pattern = $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    ([System.Windows.Automation.InvokePattern]$pattern).Invoke()
}

function Center([System.Windows.Automation.AutomationElement] $Element) {
    $bounds = $Element.Current.BoundingRectangle
    return [Drawing.Point]::new([int]($bounds.Left + $bounds.Width / 2), [int]($bounds.Top + $bounds.Height / 2))
}

function Move-Mouse([Drawing.Point] $From, [Drawing.Point] $To, [int] $Steps = 18) {
    for ($step = 1; $step -le $Steps; $step++) {
        $x = [int]($From.X + ($To.X - $From.X) * $step / $Steps)
        $y = [int]($From.Y + ($To.Y - $From.Y) * $step / $Steps)
        [AudioMixerInteractionInput]::SetCursorPos($x, $y) | Out-Null
        Start-Sleep -Milliseconds 18
    }
}

function Capture-Window([System.Windows.Automation.AutomationElement] $Window, [string] $Name) {
    $bounds = $Window.Current.BoundingRectangle
    $width = [Math]::Max(1, [int][Math]::Ceiling($bounds.Width))
    $height = [Math]::Max(1, [int][Math]::Ceiling($bounds.Height))
    $bitmap = New-Object Drawing.Bitmap($width, $height)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen([int]$bounds.Left, [int]$bounds.Top, 0, 0, $bitmap.Size)
        $path = Join-Path $OutputDirectory $Name
        $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
        return $path
    } finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$process = $null
$screenshots = [Collections.Generic.List[string]]::new()
$checks = [Collections.Generic.List[string]]::new()
try {
    $process = Start-Process -FilePath $Executable -ArgumentList '--ui-interaction-test' -PassThru
    $window = Wait-Until { Find-ProcessWindow $process.Id } "Interactive diagnostic window did not appear."
    $window.SetFocus()
    $diagnosticLog = Join-Path ([IO.Path]::GetTempPath()) "AudioSourceMixer\ui-smoke\$($process.Id)\logs\AudioSourceMixer.log"

    # Verify source-menu and hidden-source Popup behavior before drag virtualization changes peers.
    for ($index = 0; $index -lt 2; $index++) {
        $window = Wait-Until { Find-ProcessWindow $process.Id } 'Interactive diagnostic window disappeared before hiding sources.'
        $menuHandles = Wait-Until {
            $found = Find-VisibleHandles $window $uiName.drag
            if ($found.Count -gt 0) { ,$found }
        } 'No visible card was available for the source-menu test.'
        $handlePoint = Center $menuHandles[0]
        [AudioMixerInteractionInput]::SetCursorPos($handlePoint.X + 47, $handlePoint.Y) | Out-Null
        [AudioMixerInteractionInput]::mouse_event([AudioMixerInteractionInput]::LeftDown, 0, 0, 0, [UIntPtr]::Zero)
        [AudioMixerInteractionInput]::mouse_event([AudioMixerInteractionInput]::LeftUp, 0, 0, 0, [UIntPtr]::Zero)
        $hide = Wait-Until { Find-ProcessOne $process.Id $uiName.hide } 'Source menu did not open.'
        Invoke-Element $hide
        Start-Sleep -Milliseconds 250
    }
    $window = Wait-Until { Find-ProcessWindow $process.Id } 'Interactive diagnostic window disappeared after hiding sources.'
    $hiddenButton = Wait-Until { Find-One $window $uiName.hidden } 'Hidden sources button did not become visible.'
    Invoke-Element $hiddenButton
    [void](Wait-Until { Find-ProcessOne $process.Id $uiName.restore } 'Hidden sources popup did not open.')
    $screenshots.Add((Capture-Window $window '06-hidden-popup-open.png'))
    Invoke-Element (Find-ProcessOne $process.Id $uiName.restore)
    Start-Sleep -Milliseconds 300
    if ($null -ne (Find-ProcessOne $process.Id $uiName.restore)) { throw 'Single restore left the hidden sources popup visible.' }
    $screenshots.Add((Capture-Window $window '07-single-restore-popup-closed.png'))
    $checks.Add('Single restore closed the popup before updating its content.')
    $window = Wait-Until { Find-ProcessWindow $process.Id } 'Interactive diagnostic window disappeared after single restore.'
    $hiddenButton = Wait-Until { Find-One $window $uiName.hidden } 'Hidden sources button disappeared while one source was still hidden.'
    Invoke-Element $hiddenButton
    $restoreAll = Wait-Until { Find-ProcessOne $process.Id $uiName.restoreAll } 'Hidden sources popup did not reopen.'
    Invoke-Element $restoreAll
    Start-Sleep -Milliseconds 300
    if ($null -ne (Find-ProcessOne $process.Id $uiName.restoreAll)) { throw 'Restore all left the hidden sources popup visible.' }
    $screenshots.Add((Capture-Window $window '08-restore-all-popup-closed.png'))
    $checks.Add('Restore all closed the popup without an orphan blank flyout.')

    $window = Wait-Until { Find-ProcessWindow $process.Id } 'Interactive diagnostic window disappeared before drag verification.'
    $handles = Wait-Until { $found = Find-VisibleHandles $window $uiName.drag; if ($found.Count -ge 2) { ,$found } } `
        'At least two drag handles were not visible.'
    $first = Center $handles[0]
    $second = Center $handles[1]

    [AudioMixerInteractionInput]::SetCursorPos($first.X, $first.Y) | Out-Null
    [AudioMixerInteractionInput]::mouse_event([AudioMixerInteractionInput]::LeftDown, 0, 0, 0, [UIntPtr]::Zero)
    $dragStarted = [Drawing.Point]::new($first.X, $first.Y + 12)
    Move-Mouse $first $dragStarted 6
    Start-Sleep -Milliseconds 250
    $screenshots.Add((Capture-Window $window '01-drag-start-full-card.png'))
    $targetY = [int]($window.Current.BoundingRectangle.Bottom - 130)
    $liveTarget = [Drawing.Point]::new($second.X, $targetY)
    Move-Mouse $dragStarted $liveTarget 22
    Start-Sleep -Milliseconds 260
    $screenshots.Add((Capture-Window $window '02-live-adjacent-reorder.png'))
    [AudioMixerInteractionInput]::mouse_event([AudioMixerInteractionInput]::LeftUp, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 350
    $screenshots.Add((Capture-Window $window '03-drop-complete.png'))
    $window = Wait-Until { Find-ProcessWindow $process.Id } 'Interactive diagnostic window disappeared after drop.'
    [void](Wait-Until {
        if (Test-Path -LiteralPath $diagnosticLog) {
            Select-String -LiteralPath $diagnosticLog -SimpleMatch 'Session drag preview committed. Changed=True' -Quiet
        }
    } 'The real mouse drop did not commit the live preview order.')
    $checks.Add('Real mouse drop order matched the live preview.')

    $handles = Wait-Until { $found = Find-VisibleHandles $window $uiName.drag; if ($found.Count -ge 1) { ,$found } } `
        'No visible drag handle was available for the Escape test.'
    $cancelStart = Center $handles[0]
    $startsBeforeCancel = if (Test-Path -LiteralPath $diagnosticLog) {
        @(Select-String -LiteralPath $diagnosticLog -SimpleMatch 'Session drag preview started.').Count
    } else { 0 }
    [AudioMixerInteractionInput]::SetForegroundWindow([IntPtr]$window.Current.NativeWindowHandle) | Out-Null
    $window.SetFocus()
    [AudioMixerInteractionInput]::SetCursorPos($cancelStart.X, $cancelStart.Y) | Out-Null
    Start-Sleep -Milliseconds 180
    [AudioMixerInteractionInput]::mouse_event([AudioMixerInteractionInput]::LeftDown, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 100
    $cancelTriggered = [Drawing.Point]::new($cancelStart.X, $cancelStart.Y + 14)
    Move-Mouse $cancelStart $cancelTriggered 6
    [void](Wait-Until {
        if (Test-Path -LiteralPath $diagnosticLog) {
            @(Select-String -LiteralPath $diagnosticLog -SimpleMatch 'Session drag preview started.').Count -gt $startsBeforeCancel
        }
    } 'The second real mouse drag did not start.')
    $bottom = [Drawing.Point]::new($cancelStart.X, [int]($window.Current.BoundingRectangle.Bottom - 38))
    Move-Mouse $cancelTriggered $bottom 26
    Start-Sleep -Milliseconds 850
    $screenshots.Add((Capture-Window $window '04-auto-scroll-during-drag.png'))
    [AudioMixerInteractionInput]::keybd_event(0x1B, 0, 0, [UIntPtr]::Zero)
    [AudioMixerInteractionInput]::keybd_event(0x1B, 0, [AudioMixerInteractionInput]::KeyUp, [UIntPtr]::Zero)
    [AudioMixerInteractionInput]::mouse_event([AudioMixerInteractionInput]::LeftUp, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 350
    $screenshots.Add((Capture-Window $window '05-escape-cancel-cleanup.png'))
    [void](Wait-Until {
        if (Test-Path -LiteralPath $diagnosticLog) {
            Select-String -LiteralPath $diagnosticLog -SimpleMatch 'Session drag preview cancelled.' -Quiet
        }
    } 'Escape did not cancel the active drag preview.')
    $checks.Add('Escape cancelled a real mouse drag after edge auto-scroll without leaving a preview.')

    $report = [ordered]@{
        executable = $Executable
        processId = $process.Id
        completedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        checks = $checks
        screenshots = $screenshots
    }
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'interaction-report.json') -Encoding UTF8
    Write-Output "Interactive UI verification passed: $OutputDirectory"
} finally {
    [AudioMixerInteractionInput]::mouse_event([AudioMixerInteractionInput]::LeftUp, 0, 0, 0, [UIntPtr]::Zero)
    if ($null -ne $process -and -not $process.HasExited) {
        try {
            try {
                $signal = [Threading.EventWaitHandle]::OpenExisting("Local\AudioSourceMixer.Exit.$($process.Id)")
                try { $signal.Set() | Out-Null } finally { $signal.Dispose() }
            } catch [Threading.WaitHandleCannotBeOpenedException] { }
            if (-not $process.WaitForExit(15000)) {
                Stop-Process -Id $process.Id -Force
                throw 'Interactive diagnostic did not exit after graceful restore signal.'
            }
        } finally { $process.Dispose() }
    } elseif ($null -ne $process) { $process.Dispose() }
}
