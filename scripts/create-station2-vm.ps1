<#
 Creates a Hyper-V virtual machine to use as station 2 (Windows 10/11 Pro, Enterprise or Education host).
 The VM needs its own Windows licence. Resources (CPU/RAM) come from this PC.
   .\scripts\create-station2-vm.ps1 -IsoPath D:\Win11.iso -Win11
#>
#Requires -RunAsAdministrator
param(
    [string]$Name = 'DuoSeat2',
    [Parameter(Mandatory)][string]$IsoPath,
    [int]$MemoryGB = 4,
    [int]$Cpu = 2,
    [int]$DiskGB = 80,
    [switch]$Win11
)
$ErrorActionPreference = 'Stop'

if ((Get-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V-All).State -ne 'Enabled') {
    Enable-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V-All -All -NoRestart | Out-Null
    throw 'Hyper-V was just enabled. Reboot, then run this script again. (Not available on Windows Home.)'
}

$vhd = Join-Path (Get-VMHost).VirtualHardDiskPath "$Name.vhdx"
New-VHD -Path $vhd -SizeBytes ($DiskGB * 1GB) -Dynamic | Out-Null
New-VM -Name $Name -Generation 2 -MemoryStartupBytes ($MemoryGB * 1GB) -VHDPath $vhd -SwitchName 'Default Switch' | Out-Null
Set-VMProcessor -VMName $Name -Count $Cpu
Set-VMMemory -VMName $Name -DynamicMemoryEnabled $false
Add-VMDvdDrive -VMName $Name -Path (Resolve-Path $IsoPath).Path
Set-VMFirmware -VMName $Name -FirstBootDevice (Get-VMDvdDrive -VMName $Name)
if ($Win11) {
    Set-VMKeyProtector -VMName $Name -NewLocalKeyProtector
    Enable-VMTPM -VMName $Name
}
Start-VM -Name $Name
Write-Host "VM '$Name' created. Install Windows, then run:  vmconnect.exe localhost $Name   (full screen on monitor 2)"
Write-Host "Inside the VM run the agent as printed by:  DuoSeat.Service agent-cmdline"
