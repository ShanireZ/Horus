[CmdletBinding()]
param(
    [switch]$Check
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$sourcePath = Join-Path $projectRoot 'docs\部署与真机验收.md'
$targetPath = Join-Path $projectRoot 'docs\dist\部署与真机验收.md'
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)

$source = [System.IO.File]::ReadAllText($sourcePath, [System.Text.Encoding]::UTF8)
$generated = [regex]::Replace(
    $source,
    '\A---\r?\n.*?\r?\n---\r?\n\r?\n',
    '',
    [System.Text.RegularExpressions.RegexOptions]::Singleline
)

if ($generated -eq $source) {
    throw "Source frontmatter was not found: $sourcePath"
}

$linkRewrites = [ordered]@{
    '(architecture-v0.2.md)' = '(../architecture-v0.2.md)'
    '(m4-identity-oidc.md)' = '(../m4-identity-oidc.md)'
    '(m5-agent-hardening.md)' = '(../m5-agent-hardening.md)'
    '(../server/server.config.sample.json)' = '(../../server/server.config.sample.json)'
}

foreach ($entry in $linkRewrites.GetEnumerator()) {
    if (-not $generated.Contains($entry.Key)) {
        throw "Expected source link was not found: $($entry.Key)"
    }
    $generated = $generated.Replace($entry.Key, $entry.Value)
}

if ($Check) {
    if (-not (Test-Path -LiteralPath $targetPath)) {
        throw "Generated document is missing: $targetPath"
    }

    $actual = [System.IO.File]::ReadAllText($targetPath, [System.Text.Encoding]::UTF8)
    if ($actual -cne $generated) {
        throw "Generated document is stale. Run: pwsh -File scripts/generate-dist-docs.ps1"
    }

    Write-Output 'Horus dist documentation is current.'
    exit 0
}

[System.IO.File]::WriteAllText($targetPath, $generated, $utf8NoBom)
Write-Output "Generated $targetPath from $sourcePath"
