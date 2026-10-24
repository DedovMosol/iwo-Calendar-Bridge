param([Parameter(Mandatory = $true)][string]$EwsDll)

# Dev-only probe: does EWS Managed API copy ExchangeService.ConnectionGroupName into the HttpWebRequest it
# creates? Scans the IL of ExchangeServiceBase.PrepareHttpWebRequestForUrl for calls to *ConnectionGroupName*.
$asm = [System.Reflection.Assembly]::LoadFrom($EwsDll)
$type = $asm.GetType('Microsoft.Exchange.WebServices.Data.ExchangeServiceBase')
$flags = [System.Reflection.BindingFlags]'Instance,NonPublic,Public'
foreach ($m in $type.GetMethods($flags) | Where-Object { $_.Name -like '*PrepareHttpWebRequest*' }) {
    $il = $m.GetMethodBody().GetILAsByteArray()
    $found = @()
    for ($i = 0; $i -lt $il.Length - 4; $i++) {
        if ($il[$i] -eq 0x28 -or $il[$i] -eq 0x6F) {
            $token = [BitConverter]::ToInt32($il, $i + 1)
            try {
                $callee = $m.Module.ResolveMethod($token)
                if ($callee.Name -like '*ConnectionGroupName*') { $found += $callee.DeclaringType.Name + '.' + $callee.Name }
            } catch { }
        }
    }
    $m.Name + ': ' + (($found | Select-Object -Unique) -join ', ')
}
