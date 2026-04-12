param(
    [string]$Range,
    [switch]$ScanAllTrackedFiles
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-TrackedPfxFiles {
    $trackedFiles = @(git ls-files)
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to enumerate tracked files."
    }

    return @($trackedFiles | Where-Object { $_ -match '(?i)\.pfx$' })
}

function Get-EventRange {
    if ($Range) {
        return $Range
    }

    $eventPath = $env:GITHUB_EVENT_PATH
    if (-not $eventPath -or -not (Test-Path -LiteralPath $eventPath)) {
        return $null
    }

    $event = Get-Content -LiteralPath $eventPath -Raw | ConvertFrom-Json

    if ($env:GITHUB_EVENT_NAME -eq 'pull_request' -or $env:GITHUB_EVENT_NAME -eq 'pull_request_target') {
        $baseSha = $event.pull_request.base.sha
        $headSha = $event.pull_request.head.sha
        if ($baseSha -and $headSha) {
            return "$baseSha...$headSha"
        }
    }

    if ($env:GITHUB_EVENT_NAME -eq 'push') {
        $beforeSha = $event.before
        $headSha = $env:GITHUB_SHA
        if ($beforeSha -and $beforeSha -ne ('0' * 40) -and $headSha) {
            return "$beforeSha...$headSha"
        }
    }

    return $null
}

function Test-IsPlaceholderValue {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    return $Value -match '(?i)(your[-_ ]?(key|token|secret|password)|example|placeholder|changeme|dummy|sample|fake|test[-_ ]?(key|token|secret|password)?|local[-_ ]?(key|token|secret|password)?)'
}

function Test-IsAllowedMatch {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Match,

        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Line
    )

    if ($Line -match 'gitleaks:allow') {
        return $true
    }

    if ($Match -like '*...*') {
        return $true
    }

    if ($Match -match '^\$[A-Za-z_][A-Za-z0-9_]*$') {
        return $true
    }

    if ($Match -match '^[xX]+$') {
        return $true
    }

    if ($Match -match '(?i)^gh[pours]?_x+$') {
        return $true
    }

    if ($Match -match '^(?i)(access_token|refresh_token|id_token|token_type)$') {
        return $true
    }

    if ($Match -match '[<>\[\]\(\)\{\}\\]') {
        return $true
    }

    return Test-IsPlaceholderValue -Value $Match
}

$detectors = @(
    @{
        Name = 'OpenAI/OpenRouter/Anthropic-style API key'
        Regex = '(?<![A-Za-z0-9_-])sk-[A-Za-z0-9_-]{10,}(?![A-Za-z0-9_-])'
    },
    @{
        Name = 'GitHub token'
        Regex = '(?<![A-Za-z0-9_-])(ghp_[A-Za-z0-9]{10,}|github_pat_[A-Za-z0-9_]{10,}|gho_[A-Za-z0-9]{10,}|ghu_[A-Za-z0-9]{10,}|ghs_[A-Za-z0-9]{10,}|ghr_[A-Za-z0-9]{10,})(?![A-Za-z0-9_-])'
    },
    @{
        Name = 'Slack token'
        Regex = '(?<![A-Za-z0-9_-])xox[baprs]-[A-Za-z0-9-]{10,}(?![A-Za-z0-9_-])'
    },
    @{
        Name = 'Google API key'
        Regex = '(?<![A-Za-z0-9_-])AIza[A-Za-z0-9_-]{30,}(?![A-Za-z0-9_-])'
    },
    @{
        Name = 'AWS access key'
        Regex = '(?<![A-Za-z0-9_-])AKIA[A-Z0-9]{16}(?![A-Za-z0-9_-])'
    },
    @{
        Name = 'Stripe key'
        Regex = '(?<![A-Za-z0-9_-])(sk_live_[A-Za-z0-9]{10,}|sk_test_[A-Za-z0-9]{10,}|rk_live_[A-Za-z0-9]{10,})(?![A-Za-z0-9_-])'
    },
    @{
        Name = 'SendGrid key'
        Regex = '(?<![A-Za-z0-9_-])SG\.[A-Za-z0-9_-]{10,}(?![A-Za-z0-9_-])'
    },
    @{
        Name = 'Hugging Face key'
        Regex = '(?<![A-Za-z0-9_-])hf_[A-Za-z0-9]{10,}(?![A-Za-z0-9_-])'
    },
    @{
        Name = 'PyPI token'
        Regex = '(?<![A-Za-z0-9_-])pypi-[A-Za-z0-9_-]{10,}(?![A-Za-z0-9_-])'
    },
    @{
        Name = 'Private key block'
        Regex = '-----BEGIN[A-Z ]*PRIVATE KEY-----'
    },
    @{
        Name = 'Authorization bearer token'
        Regex = 'Authorization:\s*Bearer\s+(\S+)'
        UsesCapture = $true
    },
    @{
        Name = 'Credential-bearing URL'
        Regex = 'https?://[^/\s:@]+:[^@\s]+@'
    },
    @{
        Name = 'Quoted secret field'
        Regex = '"(?:api_?[Kk]ey|token|secret|password|access_token|refresh_token|auth_token|bearer)"\s*:\s*"([^"]+)"'
        UsesCapture = $true
    }
)

function Test-LineForSecrets {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Line
    )

    $matches = @()
    foreach ($detector in $detectors) {
        $regexMatches = [System.Text.RegularExpressions.Regex]::Matches($Line, $detector.Regex)
        foreach ($match in $regexMatches) {
            $usesCapture = $detector.ContainsKey('UsesCapture') -and $detector.UsesCapture
            if ($usesCapture) {
                $capturedValue = $match.Groups[1].Value
                if ($capturedValue -and (Test-IsAllowedMatch -Match $capturedValue -Line $Line)) {
                    continue
                }
            }

            if ($match.Value -and (Test-IsAllowedMatch -Match $match.Value -Line $Line)) {
                continue
            }

            $matches += [pscustomobject]@{
                Rule = $detector.Name
                Match = $match.Value
            }
        }
    }

    return $matches
}

function Get-AddedLinesFromDiff {
    param(
        [Parameter(Mandatory = $true)]
        [string]$DiffRange
    )

    $diffOutput = @(git diff --unified=0 --no-color --diff-filter=AMRT $DiffRange)
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to read git diff for range '$DiffRange'."
    }

    $file = $null
    $lineNumber = 0
    $added = New-Object System.Collections.Generic.List[object]

    foreach ($line in $diffOutput) {
        if ($line -match '^\+\+\+ b/(.+)$') {
            $file = $Matches[1]
            continue
        }

        if ($line -match '^@@ -\d+(?:,\d+)? \+(\d+)(?:,\d+)? @@') {
            $lineNumber = [int]$Matches[1] - 1
            continue
        }

        if ($line.StartsWith('+') -and -not $line.StartsWith('+++')) {
            $lineNumber++
            $added.Add([pscustomobject]@{
                File = $file
                Line = $lineNumber
                Content = $line.Substring(1)
            })
        }
    }

    return $added
}

function Get-LinesFromTrackedFiles {
    $trackedFiles = @(git ls-files)
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to enumerate tracked files."
    }

    $lines = New-Object System.Collections.Generic.List[object]
    foreach ($file in $trackedFiles) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
            continue
        }

        $lineNumber = 0
        foreach ($line in Get-Content -LiteralPath $file) {
            $lineNumber++
            $lines.Add([pscustomobject]@{
                File = $file
                Line = $lineNumber
                Content = [string]$line
            })
        }
    }

    return $lines
}

$failures = New-Object System.Collections.Generic.List[string]

$trackedPfxFiles = @(Get-TrackedPfxFiles)
if ($trackedPfxFiles.Count -gt 0) {
    $failures.Add("Tracked .pfx files are not allowed:`n - " + ($trackedPfxFiles -join "`n - "))
}

$candidateLines =
    if ($ScanAllTrackedFiles) {
        Write-Host "Scanning all tracked files for secret-like content..."
        Get-LinesFromTrackedFiles
    }
    else {
        $effectiveRange = Get-EventRange
        if (-not $effectiveRange) {
            Write-Host "No git range detected; scanning all tracked files for secret-like content..."
            Get-LinesFromTrackedFiles
        }
        else {
            Write-Host "Scanning added lines in git range $effectiveRange for secret-like content..."
            Get-AddedLinesFromDiff -DiffRange $effectiveRange
        }
    }

$secretHits = New-Object System.Collections.Generic.List[object]
foreach ($candidate in $candidateLines) {
    $lineHits = Test-LineForSecrets -Line $candidate.Content
    foreach ($hit in $lineHits) {
        $secretHits.Add([pscustomobject]@{
            File = $candidate.File
            Line = $candidate.Line
            Rule = $hit.Rule
            Match = $hit.Match
        })
    }
}

if ($secretHits.Count -gt 0) {
    $formattedHits = $secretHits |
        Select-Object -First 20 |
        ForEach-Object { " - $($_.File):$($_.Line) [$($_.Rule)]" }

    $message = "Secret-like content detected:`n" + ($formattedHits -join "`n")
    if ($secretHits.Count -gt 20) {
        $message += "`n - ... and $($secretHits.Count - 20) more"
    }

    $failures.Add($message)
}

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) {
        Write-Error $failure
    }

    exit 1
}

Write-Host "Repo guard passed: no tracked .pfx files and no detected secrets."
