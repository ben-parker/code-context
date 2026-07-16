param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('win-x64', 'linux-x64', 'osx-x64', 'osx-arm64')]
    [string] $RuntimeIdentifier,
    [string] $NodeVersion = '22.15.0',
    [string] $WorkerDirectory = (Join-Path $PSScriptRoot '..\src\CodeContext.TypeScript.Worker')
)

$ErrorActionPreference = 'Stop'
$platform = switch ($RuntimeIdentifier) {
    'win-x64'   { 'win-x64' }
    'linux-x64' { 'linux-x64' }
    'osx-x64'   { 'darwin-x64' }
    'osx-arm64' { 'darwin-arm64' }
}
$isWindowsPayload = $RuntimeIdentifier -eq 'win-x64'
$extension = if ($isWindowsPayload) { 'zip' } else { 'tar.gz' }
$folder = "node-v$NodeVersion-$platform"
$archive = "$folder.$extension"
$url = "https://nodejs.org/dist/v$NodeVersion/$archive"
$temporary = Join-Path ([System.IO.Path]::GetTempPath()) ("codecontext-node-" + [Guid]::NewGuid().ToString('N'))

try {
    New-Item -ItemType Directory -Force $temporary | Out-Null
    $archivePath = Join-Path $temporary $archive
    $checksumsPath = Join-Path $temporary 'SHASUMS256.txt'
    Write-Host "Downloading Node.js v$NodeVersion for $RuntimeIdentifier..."
    Invoke-WebRequest $url -OutFile $archivePath
    Invoke-WebRequest "https://nodejs.org/dist/v$NodeVersion/SHASUMS256.txt" -OutFile $checksumsPath
    $checksumLine = Get-Content $checksumsPath | Where-Object { $_ -match "\s$([Regex]::Escape($archive))$" } | Select-Object -First 1
    if (-not $checksumLine) { throw "No upstream checksum found for $archive." }
    $expectedChecksum = ($checksumLine -split '\s+')[0].ToLowerInvariant()
    $actualChecksum = (Get-FileHash $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualChecksum -ne $expectedChecksum) {
        throw "Node.js archive checksum mismatch for $archive."
    }

    if ($isWindowsPayload) {
        Expand-Archive $archivePath -DestinationPath $temporary
    } else {
        & tar -xzf $archivePath -C $temporary
        if ($LASTEXITCODE -ne 0) { throw "tar failed with exit code $LASTEXITCODE" }
    }

    $payload = Join-Path $temporary $folder
    $sourceExecutable = if ($isWindowsPayload) {
        Join-Path $payload 'node.exe'
    } else {
        Join-Path (Join-Path $payload 'bin') 'node'
    }
    $targetName = if ($isWindowsPayload) { 'node.exe' } else { 'node' }
    $targetExecutable = Join-Path $WorkerDirectory $targetName
    New-Item -ItemType Directory -Force $WorkerDirectory | Out-Null
    Copy-Item $sourceExecutable $targetExecutable -Force
    Copy-Item (Join-Path $payload 'LICENSE') (Join-Path $WorkerDirectory 'node-LICENSE') -Force

    if (-not $isWindowsPayload) {
        & chmod +x $targetExecutable
        if ($LASTEXITCODE -ne 0) { throw "chmod failed with exit code $LASTEXITCODE" }
    }
    Write-Host "Prepared bundled Node.js runtime at $targetExecutable"
}
finally {
    if (Test-Path $temporary) {
        Remove-Item -LiteralPath $temporary -Recurse -Force
    }
}
