[CmdletBinding()]
param(
    [switch]$Staged,
    [switch]$SelfTest,
    [string]$Root
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Root)) { $Root = Split-Path -Parent $PSScriptRoot }

# Deliberately reports paths and rule names only, never matching lines or token values.
# Default: tracked + nonignored untracked working files; source candidates before Git initialization.
# Staged: every added/modified index entry, including force-added generated directories.
function Invoke-ScanGit {
    param([string]$Directory, [string[]]$Arguments)
    $git = Get-Command git -ErrorAction SilentlyContinue
    if (-not $git) { throw 'Git is required for this operation.' }
    $savedErrorPreference = $ErrorActionPreference
    $savedEncoding = [Console]::OutputEncoding
    try {
        $ErrorActionPreference = 'Continue'
        [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
        $output = @(& $git.Source -C $Directory @Arguments 2>$null)
        $code = $LASTEXITCODE
        return [pscustomobject]@{ Code = $code; Text = ($output -join "`n") }
    }
    finally {
        $ErrorActionPreference = $savedErrorPreference
        [Console]::OutputEncoding = $savedEncoding
    }
}

function Test-ExcludedPath([string]$Path) {
    return $Path -match '(^|/)(?:\.git|bin|obj|dist|artifacts)(/|$)'
}

function Get-SourceCandidates([string]$Directory) {
    $pending = New-Object 'System.Collections.Generic.Stack[string]'
    $pending.Push($Directory)
    while ($pending.Count -gt 0) {
        foreach ($item in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
            $relative = $item.FullName.Substring($Directory.Length).TrimStart('\', '/').Replace('\', '/')
            if (Test-ExcludedPath $relative) { continue }
            if ($item.PSIsContainer) {
                if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) { $pending.Push($item.FullName) }
            }
            else { $relative }
        }
    }
}

function Test-Placeholder([string]$Value, [string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return $true }
    if ($Value -match '^(?:YOUR_[A-Z0-9_]+|REPLACE[_-]ME|CHANGEME|REDACTED|\[redacted\]|<[^<>\r\n]+>|\$\{[A-Z0-9_]+\})$') { return $true }
    # Exact dummy values only; a tests/ location does not exempt real token patterns.
    if ($Value -in @('test-only-key', 'synthetic-router-key', 'synthetic-typesafe-key', 'synthetic-llm-key')) { return $true }
    if ($Path -like 'tests/*' -and $Value -eq 'secret\r\nInjected: yes') { return $true }
    return $false
}

function Find-ContentRules([string]$Path, [string]$Content) {
    $rules = @{
        'provider-api-key' = '(?<![A-Za-z0-9_-])sk-(?:or-v1-|proj-)?[A-Za-z0-9_-]{20,}'
        'github-token' = '(?<![A-Za-z0-9_])(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,})'
        'aws-access-key' = '(?<![A-Z0-9])(?:AKIA|ASIA)[A-Z0-9]{16}(?![A-Z0-9])'
        'private-key-material' = '-----BEGIN (?:RSA |EC |OPENSSH |DSA |ENCRYPTED )?PRIVATE KEY-----'
    }
    foreach ($rule in $rules.Keys) {
        if ([regex]::IsMatch($Content, $rules[$rule])) { $rule }
    }
    $name = '(?:api[_-]?key|llm[_-]?api[_-]?key|(?:openrouter|typesafe)[_-]?(?:api[_-]?)?key|access[_-]?token|client[_-]?secret|password|secret)'
    $jsonPattern = '(?i)"' + $name + '"\s*:\s*"(?<value>(?:\\.|[^"\\])*)"'
    $assignmentPattern = '(?i)\b' + $name + '\b\s*=\s*@?(?<quote>[''"])(?<value>(?:\\.|(?!\k<quote>)[^\\\r\n])*)\k<quote>'
    foreach ($pattern in @($jsonPattern, $assignmentPattern)) {
        foreach ($match in [regex]::Matches($Content, $pattern)) {
            if (-not (Test-Placeholder $match.Groups['value'].Value $Path)) {
                'plaintext-credential-value'
                break
            }
        }
    }
}

function Find-FileRules([string]$Path, [string]$Content) {
    $name = [IO.Path]::GetFileName($Path)
    $isTemplate = $name -match '(?:^|[._-])(?:example|sample|template)(?:[._-]|$)'
    if (-not $isTemplate -and (
        $name -match '^\.env(?:\..*)?$' -or
        $name -match '^(?:appsettings(?:\.[^.]+)?|settings|config|credentials|secrets)\.json$' -or
        $name -match '^(?:id_rsa|id_dsa|id_ecdsa|id_ed25519)$' -or
        $name -match '\.(?:pem|pfx|p12|key)$')) {
        'sensitive-filename'
    }
    Find-ContentRules $Path $Content
}

function Get-ScanFindings([string]$Directory, [bool]$IndexOnly) {
    $Directory = [IO.Path]::GetFullPath($Directory).TrimEnd('\', '/')
    $gitCommand = Get-Command git -ErrorAction SilentlyContinue
    $repo = $null
    if ($gitCommand) { $repo = Invoke-ScanGit $Directory @('rev-parse', '--show-toplevel') }
    $isRepoRoot = $repo -and $repo.Code -eq 0 -and
        [IO.Path]::GetFullPath($repo.Text.Trim()).TrimEnd('\', '/') -eq $Directory
    if ($IndexOnly -and -not $isRepoRoot) { throw 'Staged scanning requires the project Git repository root.' }

    if ($isRepoRoot) {
        $arguments = @('ls-files', '--cached', '--others', '--exclude-standard', '-z')
        if ($IndexOnly) { $arguments = @('diff', '--cached', '--name-only', '--diff-filter=ACMR', '-z') }
        $listed = Invoke-ScanGit $Directory $arguments
        if ($listed.Code -ne 0) { throw 'Git could not enumerate scan candidates.' }
        $paths = @($listed.Text.Split([char]0) | Where-Object { $_ })
    }
    else { $paths = @(Get-SourceCandidates $Directory) }

    foreach ($path in $paths) {
        $path = $path.Replace('\', '/')
        if ($IndexOnly) {
            # Read the staged blob, not the potentially different working-tree file.
            $blob = Invoke-ScanGit $Directory @('show', (':' + $path))
            if ($blob.Code -ne 0) { throw ('Unable to read staged entry: ' + $path) }
            $content = $blob.Text
        }
        else {
            $fullPath = [IO.Path]::GetFullPath((Join-Path $Directory $path))
            if (-not $fullPath.StartsWith($Directory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'A scan candidate escaped the project directory.'
            }
            if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { continue }
            $item = Get-Item -LiteralPath $fullPath -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                [pscustomobject]@{ Path = $path; Rule = 'linked-file-needs-review' }
                continue
            }
            $content = [IO.File]::ReadAllText($fullPath)
        }
        foreach ($rule in @(Find-FileRules $path $content | Sort-Object -Unique)) {
            [pscustomobject]@{ Path = $path; Rule = $rule }
        }
    }
}

function Invoke-ScannerSelfTest {
    $tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
    $fixtureRoot = Join-Path $tempBase ('lineintent-secret-scan-' + [guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
    $checks = 0
    try {
        $fakeToken = ('sk-' + 'or-v1-' + ('a' * 64))
        $cases = @(
            @{ Path = 'src/token.txt'; Text = $fakeToken; Rule = 'provider-api-key' },
            @{ Path = 'src/plain.json'; Text = ('{' + '"ApiKey"' + ':"fixture-credential-value"}'); Rule = 'plaintext-credential-value' },
            @{ Path = '.env'; Text = ''; Rule = 'sensitive-filename' },
            @{ Path = 'private.pem'; Text = ''; Rule = 'sensitive-filename' },
            @{ Path = 'src/private.txt'; Text = ('-----BEGIN ' + 'PRIVATE KEY-----'); Rule = 'private-key-material' },
            @{ Path = 'src/plain.ps1'; Text = ('$Api' + "Key = 'fixture-credential-value'"); Rule = 'plaintext-credential-value' },
            @{ Path = 'src/llm.cs'; Text = ('LlmApi' + 'Key = "fixture-credential-value"'); Rule = 'plaintext-credential-value' }
        )
        foreach ($case in $cases) {
            if ($case.Rule -notin @(Find-FileRules $case.Path $case.Text)) { throw ('Self-test missed rule: ' + $case.Rule) }
            $checks++
        }
        $safe = '{' + '"ApiKey"' + ':"test-only-key"}'
        if (@(Find-FileRules 'settings.example.json' $safe).Count -ne 0) { throw 'Self-test rejected a safe example.' }
        $checks++
        foreach ($arguments in @(@('init', '--quiet'), @('config', 'core.autocrlf', 'false'))) {
            if ((Invoke-ScanGit $fixtureRoot $arguments).Code -ne 0) { throw 'Self-test Git initialization failed.' }
        }
        $fixture = Join-Path $fixtureRoot 'fixture.txt'
        [IO.File]::WriteAllText($fixture, $fakeToken)
        if ((Invoke-ScanGit $fixtureRoot @('add', '--', 'fixture.txt')).Code -ne 0) { throw 'Self-test Git staging failed.' }
        [IO.File]::WriteAllText($fixture, 'safe working copy')
        if (@(Get-ScanFindings $fixtureRoot $true | Where-Object Rule -eq 'provider-api-key').Count -ne 1) {
            throw 'Self-test did not read the actual staged secret.'
        }
        $checks++
        if ((Invoke-ScanGit $fixtureRoot @('add', '--', 'fixture.txt')).Code -ne 0) { throw 'Self-test Git staging failed.' }
        [IO.File]::WriteAllText($fixture, $fakeToken)
        if (@(Get-ScanFindings $fixtureRoot $true).Count -ne 0) { throw 'Self-test incorrectly scanned the working copy in staged mode.' }
        $checks++
        [IO.File]::WriteAllText((Join-Path $fixtureRoot 'untracked.txt'), $fakeToken)
        if (@(Get-ScanFindings $fixtureRoot $false | Where-Object Path -eq 'untracked.txt').Count -ne 1) {
            throw 'Self-test missed a nonignored untracked source file.'
        }
        $checks++
        [IO.File]::WriteAllText((Join-Path $fixtureRoot '.gitignore'), 'dist/')
        [IO.Directory]::CreateDirectory((Join-Path $fixtureRoot 'dist')) | Out-Null
        [IO.File]::WriteAllText((Join-Path $fixtureRoot 'dist/forced.txt'), $fakeToken)
        if ((Invoke-ScanGit $fixtureRoot @('add', '--force', '--', 'dist/forced.txt')).Code -ne 0) { throw 'Self-test force staging failed.' }
        if (@(Get-ScanFindings $fixtureRoot $false | Where-Object Path -eq 'dist/forced.txt').Count -ne 1) {
            throw 'Self-test skipped a force-tracked output file.'
        }
        $checks++
        Write-Output ('PASS: ' + $checks + ' secret-scanner checks, including Git index isolation.')
    }
    finally {
        $resolved = [IO.Path]::GetFullPath($fixtureRoot)
        if ([IO.Path]::GetDirectoryName($resolved) -eq $tempBase -and
            [IO.Path]::GetFileName($resolved).StartsWith('lineintent-secret-scan-', [StringComparison]::Ordinal)) {
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
        else { throw 'Self-test cleanup refused an unexpected directory.' }
    }
}

try {
    if ($SelfTest) { Invoke-ScannerSelfTest; exit 0 }
    $findings = @(Get-ScanFindings $Root ([bool]$Staged))
    if ($findings.Count -gt 0) {
        foreach ($finding in $findings) { Write-Output ('BLOCK ' + $finding.Path.Replace("`r", '').Replace("`n", '') + ' [' + $finding.Rule + ']') }
        Write-Output 'Secret check failed. Remove the sensitive value/file from the candidate changes and scan again.'
        exit 1
    }
    Write-Output 'PASS: no configured secret patterns found. This scan is a guardrail, not a guarantee.'
    exit 0
}
catch {
    # Error messages are authored above; native output and file contents are never displayed.
    Write-Output ('Secret check could not complete: ' + $_.Exception.Message)
    exit 2
}
