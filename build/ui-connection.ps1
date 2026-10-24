param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$OutDir,
    # Field values. Defaults point to a closed local port, so nothing leaves the machine.
    [string]$Server = '127.0.0.1:1',
    [string]$Alternate = '',
    [string]$User = 'DOMAIN\user',
    [string]$Offset = '+03:00',
    # Only fill the form and take a screenshot (for documentation), do not run the check.
    [switch]$NoCheck
)
# Dev-only check of the Connection window: start the app from a folder without config\connection.json,
# so the window opens at startup; screenshot it empty, fill it, optionally run the check, screenshot the
# result. Nothing is saved: the process is stopped at the end. ASCII-only on purpose.
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$shot = Join-Path $PSScriptRoot 'screenshot.ps1'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$p = Start-Process -FilePath $Exe -PassThru
$byProcess = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
function Find-ById($scope, [string]$id) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $scope.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
function Find-Dialog {
    # A modal window owned by the main window is exposed by UIA as its child, so find the field and walk up
    # to the nearest Window element - that is the dialog itself.
    foreach ($w in [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
            [System.Windows.Automation.TreeScope]::Children, $byProcess)) {
        $field = Find-ById $w 'ServerBox'
        if ($field -ne $null) {
            $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
            $e = $walker.GetParent($field)
            while ($e -ne $null -and $e.Current.ControlType -ne [System.Windows.Automation.ControlType]::Window) {
                $e = $walker.GetParent($e)
            }
            return $e
        }
    }
    return $null
}
function Shot($window, [string]$name) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File $shot -Handle $window.Current.NativeWindowHandle -OutFile (Join-Path $OutDir $name) | Out-Null
}
function Set-Text($window, [string]$id, [string]$value) {
    (Find-ById $window $id).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value)
}
$dialog = $null
for ($i = 0; $i -lt 40 -and $dialog -eq $null; $i++) { Start-Sleep -Milliseconds 250; $dialog = Find-Dialog }
if ($dialog -eq $null) { "connection window not found"; Stop-Process -Id $p.Id; exit 1 }
Start-Sleep -Milliseconds 500
Shot $dialog 'c1-empty.png'
Set-Text $dialog 'ServerBox' $Server
if ($Alternate -ne '') { Set-Text $dialog 'AlternateServerBox' $Alternate }
Set-Text $dialog 'UserBox' $User
$box = Find-ById $dialog 'OffsetBox'
$box.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
Start-Sleep -Milliseconds 300
$items = $box.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)))
foreach ($item in $items) {
    if ($item.Current.Name -like ('*' + $Offset + '*')) {
        $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        break
    }
}
$box.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
Start-Sleep -Milliseconds 400
Shot $dialog 'c2-filled.png'
if (-not $NoCheck) {
    (Find-ById $dialog 'CheckButton').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Seconds 4
    Shot $dialog 'c3-checked.png'
}
Stop-Process -Id $p.Id
"done"
