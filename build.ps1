param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$releaseDir = 'dist/LineIntent-win-x64-0.7.0'
& "$PSScriptRoot\scripts\Check-Secrets.ps1"
if ($LASTEXITCODE -ne 0) { throw 'Secret scan failed.' }
if (-not $SkipTests) {
    dotnet run --project tests/LineIntent.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
}
dotnet publish src/LineIntent/LineIntent.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o $releaseDir
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Copy-Item -LiteralPath README.md,LICENSE,NOTICE,SECURITY.md -Destination $releaseDir -Force
New-Item -ItemType Directory -Force "$releaseDir/docs" | Out-Null
Copy-Item -LiteralPath docs/jev-protocol.md,docs/verification.md -Destination "$releaseDir/docs" -Force
Copy-Item -LiteralPath docs/code-review-2026-09-24.md -Destination "$releaseDir/docs" -Force
Copy-Item -LiteralPath docs/windows-ui-ci.md -Destination "$releaseDir/docs" -Force
Compress-Archive -Path $releaseDir -DestinationPath "$releaseDir.zip" -Force
Write-Output "Ready: $releaseDir/LineIntent.exe"
