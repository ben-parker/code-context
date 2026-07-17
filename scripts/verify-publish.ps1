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
    [string]$PublishDirectory,

    # Debug symbols are NOT part of the shipped payload; by default they are pruned from
    # the publish root (see the cleanup below). Pass -KeepSymbols to leave them in place
    # for local native debugging.
    [switch]$KeepSymbols
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
# The host publishes as Native AOT (PublishAot=true in the csproj): ILC emits a single
# native binary, so PublishSingleFile/IncludeNativeLibrariesForSelfExtract are meaningless
# and are intentionally omitted.
& dotnet publish $project -c Release -r $RuntimeIdentifier `
    --self-contained `
    -o $publishPath
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

# Strip debug symbols from the publish ROOT (not workers/**). Native AOT emits a separate
# .pdb next to the host binary on Windows (~125MB — StripSymbols is deliberately non-Windows
# only), and a handful of managed .pdb files (Core/Mcp/Parser.Protocol) also land here.
# release.yml zips the publish directory verbatim, so any .pdb left here ships in the release
# archive — nearly tripling its size. Debug symbols are not part of the shipped payload;
# remove them so what CI zips is exactly the shippable payload. Workers keep their symbols
# (they are already part of the self-contained worker payload the size table accounts for).
# Contributors debugging locally can pass -KeepSymbols to skip this.
if (-not $KeepSymbols) {
    Get-ChildItem -LiteralPath $publishPath -Filter '*.pdb' -File |
        Remove-Item -Force
}

# AOT payload guard: a Native AOT publish must leave NO managed *.dll next to the host
# binary in the publish root. This is the permanent guard that unreferenced packages
# (e.g. Microsoft.AspNetCore.OpenApi, which is referenced but never rooted in Release)
# do not leak into the shipped host payload. The *.pdb guard likewise ensures debug
# symbols (native AOT PDB + managed PDBs) never re-enter the shipped root — the cleanup
# above must have removed them (unless -KeepSymbols was passed). The language workers
# legitimately ship managed DLLs and their PDBs (JIT+R2R), so workers/csharp/** and
# workers/typescript/** are exempt.
# A stray *.dll always fails the guard. A stray *.pdb fails too, unless -KeepSymbols was
# passed (in which case the contributor deliberately kept native symbols for local debugging).
$strayExtensions = if ($KeepSymbols) { @('.dll') } else { @('.dll', '.pdb') }
$strayFiles = Get-ChildItem -LiteralPath $publishPath -File |
    Where-Object { $_.Extension -in $strayExtensions } |
    Select-Object -ExpandProperty Name
if ($strayFiles) {
    throw ("Native AOT host publish leaked managed DLL/PDB file(s) into the publish root: " +
        ($strayFiles -join ', ') + ". The AOT host must be a single native binary with no shipped symbols.")
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
# nbgv's InformationalVersion always carries a `+<gitcommit>` build-metadata suffix
# (even on a public-release tag build), so compare only the semantic-version portion
# against the resolved release tag.
$publishedVersionCore = $publishedVersion.Split('+')[0]
if ($LASTEXITCODE -ne 0 -or $publishedVersionCore -cne $ReleaseVersion) {
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

# The graph edge-kind contract (tests/.../Workers/GraphContractTests.cs) is also a live
# guardrail: both language workers must actually emit every kind ContextService consumes.
# These extra fixtures exercise every contracted C# and TypeScript kind so the smoke test
# below can assert the packaged host's graph reports them all with count > 0.
$csharpContractFixture = @'
namespace PublishFixture;
using System;

public sealed class FactAttribute : Attribute { }

// Interface implementation, inheritance, and method families (IMPLEMENTS, INHERITS,
// IMPLEMENTS_MEMBER, OVERRIDES_MEMBER, HAS_METHOD).
public interface IBase<T> { void Work(T value); }
public interface IDerived : IBase<int> { }
public class FirstWorker : IDerived { public void Work(int value) { } }
public class SecondWorker : IDerived { void IBase<int>.Work(int value) { } }
public class RunBase { public virtual void Run(int value) { } }
public class RunDerived : RunBase { public override void Run(int value) { } }

// HAS_PROPERTY, REFERENCES (to a concrete type), and CALLS.
public class Widget { }
public class Assembler
{
    public int Count { get; set; }
    private Widget _widget;
    public Widget Current => _widget;
    public void Build() { Consume(); }
    public void Consume() { }
}

// MOCK_CALLS: NSubstitute-shaped fluent/mock calls (self-contained stand-ins).
public interface IService { int Get(int value); }
public static class MockExtensions
{
    public static T Received<T>(this T value) => value;
    public static T Returns<T>(this T value, T configured) => value;
}
public class ServiceTests
{
    [Fact]
    public void Verify()
    {
        IService service = null;
        service.Received().Get(1);
        service.Get(3).Returns(4);
    }
}
'@
[IO.File]::WriteAllText((Join-Path $fixture 'Contracts.cs'), $csharpContractFixture)

# TypeScript fixtures exercise EXTENDS, HAS_FIELD, IMPORTS (and the shared kinds) through
# the packaged Node worker. A minimal tsconfig makes it a real project so imports resolve.
[IO.File]::WriteAllText(
    (Join-Path $fixture 'tsconfig.json'),
    '{ "compilerOptions": { "strict": false, "target": "ES2020", "module": "ESNext" } }' + "`n")
$typeScriptBaseModule = @'
export interface IService { run(): void; }
export class Base {
    count: number = 0;
    get label(): string { return 'b'; }
    greet(): string { return 'hi'; }
}
'@
[IO.File]::WriteAllText((Join-Path $fixture 'base.ts'), $typeScriptBaseModule)
$typeScriptMainModule = @'
import { Base, IService } from './base';
export class Derived extends Base {
    greet(): string { return this.helper(); }
    helper(): string { return 'x'; }
}
export class Impl implements IService {
    run(): void { }
}
'@
[IO.File]::WriteAllText((Join-Path $fixture 'main.ts'), $typeScriptMainModule)

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

    # Graph edge-kind contract: every edge kind ContextService consumes must be produced by
    # a real worker in the packaged host (see GraphContractTests). Assert the live graph
    # reports each contracted C# and TypeScript kind (both workers, not the RESERVED set)
    # with count > 0. A missing kind means a worker regressed or failed to start.
    $edgeTypes = $status.database.edgeTypes
    if ($null -eq $edgeTypes) {
        throw 'Published status omitted database.edgeTypes.'
    }
    $contractedEdgeKinds = @(
        'CALLS', 'MOCK_CALLS', 'REFERENCES', 'IMPLEMENTS', 'INHERITS',
        'IMPLEMENTS_MEMBER', 'OVERRIDES_MEMBER', 'HAS_METHOD', 'HAS_PROPERTY',
        'EXTENDS', 'HAS_FIELD', 'IMPORTS')
    $missingEdgeKinds = foreach ($kind in $contractedEdgeKinds) {
        $property = $edgeTypes.PSObject.Properties[$kind]
        if ($null -eq $property -or [int]$property.Value -le 0) { $kind }
    }
    if ($missingEdgeKinds) {
        $present = ($edgeTypes.PSObject.Properties |
            ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ', '
        throw ("Published graph is missing contracted edge kind(s): " +
            ($missingEdgeKinds -join ', ') + ". Present edge types: $present.")
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
