[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('win-x64', 'linux-x64', 'osx-x64', 'osx-arm64')]
    [string]$RuntimeIdentifier,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ReleaseVersion,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$PublishDirectory
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$publishPath = if ([IO.Path]::IsPathRooted($PublishDirectory)) {
    [IO.Path]::GetFullPath($PublishDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $repoRoot $PublishDirectory))
}

if (Test-Path -LiteralPath $publishPath) {
    if (Get-ChildItem -LiteralPath $publishPath -Force | Select-Object -First 1) {
        throw "Publish directory must be initially empty: $publishPath"
    }
} else {
    New-Item -ItemType Directory -Path $publishPath | Out-Null
}

$project = Join-Path $repoRoot 'src/CodeContext.Api/CodeContext.Api.csproj'
& dotnet publish $project -c Release -r $RuntimeIdentifier `
    --self-contained `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:Version=$ReleaseVersion `
    -p:InformationalVersion=$ReleaseVersion `
    -p:IncludeSourceRevisionInInformationalVersion=false `
    -o $publishPath
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$canonicalSkill = Join-Path $repoRoot 'skill/SKILL.md'
$packagedSkill = Join-Path $publishPath 'skill/SKILL.md'
if (-not (Test-Path -LiteralPath $packagedSkill -PathType Leaf)) {
    throw "Packaged skill is missing: $packagedSkill"
}
$canonicalHash = [Convert]::ToHexString(
    [Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($canonicalSkill)))
$packagedHash = [Convert]::ToHexString(
    [Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($packagedSkill)))
if ($canonicalHash -cne $packagedHash) {
    throw 'Packaged skill differs byte-for-byte from canonical skill/SKILL.md.'
}

$executableName = if ($RuntimeIdentifier -like 'win-*') { 'codecontext.exe' } else { 'codecontext' }
$executable = Join-Path $publishPath $executableName
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Published executable is missing: $executable"
}
$publishedVersion = (& $executable --version 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $publishedVersion -cne $ReleaseVersion) {
    throw "Published executable version '$publishedVersion' does not match '$ReleaseVersion'."
}

$fixture = Join-Path ([IO.Path]::GetTempPath()) ("codecontext-publish-smoke-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
[IO.File]::WriteAllText(
    (Join-Path $fixture 'Repository.cs'),
    "namespace PublishFixture;`npublic class Repository { public void Save() { } }`n")
[IO.File]::WriteAllText(
    (Join-Path $fixture 'RepositoryService.cs'),
    "namespace PublishFixture;`npublic class RepositoryService { public Repository Create() => new(); }`n")

$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
$listener.Stop()
$requestedInstanceId = [Guid]::NewGuid().ToString('N')
$logFile = Join-Path $fixture 'host.log'
$process = $null
$reportedInstanceId = $null

try {
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $executable
    $startInfo.WorkingDirectory = $fixture
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    foreach ($argument in @(
        'start', '--path', $fixture, '--port', $port.ToString(), '--idle-timeout', '0',
        '--instance-id', $requestedInstanceId, '--log-file', $logFile)) {
        $startInfo.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw 'Failed to start the published executable.'
    }

    $baseUri = "http://localhost:$port"
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes(2)
    $status = $null
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if ($process.HasExited) {
            throw "Published host exited before readiness with code $($process.ExitCode)."
        }
        try {
            $status = Invoke-RestMethod -Uri "$baseUri/api/status" -TimeoutSec 2
            if ($status.indexing.status -eq 'ready') {
                break
            }
        } catch {
            # Startup and indexing are asynchronous; retry until the bounded deadline.
        }
        Start-Sleep -Milliseconds 250
    }
    if ($null -eq $status -or $status.indexing.status -ne 'ready') {
        if (Test-Path -LiteralPath $logFile -PathType Leaf) {
            Write-Host 'Published host log:'
            Get-Content -LiteralPath $logFile | Write-Host
        }
        throw 'Published host did not report indexing.status=ready within two minutes.'
    }
    if ([string]::IsNullOrWhiteSpace([string]$status.system.informationalVersion)) {
        throw 'Published status omitted system.informationalVersion.'
    }
    if ([int]$status.api.contractVersion -ne 1) {
        throw "Published status reported contractVersion '$($status.api.contractVersion)' instead of 1."
    }
    $reportedInstanceId = [string]$status.system.instanceId
    if ([string]::IsNullOrWhiteSpace($reportedInstanceId)) {
        throw 'Published status omitted system.instanceId.'
    }

    $exact = Invoke-RestMethod -Uri "$baseUri/api/context/complete?identifier=Repository&depth=0" -TimeoutSec 10
    if ($exact.matchMode -ne 'exact' -or $exact.substringSearchSkipped -ne $true) {
        throw 'Packaged host did not expose exact-first match metadata.'
    }
    $substring = Invoke-RestMethod -Uri "$baseUri/api/context/complete?identifier=Repository&depth=0&exact=false" -TimeoutSec 10
    if ($substring.matchMode -ne 'substring' -or $substring.substringSearchSkipped -eq $true -or $substring.totalMatches -lt 2) {
        throw 'Packaged host did not expose explicit substring match metadata and broader results.'
    }

    Invoke-RestMethod -Method Post -Uri (
        "$baseUri/api/shutdown?instanceId=" + [Uri]::EscapeDataString($reportedInstanceId)) -TimeoutSec 10 | Out-Null
    if (-not $process.WaitForExit(15000)) {
        throw 'Published host did not exit after instanceId-authenticated shutdown.'
    }
} finally {
    if ($null -ne $process -and -not $process.HasExited) {
        $process.Kill($true)
        $process.WaitForExit(5000)
    }
    if (Test-Path -LiteralPath $fixture) {
        Remove-Item -LiteralPath $fixture -Recurse -Force
    }
}

Write-Host "Verified publish payload for $RuntimeIdentifier version $ReleaseVersion."
