<#
.SYNOPSIS
    Measures cold-start timings for a published codecontext host binary.

.DESCRIPTION
    Starts the given codecontext executable watching -Path on a random free port,
    then measures:
      (a) milliseconds from process start until the HTTP server is listening
          (the first successful /healthz response), and
      (b) milliseconds from process start until indexing.status == "ready" in
          /api/status.
    It prints both numbers plus the chosen port, then shuts the instance down via
    POST /api/shutdown (authenticated with the instance id it launched under),
    falling back to Stop-Process if the graceful shutdown does not complete.

    The wait loops mirror scripts/verify-publish.ps1 so the readiness semantics
    stay identical to the release smoke test.

.PARAMETER BinaryPath
    Path to the published codecontext executable (codecontext.exe on Windows).

.PARAMETER Path
    Directory the instance should watch/index.

.PARAMETER TimeoutSeconds
    Maximum seconds to wait for both listening and indexed readiness (default 180).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$BinaryPath,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$Path,

    [int]$TimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'

$executable = (Resolve-Path -LiteralPath $BinaryPath).Path
$watchPath = (Resolve-Path -LiteralPath $Path).Path

# Reserve a free loopback port the same way verify-publish.ps1 does: bind to 0,
# read the assigned port, then release it before the host claims it.
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
$listener.Stop()

$instanceId = [Guid]::NewGuid().ToString('N')
$logFile = Join-Path ([IO.Path]::GetTempPath()) ("codecontext-measure-" + $instanceId + '.log')
$baseUri = "http://localhost:$port"
$process = $null
$listeningMs = $null
$indexedMs = $null

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

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw 'Failed to start the codecontext executable.'
    }

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)

    # (a) Time-to-listening: first successful /healthz response.
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if ($process.HasExited) {
            throw "Host exited before listening with code $($process.ExitCode)."
        }
        try {
            $health = Invoke-RestMethod -Uri "$baseUri/healthz" -TimeoutSec 2
            if ($null -ne $health) {
                $listeningMs = [int]$stopwatch.Elapsed.TotalMilliseconds
                break
            }
        } catch {
            # Server not up yet; retry until the bounded deadline.
        }
        Start-Sleep -Milliseconds 25
    }
    if ($null -eq $listeningMs) {
        throw "Host did not begin listening on /healthz within $TimeoutSeconds seconds."
    }

    # (b) Time-to-indexed: indexing.status == "ready" in /api/status.
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if ($process.HasExited) {
            throw "Host exited before indexing readiness with code $($process.ExitCode)."
        }
        try {
            $status = Invoke-RestMethod -Uri "$baseUri/api/status" -TimeoutSec 2
            if ($status.indexing.status -eq 'ready') {
                $indexedMs = [int]$stopwatch.Elapsed.TotalMilliseconds
                break
            }
        } catch {
            # Indexing is asynchronous; retry until the bounded deadline.
        }
        Start-Sleep -Milliseconds 50
    }
    $stopwatch.Stop()
    if ($null -eq $indexedMs) {
        if (Test-Path -LiteralPath $logFile -PathType Leaf) {
            Write-Host 'Host log:'
            Get-Content -LiteralPath $logFile | Write-Host
        }
        throw "Host did not report indexing.status=ready within $TimeoutSeconds seconds."
    }

    [PSCustomObject]@{
        Port           = $port
        ListeningMs    = $listeningMs
        IndexedMs      = $indexedMs
    } | Format-List | Out-String | Write-Host

    Write-Host "port=$port listening_ms=$listeningMs indexed_ms=$indexedMs"
} finally {
    if ($null -ne $process -and -not $process.HasExited) {
        $shutdownUri = "$baseUri/api/shutdown?instanceId=" + [Uri]::EscapeDataString($instanceId)
        try {
            Invoke-RestMethod -Method Post -Uri $shutdownUri -TimeoutSec 10 | Out-Null
            $process.WaitForExit(15000) | Out-Null
        } catch {
            # Fall through to a hard kill below.
        }
        if (-not $process.HasExited) {
            $process.Kill($true)
            $process.WaitForExit(5000) | Out-Null
        }
    }
    if (Test-Path -LiteralPath $logFile -PathType Leaf) {
        Remove-Item -LiteralPath $logFile -Force
    }
}
