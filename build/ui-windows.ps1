param([Parameter(Mandatory = $true)][int]$ProcessId)

# Dev-only: list top-level windows of a process (handle, class, AutomationId of a few children).
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$byProcess = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
$windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
    [System.Windows.Automation.TreeScope]::Children, $byProcess)
"windows: " + $windows.Count
foreach ($w in $windows) {
    $ids = @()
    foreach ($e in $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($e.Current.AutomationId) { $ids += $e.Current.AutomationId }
    }
    "handle=" + $w.Current.NativeWindowHandle + " class=" + $w.Current.ClassName + " ids=" + (($ids | Select-Object -First 12) -join ',')
}
