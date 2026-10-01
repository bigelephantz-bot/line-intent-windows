[CmdletBinding()]
param([switch]$InstallOcr)
$ErrorActionPreference = 'Stop'
# Explicit, opt-in provisioning for a disposable test machine. No runner registration or tokens.
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { throw 'Run this provisioning script as administrator on the dedicated test machine.' }
foreach ($name in @('Language.OCR~~~zh-TW~0.0.1.0', 'Language.Fonts.Hant~~~und-HANT~0.0.1.0')) {
    $capability = Get-WindowsCapability -Online -Name $name
    if (-not $capability) { throw "Capability is not supported on this Windows image: $name" }
    if ($capability.State -ne 'Installed' -and $InstallOcr) {
        Add-WindowsCapability -Online -Name $name | Select-Object RestartNeeded
    }
    elseif ($capability.State -ne 'Installed') { throw "Missing $name. Use -InstallOcr to explicitly install it." }
}
Write-Output 'OCR/font capability checks completed. Install .NET 10 SDK and run the CI agent interactively, not as a service.'
