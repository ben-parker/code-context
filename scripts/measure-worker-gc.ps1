<#
.SYNOPSIS
    Measures C# worker GC/allocation behavior for one host run against a corpus.

.DESCRIPTION
    Starts the given codecontext executable watching -Path (mirroring
    scripts/measure-startup.ps1 launch/readiness/shutdown semantics), then:
      1. records time-to-listening and time-to-indexed,
      2. locates the csharp worker PID via /api/status,
      3. attaches dotnet-counters (System.Runtime, CSV) to the worker,
      4. runs -Rescans forced full rescans (POST /api/index/refresh), timing each,
      5. runs -Touches single-file incremental edits (append/revert a trailing
         space, ~1/sec), timing each touch-to-commit round-trip by watching the
         host log for new "atomically committed generation" lines (the host is
         launched with Logging__LogLevel__Default=Debug so the applier's
         LogDebug commit line is captured),
      6. detaches counters, shuts down, and writes a JSON summary plus the raw
         counters CSV to -OutDir.

    Worker env-var configs (e.g. ServerGC/DATAS A/B) are injected through the
    host's CodeContext:WorkerEnvironment configuration section by setting
    CodeContext__WorkerEnvironment__csharp__<KEY> variables on the host process
    (-WorkerEnv "DOTNET_gcServer=1","DOTNET_GCDynamicAdaptationMode=1").

.PARAMETER BinaryPath
    Path to the codecontext executable (published, or a JIT build output).

.PARAMETER Path
    Directory the instance should watch/index (the benchmark corpus).

.PARAMETER ConfigName
    Label for this configuration; used in output file names and the summary.

.PARAMETER RunIndex
    1-based run number; used in output file names.

.PARAMETER WorkerEnv
    KEY=VALUE strings injected into the csharp worker via the host's
    CodeContext:WorkerEnvironment:csharp configuration section.

.PARAMETER OutDir
    Directory that receives <ConfigName>-run<RunIndex>.counters.csv and
    <ConfigName>-run<RunIndex>.summary.json.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$BinaryPath,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$Path,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ConfigName,

    [int]$RunIndex = 1,

    [string[]]$WorkerEnv = @(),

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$OutDir,

    [int]$Rescans = 5,

    [int]$Touches = 20,

    [int]$TimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'

$executable = (Resolve-Path -LiteralPath $BinaryPath).Path
$watchPath = (Resolve-Path -LiteralPath $Path).Path
if (-not (Test-Path -LiteralPath $OutDir)) {
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
}
$outDirResolved = (Resolve-Path -LiteralPath $OutDir).Path
$runTag = "$ConfigName-run$RunIndex"
$countersCsv = Join-Path $outDirResolved "$runTag.counters.csv"
$summaryJson = Join-Path $outDirResolved "$runTag.summary.json"

$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
$listener.Stop()

$instanceId = [Guid]::NewGuid().ToString('N')
$logFile = Join-Path ([IO.Path]::GetTempPath()) ("codecontext-gcbench-" + $instanceId + '.log')
$baseUri = "http://localhost:$port"
$process = $null
$countersProcess = $null

function Wait-Until([scriptblock]$Condition, [datetime]$Deadline, [int]$PollMs, [string]$What) {
    while ([DateTimeOffset]::UtcNow -lt $Deadline) {
        $result = & $Condition
        if ($null -ne $result) { return $result }
        Start-Sleep -Milliseconds $PollMs
    }
    throw "Timed out waiting for: $What"
}

function Get-CommittedCount([string]$LogPath) {
    if (-not (Test-Path -LiteralPath $LogPath -PathType Leaf)) { return 0 }
    # Shared read: the host keeps the log open for append; tolerate partial last line.
    $stream = [IO.FileStream]::new($LogPath, [IO.FileMode]::Open, [IO.FileAccess]::Read,
        [IO.FileShare]([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try {
        $reader = [IO.StreamReader]::new($stream)
        $text = $reader.ReadToEnd()
    } finally {
        $stream.Dispose()
    }
    return ([regex]::Matches($text, 'atomically committed generation')).Count
}

try {
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $executable
    $startInfo.WorkingDirectory = $watchPath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    foreach ($argument in @(
        'start', '--path', $watchPath, '--port', $port.ToString(), '--idle-timeout', '0',
        '--instance-id', $instanceId, '--log-file', $logFile)) {
        $startInfo.ArgumentList.Add($argument)
    }
    # Debug logging so AnalysisDeltaApplier's commit LogDebug line lands in the log file.
    $startInfo.Environment['Logging__LogLevel__Default'] = 'Debug'
    foreach ($pair in $WorkerEnv) {
        $key, $value = $pair -split '=', 2
        if ([string]::IsNullOrWhiteSpace($key) -or $null -eq $value) {
            throw "WorkerEnv entry '$pair' is not KEY=VALUE."
        }
        $startInfo.Environment["CodeContext__WorkerEnvironment__csharp__$key"] = $value
    }

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) { throw 'Failed to start the codecontext executable.' }
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds).UtcDateTime

    $listeningMs = Wait-Until -Deadline $deadline -PollMs 25 -What '/healthz listening' -Condition {
        if ($process.HasExited) { throw "Host exited before listening with code $($process.ExitCode)." }
        try {
            if ($null -ne (Invoke-RestMethod -Uri "$baseUri/healthz" -TimeoutSec 2)) {
                [int]$stopwatch.Elapsed.TotalMilliseconds
            }
        } catch { $null }
    }

    $indexedMs = Wait-Until -Deadline $deadline -PollMs 50 -What 'indexing.status == ready' -Condition {
        if ($process.HasExited) { throw "Host exited before indexed with code $($process.ExitCode)." }
        try {
            $status = Invoke-RestMethod -Uri "$baseUri/api/status" -TimeoutSec 2
            if ($status.indexing.status -eq 'ready') { [int]$stopwatch.Elapsed.TotalMilliseconds }
        } catch { $null }
    }

    # Locate the csharp worker PID from parser sessions.
    $status = Invoke-RestMethod -Uri "$baseUri/api/status" -TimeoutSec 5
    $csharpSession = @($status.parsers.sessions) | Where-Object { $_.parserId -eq 'csharp' } | Select-Object -First 1
    if ($null -eq $csharpSession -or $null -eq $csharpSession.pid) {
        throw "No running csharp parser session with a pid in /api/status (sessions: $($status.parsers.sessions | ConvertTo-Json -Compress -Depth 4))."
    }
    $workerPid = [int]$csharpSession.pid

    # Attach dotnet-counters; stopped later by writing Q to its stdin.
    $countersInfo = [Diagnostics.ProcessStartInfo]::new()
    $countersInfo.FileName = 'dotnet-counters'
    $countersInfo.UseShellExecute = $false
    $countersInfo.CreateNoWindow = $true
    $countersInfo.RedirectStandardInput = $true
    $countersInfo.RedirectStandardOutput = $true
    $countersInfo.RedirectStandardError = $true
    foreach ($argument in @(
        'collect', '--process-id', $workerPid.ToString(), '--refresh-interval', '1',
        '--format', 'csv', '--output', $countersCsv, '--counters', 'System.Runtime')) {
        $countersInfo.ArgumentList.Add($argument)
    }
    $countersProcess = [Diagnostics.Process]::Start($countersInfo)
    Start-Sleep -Seconds 2   # let the EventPipe session establish before the workload

    # --- Workload part 1: forced full rescans ---
    $rescanMs = @()
    for ($i = 1; $i -le $Rescans; $i++) {
        $before = Invoke-RestMethod -Uri "$baseUri/api/status" -TimeoutSec 5
        $beforeOp = [long]$before.indexing.operationId
        $sw = [Diagnostics.Stopwatch]::StartNew()
        Invoke-RestMethod -Method Post -Uri "$baseUri/api/index/refresh" -TimeoutSec 10 | Out-Null
        Wait-Until -Deadline ([DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds).UtcDateTime) -PollMs 50 -What "rescan $i ready" -Condition {
            try {
                $s = Invoke-RestMethod -Uri "$baseUri/api/status" -TimeoutSec 2
                if ($s.indexing.status -eq 'ready' -and [long]$s.indexing.operationId -gt $beforeOp) { $true }
            } catch { $null }
        } | Out-Null
        $sw.Stop()
        $rescanMs += [int]$sw.Elapsed.TotalMilliseconds
    }

    # --- Workload part 2: incremental single-file touches ---
    # Mid-size file: median-by-length .cs file in the corpus.
    $csFiles = Get-ChildItem -LiteralPath $watchPath -Recurse -File -Filter '*.cs' | Sort-Object Length
    if ($csFiles.Count -eq 0) { throw "No .cs files under $watchPath." }
    $target = $csFiles[[int]($csFiles.Count / 2)].FullName
    $original = [IO.File]::ReadAllText($target)

    $touchMs = @()
    try {
        for ($i = 1; $i -le $Touches; $i++) {
            $baselineCommits = Get-CommittedCount $logFile
            # Alternate append/revert so file content (and corpus) ends unchanged.
            $newText = if ($i % 2 -eq 1) { $original + ' ' } else { $original }
            $sw = [Diagnostics.Stopwatch]::StartNew()
            [IO.File]::WriteAllText($target, $newText)
            Wait-Until -Deadline ([DateTimeOffset]::UtcNow.AddSeconds(60).UtcDateTime) -PollMs 10 -What "touch $i commit" -Condition {
                if ((Get-CommittedCount $logFile) -gt $baselineCommits) { $true }
            } | Out-Null
            $sw.Stop()
            $touchMs += [int]$sw.Elapsed.TotalMilliseconds
            Start-Sleep -Milliseconds 500   # settle between touches (~1/sec cadence)
        }
    } finally {
        [IO.File]::WriteAllText($target, $original)
    }

    # Stop dotnet-counters cleanly (Q keypress semantics via stdin).
    if ($null -ne $countersProcess -and -not $countersProcess.HasExited) {
        $countersProcess.StandardInput.Write('Q')
        $countersProcess.StandardInput.Flush()
        if (-not $countersProcess.WaitForExit(15000)) { $countersProcess.Kill($true) }
    }

    $summary = [ordered]@{
        config       = $ConfigName
        run          = $RunIndex
        workerEnv    = $WorkerEnv
        workerPid    = $workerPid
        listeningMs  = $listeningMs
        indexedMs    = $indexedMs
        rescanMs     = $rescanMs
        rescanMedian = ($rescanMs | Sort-Object)[[int]($rescanMs.Count / 2)]
        touchMs      = $touchMs
        touchMedian  = ($touchMs | Sort-Object)[[int]($touchMs.Count / 2)]
        countersCsv  = $countersCsv
    }
    $summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $summaryJson -Encoding utf8
    Write-Host "config=$ConfigName run=$RunIndex indexed_ms=$indexedMs rescan_med=$($summary.rescanMedian) touch_med=$($summary.touchMedian)"
    Write-Host "summary=$summaryJson"
} finally {
    if ($null -ne $countersProcess -and -not $countersProcess.HasExited) {
        try { $countersProcess.Kill($true) } catch { }
    }
    if ($null -ne $process -and -not $process.HasExited) {
        $shutdownUri = "$baseUri/api/shutdown?instanceId=" + [Uri]::EscapeDataString($instanceId)
        try {
            Invoke-RestMethod -Method Post -Uri $shutdownUri -TimeoutSec 10 | Out-Null
            $process.WaitForExit(15000) | Out-Null
        } catch { }
        if (-not $process.HasExited) {
            $process.Kill($true)
            $process.WaitForExit(5000) | Out-Null
        }
    }
    if (Test-Path -LiteralPath $logFile -PathType Leaf) {
        Copy-Item -LiteralPath $logFile -Destination (Join-Path $outDirResolved "$runTag.host.log") -Force
        Remove-Item -LiteralPath $logFile -Force
    }
}
