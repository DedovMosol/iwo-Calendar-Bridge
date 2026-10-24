param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$OutDir,
    [int]$Width = 1120,
    [int]$Height = 780
)

# Dev-only: README screenshots of every screen in demo mode (synthetic data, no network, no Outlook).
#   copy-1-source.png    copy tab: source (Outlook calendars incl. shared), search by name, period
#   copy-2-preview.png   copy preview with per-row checkboxes
#   copy-3-confirm.png   copy confirmation
#   copy-4-result.png    copy result
#   cleanup-1-choose.png cleanup tab: mailbox, period
#   cleanup-2-preview.png, cleanup-3-confirm.png, cleanup-4-result.png
# Controls are found by AutomationId; the file is ASCII-only (Windows PowerShell 5.1 reads BOM-less UTF-8 as ANSI).
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$shot = Join-Path $PSScriptRoot 'screenshot.ps1'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$AE = [System.Windows.Automation.AutomationElement]

$p = Start-Process -FilePath $Exe -ArgumentList '--demo' -PassThru
for ($i = 0; $i -lt 40 -and $p.MainWindowHandle -eq [IntPtr]::Zero; $i++) { Start-Sleep -Milliseconds 250; $p.Refresh() }
Start-Sleep -Seconds 1
$byProcess = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $p.Id)

function Find-ById($scope, [string]$id) {
    $c = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id)
    return $scope.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}
function Click($e) { $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Click-Async($e) {
    # Invoke of a button that opens a modal window blocks UIA until it closes: run it on another runspace.
    $rs = [runspacefactory]::CreateRunspace(); $rs.Open()
    $ps = [powershell]::Create(); $ps.Runspace = $rs
    [void]$ps.AddScript({ param($x) $x.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }).AddArgument($e)
    [void]$ps.BeginInvoke()
}
function Select-Tab($id) { (Find-ById $root $id).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
function Shot([string]$name) { & powershell -NoProfile -ExecutionPolicy Bypass -File $shot -ProcessId $p.Id -OutFile (Join-Path $OutDir $name) | Out-Null }
function ShotWindow($w, [string]$name) { & powershell -NoProfile -ExecutionPolicy Bypass -File $shot -Handle $w.Current.NativeWindowHandle -OutFile (Join-Path $OutDir $name) | Out-Null }
function Find-MessageBox {
    # MessageBox (class #32770) owned by the main window.
    for ($i = 0; $i -lt 20; $i++) {
        $c = New-Object System.Windows.Automation.PropertyCondition($AE::ClassNameProperty, '#32770')
        $box = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $c)
        if ($box -eq $null) { $box = $AE::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.AndCondition($byProcess, $c))) }
        if ($box -ne $null) { return $box }
        Start-Sleep -Milliseconds 250
    }
    return $null
}
function Find-Owned([string]$fieldId) {
    foreach ($w in $AE::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $byProcess)) {
        $field = Find-ById $w $fieldId
        if ($field -ne $null) {
            $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
            $e = $walker.GetParent($field)
            while ($e -ne $null -and $e.Current.ControlType -ne [System.Windows.Automation.ControlType]::Window) { $e = $walker.GetParent($e) }
            return $e
        }
    }
    return $null
}

$root = $AE::FromHandle($p.MainWindowHandle)
$t = $root.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern)
$t.Move(40, 40); $t.Resize($Width, $Height)
Start-Sleep -Milliseconds 800

# --- Copy ---
Select-Tab 'CopyTab'
Start-Sleep -Milliseconds 600
Shot 'copy-1-source.png'
Click (Find-ById $root 'CopyPreviewButton')
Start-Sleep -Seconds 2
Shot 'copy-2-preview.png'
Click-Async (Find-ById $root 'CopyButton')
Start-Sleep -Seconds 2
$box = Find-Owned 'ConfirmButton'
if ($box -ne $null) {
    ShotWindow $box 'copy-3-confirm.png'
    Click (Find-ById $box 'ConfirmButton')
    Start-Sleep -Seconds 6
    Shot 'copy-4-result.png'
}

# --- Help ---
Click (Find-ById $root 'HelpButton')
Start-Sleep -Seconds 2
$help = Find-Owned 'HelpDocument'
if ($help -ne $null) {
    ShotWindow $help 'help.png'
    $help.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    Start-Sleep -Milliseconds 500
}

# --- Cleanup ---
Select-Tab 'CleanupTab'
Start-Sleep -Milliseconds 600
Shot 'cleanup-1-choose.png'
Click (Find-ById $root 'PreviewButton')
Start-Sleep -Seconds 5
Shot 'cleanup-2-preview.png'
Click-Async (Find-ById $root 'DeleteButton')
Start-Sleep -Seconds 2
$dialog = Find-Owned 'PhraseBox'
if ($dialog -ne $null) {
    (Find-ById $dialog 'PhraseBox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('demo@example.test')
    Start-Sleep -Milliseconds 400
    ShotWindow $dialog 'cleanup-3-confirm.png'
    Click (Find-ById $dialog 'ConfirmDeleteButton')
    Start-Sleep -Seconds 7
    Shot 'cleanup-4-result.png'
}

Stop-Process -Id $p.Id
"done"
