<#
 Removes DuoSeatFilter from the keyboard/mouse class filter lists. Add -Uninstall to also delete the service and .sys.
#>
#Requires -RunAsAdministrator
param([switch]$Uninstall)
$ErrorActionPreference = 'Stop'

$classes = @(
    '{4D36E96B-E325-11CE-BFC1-08002BE10318}',
    '{4D36E96F-E325-11CE-BFC1-08002BE10318}'
)
foreach ($guid in $classes) {
    $key = "HKLM:\SYSTEM\CurrentControlSet\Control\Class\$guid"
    $current = @((Get-ItemProperty -Path $key -Name UpperFilters -ErrorAction SilentlyContinue).UpperFilters) | Where-Object { $_ }
    $new = @($current | Where-Object { $_ -ne 'DuoSeatFilter' })
    if ($new.Count -eq $current.Count) { continue }
    if ($new.Count -eq 0) { Remove-ItemProperty -Path $key -Name UpperFilters }
    else { Set-ItemProperty -Path $key -Name UpperFilters -Type MultiString -Value $new }
    Write-Host "$guid UpperFilters = $($new -join ', ')"
}

if ($Uninstall) {
    sc.exe stop DuoSeatFilter | Out-Null
    sc.exe delete DuoSeatFilter | Out-Null
    Remove-Item "$env:SystemRoot\System32\drivers\DuoSeatFilter.sys" -Force -ErrorAction SilentlyContinue
    Write-Host 'Service and driver file removed.'
}
Write-Host 'Done. Reboot to unload the filter.'
