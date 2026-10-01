[CmdletBinding()]
param(
    [string]$Executable = 'src/LineIntent/bin/Release/net10.0-windows10.0.19041.0/LineIntent.exe',
    [string]$OutputDirectory = 'artifacts/ui-smoke',
    [ValidateRange(30, 600)][int]$TimeoutSeconds = 180
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exePath = [IO.Path]::GetFullPath((Join-Path $root $Executable))
if ([IO.Path]::IsPathRooted($Executable)) { $exePath = [IO.Path]::GetFullPath($Executable) }
$outputPath = [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
if ([IO.Path]::IsPathRooted($OutputDirectory)) { $outputPath = [IO.Path]::GetFullPath($OutputDirectory) }
if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) { throw 'Build the Windows executable before running UI smoke tests.' }
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$runPath = Join-Path $outputPath ('run-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runPath | Out-Null

function Invoke-OwnedProcess([string]$Flag, [string]$Destination) {
    $process = Start-Process -FilePath $exePath -ArgumentList @($Flag, ('"' + $Destination + '"')) -PassThru -WindowStyle Hidden
    try {
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            # Only terminate the exact test process created above, never other app instances.
            $process.Kill()
            throw 'UI smoke test timed out; only its own process was stopped.'
        }
        return $process.ExitCode
    }
    finally { $process.Dispose() }
}

$preflight = Join-Path $runPath 'preflight.json'
$preflightExit = Invoke-OwnedProcess '--ci-preflight' $preflight
if (-not (Test-Path -LiteralPath $preflight)) { throw 'UI preflight did not produce a report.' }
Copy-Item -LiteralPath $preflight -Destination (Join-Path $outputPath 'preflight.json') -Force
$environment = Get-Content -LiteralPath $preflight -Raw -Encoding UTF8 | ConvertFrom-Json
if ($preflightExit -ne 0 -or -not $environment.ok) {
    $environment.failures | Write-Output
    throw 'UI environment check failed; no tests were silently skipped.'
}
$resultExit = Invoke-OwnedProcess '--self-test' $runPath
$resultPath = Join-Path $runPath 'results.txt'
if (-not (Test-Path -LiteralPath $resultPath)) { throw 'UI smoke test did not produce results.' }
Copy-Item -LiteralPath $resultPath -Destination (Join-Path $outputPath 'results.txt') -Force
Get-ChildItem -LiteralPath $runPath -Filter '*.png' | Copy-Item -Destination $outputPath -Force
$resultLines = Get-Content -LiteralPath $resultPath -Encoding UTF8
$resultLines | Write-Output
$summary = $resultLines | Where-Object { $_ -match '^RESULT \d+/\d+ smoke checks passed\.' } | Select-Object -Last 1
if ($resultExit -ne 0 -or -not $summary -or $summary -notmatch '^RESULT (\d+)/(\d+)' -or $Matches[1] -ne $Matches[2]) {
    throw 'UI smoke checks failed.'
}
Write-Output 'PASS: controlled Windows UI smoke completed without model credentials.'
