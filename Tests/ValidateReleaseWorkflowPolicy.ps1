# Guard the public release path against re-introducing auto-release workflows.
# This policy is intentionally independent of GitHub YAML parsing and fails closed
# if a retired release workflow or an automatic official release trigger reappears.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$workflowsDir = Join-Path $root '.github/workflows'
$official = Join-Path $workflowsDir 'publish-release.yml'
$preview = Join-Path $workflowsDir 'pr-test-package.yml'

if (-not (Test-Path -LiteralPath $official)) { throw 'Missing official release workflow' }
if (-not (Test-Path -LiteralPath $preview)) { throw 'Missing PR test-package workflow' }

$text = Get-Content -LiteralPath $official -Raw
if ($text -notmatch '(?m)^on:\r?\n  workflow_dispatch:\r?\n') { throw 'Official releases must be workflow_dispatch only' }
if ($text -match '(?m)^  (?:push|pull_request|pull_request_target|schedule|workflow_run|repository_dispatch):') {
    throw 'Official releases must never run from automatic events'
}
if ($text -notmatch [regex]::Escape("github.ref == 'refs/heads/stable'")) { throw 'Official release needs stable ref guard' }
if ($text -notmatch [regex]::Escape("github.repository == 'jingjial2018-beep/JuChuang'")) {
    throw 'Official release needs repository identity guard'
}
if ($text -notmatch [regex]::Escape("inputs.operation == 'publish'")) { throw 'Missing explicit publish mode guard' }
if ($text -notmatch 'confirm_version') { throw 'Missing explicit release tag confirmation' }
if ($text -notmatch 'verify-stable-baseline') { throw 'Missing required passing stable CI check' }
if ($text -notmatch 'sha256sum --check') { throw 'Missing package hash verification' }
if ($text -match '(?i)git\s+push\s+(?:-f|--force)') {
    throw 'Unreviewed force-push is not permitted'
}
if ($text -match '(?i)--clobber|\bgh\s+release\s+upload\b') {
    if ($text -notmatch "inputs.operation == 'republish'" -or
        $text -notmatch 'Only latest release can be republished' -or
        $text -notmatch 'Republish source must advance existing tag') {
        throw 'Release asset replacement requires explicit republish safeguards'
    }
}
foreach ($old in @(
    'beta-release.yml',
    'v1.0.3-final-release.yml',
    'v1.0.4-beta.4-release.yml',
    'v1.0.4-beta.5-release.yml',
    'v1.0.4-beta.6-release.yml',
    'v1.0.4-beta.7-release.yml'
)) {
    if (Test-Path -LiteralPath (Join-Path $workflowsDir $old)) { throw "Obsolete publisher still present: $old" }
}
foreach ($file in Get-ChildItem -LiteralPath $workflowsDir -File -Include '*.yml', '*.yaml' -Recurse) {
    if ($file.FullName -eq $official) { continue }
    $other = Get-Content -LiteralPath $file.FullName -Raw
    if ($other -match '(?m)^\s+contents:\s*write\b' -or
        $other -match '(?i)\bgh\s+release\s+(?:create|upload|edit|delete)\b') {
        throw "Unexpected second release-capable workflow: $($file.Name)"
    }
}
$pr = Get-Content -LiteralPath $preview -Raw
if ($pr -notmatch '(?ms)^on:\r?\n  pull_request:\r?\n    branches:\r?\n      - stable') {
    throw 'PR preview packages must target stable'
}
if ($pr -match '(?m)^\s+contents:\s*write\b') { throw 'PR previews must not have release write permissions' }
if ($pr -notmatch 'actions/upload-artifact') { throw 'PR preview is missing its artifact output' }
Write-Host 'PASS: only manual stable release publisher; PR previews are read-only and no old publishers remain.'
