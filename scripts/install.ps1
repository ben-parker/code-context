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

function Test-InstallerInteractiveConsole {
    try {
        return -not [Console]::IsInputRedirected -and -not [Console]::IsOutputRedirected -and
            [Console]::WindowWidth -gt 0
    } catch {
        return $false
    }
}

function Test-PathEntry {
    param([Parameter(Mandatory = $true)][string]$LiteralPath)
    return $null -ne (Get-Item -LiteralPath $LiteralPath -Force -ErrorAction SilentlyContinue)
}

function Throw-IfControlC {
    param([Parameter(Mandatory = $true)][ConsoleKeyInfo]$Key)
    if ($Key.Key -eq [ConsoleKey]::C -and
        ($Key.Modifiers -band [ConsoleModifiers]::Control) -ne 0) {
        throw [OperationCanceledException]::new('Installation cancelled.')
    }
}

function Select-SkillTargets {
    param([Parameter(Mandatory = $true)][object[]]$Targets)

    $cursor = 0
    $menuTop = 0
    $originalCursorVisible = $true
    try {
        Write-Host ''
        Write-Host 'Select agent skill targets (up/down or j/k to move, space to toggle, 1-6 to toggle a row, enter to continue):'
        Write-Host ''
        $menuTop = [Console]::CursorTop
        $originalCursorVisible = [Console]::CursorVisible
        [Console]::CursorVisible = $false

        while ($true) {
            [Console]::SetCursorPosition(0, $menuTop)
            for ($index = 0; $index -lt $Targets.Count; $index++) {
                $pointer = if ($index -eq $cursor) { '>' } else { ' ' }
                $mark = if ($Targets[$index].Selected) { 'x' } else { ' ' }
                $line = '{0} [{1}] {2,-15} ({3})' -f $pointer, $mark, $Targets[$index].Label, $Targets[$index].Hint
                $width = [Math]::Max(1, [Console]::WindowWidth - 1)
                if ($line.Length -gt $width) { $line = $line.Substring(0, $width) }
                Write-Host $line.PadRight($width)
            }

            $key = [Console]::ReadKey($true)
            Throw-IfControlC $key
            # Letters and digits are matched on the character typed, so a host that
            # reports no virtual key for them, or none for the arrows, still has a way
            # to move.
            $typed = [string]$key.KeyChar
            if ($key.Key -eq [ConsoleKey]::Enter) {
                [Console]::SetCursorPosition(0, $menuTop + $Targets.Count)
                return $Targets
            } elseif ($key.Key -eq [ConsoleKey]::UpArrow -or $typed -eq 'k') {
                $cursor = if ($cursor -eq 0) { $Targets.Count - 1 } else { $cursor - 1 }
            } elseif ($key.Key -eq [ConsoleKey]::DownArrow -or $typed -eq 'j') {
                $cursor = if ($cursor -eq $Targets.Count - 1) { 0 } else { $cursor + 1 }
            } elseif ($key.Key -eq [ConsoleKey]::Spacebar -or $typed -eq ' ') {
                $Targets[$cursor].Selected = -not $Targets[$cursor].Selected
            } elseif ($typed -match '^[1-9]$' -and [int]$typed -le $Targets.Count) {
                $cursor = [int]$typed - 1
                $Targets[$cursor].Selected = -not $Targets[$cursor].Selected
            }
        }
    } finally {
        try { [Console]::CursorVisible = $originalCursorVisible } catch { }
    }
}

function Confirm-SkillOverwrite {
    param([Parameter(Mandatory = $true)][object]$Target)

    Write-Host -NoNewline "Overwrite $($Target.Label) at $($Target.Path)? [Y/n] "
    while ($true) {
        $key = [Console]::ReadKey($true)
        Throw-IfControlC $key
        switch ($key.Key) {
            ([ConsoleKey]::Y) { Write-Host 'Yes'; return $true }
            ([ConsoleKey]::Enter) { Write-Host 'Yes'; return $true }
            ([ConsoleKey]::N) { Write-Host 'No'; return $false }
        }
    }
}

function Write-DownloadProgress {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][long]$Total
    )

    # Ask an open handle for the length: the directory entry of a file another process
    # is still writing reports a stale size on Windows.
    $received = 0L
    try {
        $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read,
            ([System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete))
        try { $received = $stream.Length } finally { $stream.Dispose() }
    } catch { }

    $invariant = [System.Globalization.CultureInfo]::InvariantCulture
    $receivedText = ($received / 1MB).ToString('0.0', $invariant)
    if ($Total -gt 0) {
        if ($received -gt $Total) { $received = $Total }
        $filled = [int][Math]::Floor($received * 30 / $Total)
        $percent = [int][Math]::Floor($received * 100 / $Total)
        $line = '  [{0}{1}] {2,3}%  {3} / {4} MB' -f ('#' * $filled), ('-' * (30 - $filled)), $percent,
            $receivedText, ($Total / 1MB).ToString('0.0', $invariant)
    } else {
        $line = "  $receivedText MB"
    }
    $width = [Math]::Max(1, [Console]::WindowWidth - 1)
    if ($line.Length -gt $width) { $line = $line.Substring(0, $width) }
    Write-Host -NoNewline ("`r" + $line.PadRight($width))
}

# Downloads $Url to $Path, drawing progress against the size the release lists. curl's own
# --progress-bar animates a placeholder until the redirected response reports a length,
# which flashes on screen before the real bar.
function Save-ReleaseAsset {
    param(
        [Parameter(Mandatory = $true)][string]$Url,
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][long]$Total
    )

    $arguments = '-fsSL --retry 3 --retry-delay 1 --connect-timeout 15 --speed-limit 1024 --speed-time 30'
    if (-not (Test-InstallerInteractiveConsole)) {
        & curl.exe @($arguments -split ' ') -o $Path $Url
        return $LASTEXITCODE
    }

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new('curl.exe')
    $startInfo.UseShellExecute = $false
    $startInfo.Arguments = '{0} -o "{1}" "{2}"' -f $arguments, $Path, $Url
    $download = [System.Diagnostics.Process]::Start($startInfo)
    try {
        while (-not $download.WaitForExit(200)) {
            Write-DownloadProgress -Path $Path -Total $Total
        }
        if ($download.ExitCode -eq 0) { Write-DownloadProgress -Path $Path -Total $Total }
        Write-Host ''
        return $download.ExitCode
    } finally {
        if (-not $download.HasExited) {
            try { $download.Kill() } catch { }
        }
        $download.Dispose()
    }
}

function Install-AgentSkillTarget {
    param(
        [Parameter(Mandatory = $true)][object]$Target,
        [Parameter(Mandatory = $true)][string]$SkillSource
    )

    if ($Target.Action -eq 'Skip') { return 'Skipped' }
    if ($Target.Action -eq 'None') { return 'None' }

    $parent = Split-Path -Parent $Target.Path
    $staged = Join-Path $parent ('.code-context.new.' + [Guid]::NewGuid().ToString('N'))
    $backup = Join-Path $parent ('.code-context.backup.' + [Guid]::NewGuid().ToString('N'))
    $movedOriginal = $false
    try {
        if ($Target.Action -eq 'New' -and (Test-PathEntry $Target.Path)) {
            throw 'The target appeared after confirmation and was preserved.'
        }
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
        New-Item -ItemType Directory -Path $staged | Out-Null
        Copy-Item -LiteralPath (Join-Path $SkillSource 'SKILL.md') -Destination (Join-Path $staged 'SKILL.md')
        Copy-Item -LiteralPath (Join-Path $SkillSource 'references') -Destination (Join-Path $staged 'references') -Recurse

        if (Test-PathEntry $Target.Path) {
            Move-Item -LiteralPath $Target.Path -Destination $backup
            $movedOriginal = $true
        }
        try {
            Move-Item -LiteralPath $staged -Destination $Target.Path
        } catch {
            if ($movedOriginal) {
                try { Move-Item -LiteralPath $backup -Destination $Target.Path }
                catch {
                    Write-Host "Warning: automatic restore failed for $($Target.Label); the original remains at $backup." -ForegroundColor Yellow
                }
            }
            throw
        }
        if ($movedOriginal -and (Test-PathEntry $backup)) {
            try { Remove-Item -LiteralPath $backup -Recurse -Force }
            catch { Write-Host "Warning: could not remove temporary backup $backup." -ForegroundColor Yellow }
        }
        return 'Installed'
    } catch {
        if (Test-PathEntry $staged) {
            try { Remove-Item -LiteralPath $staged -Recurse -Force } catch { }
        }
        Write-Host "Warning: could not install the $($Target.Label) skill at $($Target.Path): $_" -ForegroundColor Yellow
        return 'Failed'
    }
}

# Swapping the launcher/release payload out from under a running instance can corrupt
# an in-flight index or crash it outright, so refuse to install while any are up.
$existingLauncher = Join-Path $installDir 'codecontext.cmd'
if (Test-Path $existingLauncher) {
    $running = @()
    try {
        $runningJson = & $existingLauncher list --json 2>$null
        if ($LASTEXITCODE -eq 0 -and $runningJson) {
            # Windows PowerShell 5.1 hands back a JSON array as ONE object, so an empty
            # list counted as a running instance there and blocked every upgrade. Passing
            # it through a script block unrolls it in both editions.
            $running = @(ConvertFrom-Json -InputObject ($runningJson -join "`n") |
                ForEach-Object { $_ } | Where-Object { $_ -and $_.pid })
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

# Windows PowerShell paints a progress banner over the console for even this small
# request; the child scope keeps the preference out of the caller's session.
$release = & {
    $ProgressPreference = 'SilentlyContinue'
    Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest"
}
$asset = $release.assets | Where-Object { $_.name -like "*-$rid.zip" } | Select-Object -First 1
if (-not $asset) { throw "No asset for $rid in release $($release.tag_name). Available: $($release.assets.name -join ', ')" }

Write-Host "Downloading $($asset.name) ($($release.tag_name))..."
$temporary = Join-Path ([System.IO.Path]::GetTempPath()) ("codecontext-install-" + [Guid]::NewGuid().ToString('N'))
$codexHome = if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $HOME '.codex' }
$skillTargets = @(
    [pscustomobject]@{ Label = 'Shared agents'; Hint = '~/.agents/skills'; Path = (Join-Path $HOME '.agents/skills/code-context'); Selected = $false; Action = 'None' },
    [pscustomobject]@{ Label = 'Claude Code'; Hint = '~/.claude/skills'; Path = (Join-Path $HOME '.claude/skills/code-context'); Selected = $false; Action = 'None' },
    [pscustomobject]@{ Label = 'Devin Desktop'; Hint = '~/.codeium/windsurf/skills'; Path = (Join-Path $HOME '.codeium/windsurf/skills/code-context'); Selected = $false; Action = 'None' },
    [pscustomobject]@{ Label = 'Codex (legacy)'; Hint = '~/.codex/skills'; Path = (Join-Path $codexHome 'skills/code-context'); Selected = $false; Action = 'None' },
    [pscustomobject]@{ Label = 'Cursor'; Hint = '~/.cursor/skills'; Path = (Join-Path $HOME '.cursor/skills/code-context'); Selected = $false; Action = 'None' },
    [pscustomobject]@{ Label = 'Gemini CLI'; Hint = '~/.gemini/skills'; Path = (Join-Path $HOME '.gemini/skills/code-context'); Selected = $false; Action = 'None' }
)

# Pre-select targets that already have a code-context skill installed, so an upgrade
# refreshes exactly what's there instead of re-asking about every possible agent. Only
# when nothing is detected anywhere do we fall back to the original default (shared only).
$anyExistingSkill = $false
foreach ($target in $skillTargets) {
    if (Test-PathEntry $target.Path) {
        $target.Selected = $true
        $anyExistingSkill = $true
    }
}
if (-not $anyExistingSkill) {
    foreach ($target in $skillTargets) {
        if ($target.Label -eq 'Shared agents') { $target.Selected = $true }
    }
}

$skillsInteractive = $false
$cancelled = $false
try {
    New-Item -ItemType Directory -Force $temporary | Out-Null
    $zipPath = Join-Path $temporary $asset.name
    $stageDir = Join-Path $temporary 'payload'
    $downloadExitCode = Save-ReleaseAsset -Url $asset.browser_download_url -Path $zipPath -Total ([long]$asset.size)
    if ($downloadExitCode -ne 0) {
        throw "Download failed. Check access to github.com and release-assets.githubusercontent.com, then retry."
    }

    Write-Host "Download complete. Extracting release..."
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
    foreach ($relative in @(
        'skill\SKILL.md',
        'skill\references\native-syntax.md',
        'skill\references\operations.md')) {
        if (-not (Test-Path -LiteralPath (Join-Path $stageDir $relative) -PathType Leaf)) {
            throw "Release is missing $relative."
        }
    }

    # Gather every selection and overwrite decision before mutating the installed release.
    if (Test-InstallerInteractiveConsole) {
        try {
            $skillTargets = @(Select-SkillTargets $skillTargets)
            $skillsInteractive = $true
        } catch [OperationCanceledException] {
            throw
        } catch {
            Write-Host "No usable interactive terminal is available; skipping agent skill installation."
            foreach ($target in $skillTargets) { $target.Selected = $false }
        }
    } else {
        Write-Host "No interactive terminal is available; skipping agent skill installation."
        foreach ($target in $skillTargets) { $target.Selected = $false }
    }
    if ($skillsInteractive) {
        foreach ($target in $skillTargets) {
            if (-not $target.Selected) { continue }
            if (Test-PathEntry $target.Path) {
                $target.Action = if (Confirm-SkillOverwrite $target) { 'Replace' } else { 'Skip' }
            } else {
                $target.Action = 'New'
            }
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
    $removalNoted = $false
    if (Test-Path $legacyExecutable) {
        Write-Host "Removing previous CodeContext versions..."
        $removalNoted = $true
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

    # Install selected skills only after the launchers point at the new release. A
    # failure is reported per target and does not affect the binary or other targets.
    if ($skillsInteractive) {
        $skillResults = @{ Installed = @(); Skipped = @(); Failed = @() }
        foreach ($target in $skillTargets) {
            $result = Install-AgentSkillTarget -Target $target -SkillSource (Join-Path $releaseDir 'skill')
            if ($result -in @('Installed', 'Skipped', 'Failed')) {
                $skillResults[$result] += $target.Label
            }
        }
        if (-not ($skillTargets | Where-Object Selected)) {
            $skillResults.Skipped += 'all targets (none selected)'
        }
        Write-Host 'Agent skill installation summary:'
        foreach ($result in @('Installed', 'Skipped', 'Failed')) {
            $text = if ($skillResults[$result].Count -gt 0) { $skillResults[$result] -join ', ' } else { 'none' }
            Write-Host "  ${result}: $text"
        }
    }

    # current.txt/launchers now point at the new release, so old ones are dead weight.
    $oldReleases = @(
        Get-ChildItem -Path $releaseRoot -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -ne $releaseDir }
    )
    if ($oldReleases.Count -gt 0) {
        if (-not $removalNoted) {
            Write-Host "Removing previous CodeContext versions..."
        }
        foreach ($oldRelease in $oldReleases) {
            try { Remove-Item -LiteralPath $oldRelease.FullName -Recurse -Force }
            catch { Write-Host "Warning: could not remove old release $($oldRelease.FullName): $_" -ForegroundColor Yellow }
        }
    }

    Write-Host "Installed $($release.tag_name) to $releaseDir"
} catch [OperationCanceledException] {
    Write-Host ''
    Write-Host 'Installation cancelled.' -ForegroundColor Yellow
    $cancelled = $true
}
finally {
    if (Test-Path $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
}

if ($cancelled) { return }

$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if ($userPath -notlike "*$installDir*") {
    [Environment]::SetEnvironmentVariable('Path', "$userPath;$installDir", 'User')
    Write-Host "Added $installDir to your user PATH. Open a new terminal, then run: codecontext --version"
} else {
    Write-Host "Run: codecontext --version"
}
