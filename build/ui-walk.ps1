param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$OutDir,
    [switch]$Delete,
    # Optional window size in screen pixels (adaptive layout check), e.g. -Width 900 -Height 560.
    [int]$Width = 0,
    [int]$Height = 0
)

# Dev-only visual walk-through of demo mode via UI Automation: start, preview, (optionally) delete,
# screenshot each step. Controls are found by AutomationId, so this file stays ASCII-only
# (Windows PowerShell 5.1 reads BOM-less UTF-8 as ANSI).
# Usage: powershell -File build\ui-walk.ps1 -Exe <IwoCalendarBridge.exe> -OutDir <folder> [-Delete]
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$shot = Join-Path $PSScriptRoot 'screenshot.ps1'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$p = Start-Process -FilePath $Exe -ArgumentList '--demo' -PassThru
# Wait for the main window: Process caches MainWindowHandle, so refresh it.
for ($i = 0; $i -lt 40 -and $p.MainWindowHandle -eq [IntPtr]::Zero; $i++) {
    Start-Sleep -Milliseconds 250
    $p.Refresh()
}
Start-Sleep -Seconds 1

function Find-ById([System.Windows.Automation.AutomationElement]$scope, [string]$id) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $scope.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Click($element) {
    $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Shot([string]$name) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File $shot -ProcessId $p.Id -OutFile (Join-Path $OutDir $name) | Out-Null
}

function ShotWindow([System.Windows.Automation.AutomationElement]$window, [string]$name) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File $shot -Handle $window.Current.NativeWindowHandle -OutFile (Join-Path $OutDir $name) | Out-Null
}

function Click-Async($element) {
    # Invoke of a button that opens a modal window blocks UIA until the window closes; run it on a job thread.
    $runspace = [runspacefactory]::CreateRunspace(); $runspace.Open()
    $ps = [powershell]::Create(); $ps.Runspace = $runspace
    [void]$ps.AddScript({ param($e) $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }).AddArgument($element)
    [void]$ps.BeginInvoke()
}

$root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
if ($Width -gt 0 -and $Height -gt 0) {
    $transform = $root.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern)
    $transform.Move(40, 40)
    $transform.Resize($Width, $Height)
    Start-Sleep -Milliseconds 500
}
Shot '1-start.png'

Click (Find-ById $root 'PreviewButton')
Start-Sleep -Milliseconds 800
Shot '2-reading.png'
Start-Sleep -Seconds 4
Shot '3-preview.png'

if ($Delete) {
    Click-Async (Find-ById $root 'DeleteButton')
    Start-Sleep -Seconds 2
    $byProcess = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
    # The modal dialog is exposed by UIA as a child of the main window: find its field and walk up to the
    # nearest Window element - that is the dialog itself, not the main window.
    $dialog = $null
    foreach ($w in [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
            [System.Windows.Automation.TreeScope]::Children, $byProcess)) {
        $field = Find-ById $w 'PhraseBox'
        if ($field -ne $null) {
            $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
            $e = $walker.GetParent($field)
            while ($e -ne $null -and $e.Current.ControlType -ne [System.Windows.Automation.ControlType]::Window) {
                $e = $walker.GetParent($e)
            }
            $dialog = $e
        }
    }
    if ($dialog -ne $null) {
        ShotWindow $dialog '4a-confirm-empty.png'
        (Find-ById $dialog 'PhraseBox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('demo@example.test')
        Start-Sleep -Milliseconds 300
        ShotWindow $dialog '4b-confirm-typed.png'
        Click (Find-ById $dialog 'ConfirmDeleteButton')
        Start-Sleep -Milliseconds 900
        Shot '5-deleting.png'
        Start-Sleep -Seconds 6
        Shot '6-result.png'
    }
}

"pid $($p.Id)"
