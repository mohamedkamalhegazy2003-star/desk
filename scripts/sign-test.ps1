<#
 Test-signs DuoSeatFilter.sys with a self-signed certificate (development machines only).
 Run from a "Developer PowerShell for VS" (so signtool.exe is on PATH), as Administrator.
   .\scripts\sign-test.ps1 -SysPath .\driver\DuoSeatFilter\x64\Debug\DuoSeatFilter.sys
#>
#Requires -RunAsAdministrator
param([Parameter(Mandatory)][string]$SysPath)
$ErrorActionPreference = 'Stop'

$subject = 'CN=DuoSeat Test Signing'
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $subject } | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(3)
}
$cer = Join-Path $env:TEMP 'duoseat-test.cer'
Export-Certificate -Cert $cert -FilePath $cer | Out-Null
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPublisher | Out-Null

signtool sign /v /fd SHA256 /sha1 $cert.Thumbprint $SysPath
if ($LASTEXITCODE -ne 0) { throw 'signtool failed' }
Write-Host 'Signed. Remember: bcdedit /set testsigning on  (then reboot, Secure Boot must be off).'
