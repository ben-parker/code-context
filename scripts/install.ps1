# Installs the latest CodeContext release to ~/.codecontext/bin (Windows).
# Usage: irm https://raw.githubusercontent.com/ben-parker/code-context/main/scripts/install.ps1 | iex
$ErrorActionPreference = 'Stop'

$repo = 'ben-parker/code-context'
# The release matrix currently publishes win-x64 only; on ARM64 the x64 binary runs
# through Windows x64 emulation. Revisit if a win-arm64 artifact is added to the matrix.
$rid = 'win-x64'
if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') {
    Write-Host "No native win-arm64 build is published; installing win-x64 (runs under x64 emulation)."
}
$codeContextHome = Join-Path $HOME '.codecontext'
$installDir = Join-Path $codeContextHome 'bin'

# Swapping the launcher/release payload out from under a running instance can corrupt
# an in-flight index or crash it outright, so refuse to install while any are up.
$existingLauncher = Join-Path $installDir 'codecontext.cmd'
if (Test-Path $existingLauncher) {
    $running = @()
    try {
        $runningJson = & $existingLauncher list --json 2>$null
        if ($LASTEXITCODE -eq 0 -and $runningJson) {
            $running = @(ConvertFrom-Json -InputObject ($runningJson -join "`n"))
        }
    } catch { }
    if ($running.Count -gt 0) {
        Write-Host "CodeContext is currently running for:" -ForegroundColor Yellow
        foreach ($instance in $running) {
            Write-Host "  - $($instance.rootPath) (pid $($instance.pid), port $($instance.port))"
        }
        Write-Host ""
        Write-Host "Stop all running instances first, then rerun the installer:"
        Write-Host "  codecontext stop --all"
        exit 1
    }
}

$release = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest"
$asset = $release.assets | Where-Object { $_.name -like "*-$rid.zip" } | Select-Object -First 1
if (-not $asset) { throw "No asset for $rid in release $($release.tag_name). Available: $($release.assets.name -join ', ')" }

Write-Host "Downloading $($asset.name) ($($release.tag_name))..."
$temporary = Join-Path ([System.IO.Path]::GetTempPath()) ("codecontext-install-" + [Guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Force $temporary | Out-Null
    $zipPath = Join-Path $temporary $asset.name
    $stageDir = Join-Path $temporary 'payload'
    Invoke-WebRequest $asset.browser_download_url -OutFile $zipPath
    Expand-Archive -Path $zipPath -DestinationPath $stageDir

    $required = @(
        'codecontext.exe',
        'workers\csharp\worker-manifest.json',
        'workers\csharp\CodeContext.CSharp.Worker.exe',
        'workers\typescript\worker-manifest.json',
        'workers\typescript\node.exe'
    )
    foreach ($relative in $required) {
        if (-not (Test-Path (Join-Path $stageDir $relative))) {
            throw "Release is missing $relative."
        }
    }

    $releaseRoot = Join-Path $codeContextHome 'releases'
    $releaseDir = Join-Path $releaseRoot $release.tag_name
    New-Item -ItemType Directory -Force $releaseRoot | Out-Null
    if (-not (Test-Path $releaseDir)) {
        Move-Item -LiteralPath $stageDir -Destination $releaseDir
    }
    New-Item -ItemType Directory -Force $installDir | Out-Null

    # Migrate the pre-Phase-5 in-place executable. A running old executable is locked
    # on Windows, so fail with a clear retry instead of producing a mixed install.
    $legacyExecutable = Join-Path $installDir 'codecontext.exe'
    if (Test-Path $legacyExecutable) {
        try { Remove-Item -LiteralPath $legacyExecutable -Force }
        catch { throw "Close running CodeContext instances, then rerun the installer to complete the upgrade." }
    }

    $currentPath = Join-Path $installDir 'current.txt'
    $currentTemp = Join-Path $installDir 'current.txt.new'
    [System.IO.File]::WriteAllText($currentTemp, $release.tag_name)
    Move-Item -LiteralPath $currentTemp -Destination $currentPath -Force

    $launcher = @'
@echo off
set /p CODECONTEXT_VERSION=<"%~dp0current.txt"
"%~dp0..\releases\%CODECONTEXT_VERSION%\codecontext.exe" %*
'@
    [System.IO.File]::WriteAllText((Join-Path $installDir 'codecontext.cmd'), $launcher)

    # Git Bash/MSYS resolves bare commands by exact filename and ignores PATHEXT, so
    # `codecontext.cmd` alone is invisible to it. Ship a second, extensionless POSIX
    # shim alongside it so the same installDir works from both cmd/PowerShell and
    # Git Bash. Built with explicit `n (not a here-string) so the shebang stays
    # LF-only regardless of this file's own line endings.
    $posixLauncherLines = @(
        '#!/bin/sh',
        'version=$(cat "$(dirname "$0")/current.txt")',
        'exec "$(dirname "$0")/../releases/$version/codecontext.exe" "$@"'
    )
    $posixLauncher = ($posixLauncherLines -join "`n") + "`n"
    [System.IO.File]::WriteAllText((Join-Path $installDir 'codecontext'), $posixLauncher)

    $skillInstaller = Join-Path $releaseDir 'skill\install-skill.ps1'
    if (Test-Path $skillInstaller) { & $skillInstaller }

    # current.txt/launchers now point at the new release, so old ones are dead weight.
    Get-ChildItem -Path $releaseRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -ne $releaseDir } |
        ForEach-Object {
            try { Remove-Item -LiteralPath $_.FullName -Recurse -Force }
            catch { Write-Host "Warning: could not remove old release $($_.FullName): $_" -ForegroundColor Yellow }
        }

    Write-Host "Installed $($release.tag_name) to $releaseDir"
}
finally {
    if (Test-Path $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
}

$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if ($userPath -notlike "*$installDir*") {
    [Environment]::SetEnvironmentVariable('Path', "$userPath;$installDir", 'User')
    Write-Host "Added $installDir to your user PATH. Open a new terminal, then run: codecontext --version"
} else {
    Write-Host "Run: codecontext --version"
}
