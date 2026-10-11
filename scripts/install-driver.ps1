<#
 Registers the DuoSeatFilter kernel service (copies the .sys to System32\drivers).
 It does NOT attach the filter to any device yet - see enable-filter.ps1.
   .\scripts\install-driver.ps1 -SysPath .\driver\DuoSeatFilter\x64\Debug\DuoSeatFilter.sys
#>
#Requires -RunAsAdministrator
param([Parameter(Mandatory)][string]$SysPath)
$ErrorActionPreference = 'Stop'

$sys = (Resolve-Path $SysPath).Path
$inf = (Resolve-Path (Join-Path $PSScriptRoot '..\driver\DuoSeatFilter\DuoSeatFilter.inf')).Path

# INF "CopyFiles" looks for the .sys next to the .inf, so stage both in a temp folder.
$stage = Join-Path $env:TEMP 'DuoSeatInstall'
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $stage | Out-Null
Copy-Item $sys (Join-Path $stage 'DuoSeatFilter.sys')
Copy-Item $inf (Join-Path $stage 'DuoSeatFilter.inf')

rundll32.exe setupapi.dll,InstallHinfSection DefaultInstall 132 (Join-Path $stage 'DuoSeatFilter.inf')
Start-Sleep -Seconds 3

if (-not (Get-Service DuoSeatFilter -ErrorAction SilentlyContinue)) {
    throw 'Service DuoSeatFilter was not created. Check C:\Windows\INF\setupapi.dev.log'
}
Write-Host 'Service DuoSeatFilter registered. Next: scripts\enable-filter.ps1'
