# Installs the canonical CodeContext skill for Codex and Claude Code.
$ErrorActionPreference = 'Stop'

# Nested Join-Path keeps this compatible with Windows PowerShell 5.1.
$skillSource = Join-Path $PSScriptRoot 'SKILL.md'
$referencesSource = Join-Path $PSScriptRoot 'references'
if (-not (Test-Path $skillSource)) { throw "Not found: $skillSource" }
$codexHome = if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $HOME '.codex' }
$targets = @(
    (Join-Path (Join-Path $codexHome 'skills') 'code-context'),
    (Join-Path (Join-Path (Join-Path $HOME '.claude') 'skills') 'code-context')
)

foreach ($targetDir in $targets) {
    New-Item -ItemType Directory -Force $targetDir | Out-Null
    Copy-Item $skillSource (Join-Path $targetDir 'SKILL.md') -Force
    if (Test-Path $referencesSource) {
        Copy-Item $referencesSource $targetDir -Recurse -Force
    }
    Write-Host "Installed CodeContext skill to $targetDir"
}
Write-Host "Start a new agent session to pick up the skill."
