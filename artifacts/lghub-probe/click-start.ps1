Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "G920 Emulator")
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if (-not $win) { "NO WINDOW"; exit 1 }
$allBtn = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
$btns = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $allBtn)
"button count=$($btns.Count)"
foreach ($b in $btns) { "btn='$($b.Current.Name)' enabled=$($b.Current.IsEnabled)" }
$target = $null
foreach ($b in $btns) { if ($b.Current.Name -match "Start") { $target = $b; break } }
if (-not $target) { "no Start"; exit 1 }
$target.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
"invoked $($target.Current.Name)"
Start-Sleep 5
$devs = Get-PnpDevice -EA SilentlyContinue | Where-Object { $_.InstanceId -match "VID_046D&PID_C262" }
"C262=$($devs.Count)"
$devs | ForEach-Object { "$($_.Status) | $($_.FriendlyName) | $($_.InstanceId)" }
pnputil /add-driver "C:\Windows\INF\oem107.inf" /install | Out-String
Start-Sleep 2
$col = Get-PnpDevice -EA SilentlyContinue | Where-Object { $_.InstanceId -match "C262" -and $_.InstanceId -match "COL01" } | Select-Object -First 1
if ($col) {
  $props = Get-PnpDeviceProperty -InstanceId $col.InstanceId
  foreach ($k in @("DEVPKEY_Device_DriverDesc","DEVPKEY_Device_DriverInfPath","DEVPKEY_Device_DriverProvider","DEVPKEY_Device_Service","DEVPKEY_Device_HardwareIds","DEVPKEY_Device_CompatibleIds")) {
    $v = ($props | Where-Object KeyName -eq $k).Data
    if ($v -is [array]) { $v = $v -join " | " }
    "$k=$v"
  }
}
