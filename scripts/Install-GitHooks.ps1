[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$gitRoot = git -C $projectRoot rev-parse --show-toplevel 2>$null
if ($LASTEXITCODE -ne 0) { throw 'Initialize or clone this Git repository before installing its hooks.' }
if ([IO.Path]::GetFullPath($gitRoot.Trim()).TrimEnd('\', '/') -ne [IO.Path]::GetFullPath($projectRoot).TrimEnd('\', '/')) {
    throw 'The project directory must be its own Git repository root.'
}
git -C $projectRoot config --local core.hooksPath .githooks
if ($LASTEXITCODE -ne 0) { throw 'Could not configure the repository hooks path.' }
Write-Output 'Installed the project pre-commit secret scan via core.hooksPath=.githooks.'
Write-Output 'Only this repository is changed. Hooks can be bypassed; CI runs the scanner again.'
