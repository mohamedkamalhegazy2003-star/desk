<#
 Puts DuoSeatFilter into the keyboard and mouse class UpperFilters lists, BELOW kbdclass/mouclass.
 !! If the driver fails to load (unsigned, wrong build...) the keyboards/mice that use it fail to start. !!
 Test in a VM first. Recovery: boot to Safe Mode or WinRE and run disable-filter.ps1 (or edit the
 registry values listed below by hand). A reboot (or disabling/enabling the devices) is needed afterwards.
#>
#Requires -RunAsAdministrator
param([switch]$Force)
$ErrorActionPreference = 'Stop'

if (-not (Get-Service DuoSeatFilter -ErrorAction SilentlyContinue)) { throw 'Run install-driver.ps1 first.' }

$testSigning = (bcdedit /enum '{current}' | Select-String -Pattern 'testsigning\s+Yes') -ne $null
if (-not $testSigning -and -not $Force) {
    throw 'Test signing is off. Enable it (bcdedit /set testsigning on, reboot) or use -Force if the driver is properly signed.'
}

$classes = @(
    '{4D36E96B-E325-11CE-BFC1-08002BE10318}',   # Keyboard
    '{4D36E96F-E325-11CE-BFC1-08002BE10318}'    # Mouse
)
foreach ($guid in $classes) {
    $key = "HKLM:\SYSTEM\CurrentControlSet\Control\Class\$guid"
    $current = @((Get-ItemProperty -Path $key -Name UpperFilters -ErrorAction SilentlyContinue).UpperFilters) | Where-Object { $_ }
    if ($current -contains 'DuoSeatFilter') { Write-Host "$guid already configured"; continue }
    Set-ItemProperty -Path $key -Name UpperFilters -Type MultiString -Value (@('DuoSeatFilter') + $current)
    Write-Host "$guid UpperFilters = $((@('DuoSeatFilter') + $current) -join ', ')"
}
Write-Host 'Done. Reboot to load the filter.'
