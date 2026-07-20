param(
    [string] $Configuration = 'Release',
    [string] $RuntimeIdentifier = 'win-x64',
    [switch] $DisableNuGetAudit
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solution = Join-Path $repoRoot 'CodexProfileLauncher.slnx'
$coreTests = Join-Path $repoRoot 'tests\CodexProfileLauncher.Core.Tests\CodexProfileLauncher.Core.Tests.csproj'
$windowsTests = Join-Path $repoRoot 'tests\CodexProfileLauncher.Windows.Tests\CodexProfileLauncher.Windows.Tests.csproj'
$appProject = Join-Path $repoRoot 'src\CodexProfileLauncher\CodexProfileLauncher.csproj'
$artifactsRoot = Join-Path $repoRoot 'artifacts'
$buildRoot = Join-Path $artifactsRoot 'build'
$testRoot = Join-Path $artifactsRoot 'test-results'
$publishRoot = Join-Path $artifactsRoot "publish\$RuntimeIdentifier"
$metadataPath = Join-Path $artifactsRoot 'release-metadata.json'
$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) {
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
}
[string[]] $nugetAuditArguments = if ($DisableNuGetAudit) {
    ,'-p:NuGetAudit=false'
} else {
    @()
}
[string[]] $buildOutputArguments = @(
    '-p:UseArtifactsOutput=true',
    "-p:ArtifactsPath=$buildRoot"
)

function Assert-InWorkspace {
    param([string] $Path)

    $resolved = [System.IO.Path]::GetFullPath($Path)
    $rootWithSeparator = $repoRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($rootWithSeparator, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside the workspace: $resolved"
    }

    return $resolved
}

foreach ($directory in @($buildRoot, $testRoot, $publishRoot)) {
    $safeDirectory = Assert-InWorkspace $directory
    if (Test-Path -LiteralPath $safeDirectory) {
        Remove-Item -LiteralPath $safeDirectory -Recurse -Force
    }

    New-Item -ItemType Directory -Path $safeDirectory -Force | Out-Null
}


$safeMetadataPath = Assert-InWorkspace $metadataPath
if (Test-Path -LiteralPath $safeMetadataPath) {
    Remove-Item -LiteralPath $safeMetadataPath -Force
}

& $dotnet restore $solution --locked-mode --disable-parallel -m:1 -nr:false `
    @nugetAuditArguments @buildOutputArguments
if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }

& $dotnet build $solution -c $Configuration --no-restore -warnaserror -m:1 -nr:false `
    -p:UseSharedCompilation=false @nugetAuditArguments @buildOutputArguments
if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }

& $dotnet test $coreTests -c $Configuration --no-build --no-restore `
    @buildOutputArguments `
    --results-directory $testRoot `
    --logger 'trx;LogFileName=core-release.trx'
if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }

& $dotnet test $windowsTests -c $Configuration --no-build --no-restore `
    @buildOutputArguments `
    --results-directory $testRoot `
    --logger 'trx;LogFileName=windows-release.trx'
if ($LASTEXITCODE -ne 0) { throw 'Windows tests failed.' }

& $dotnet publish $appProject -c $Configuration -r $RuntimeIdentifier `
    --self-contained true --no-restore -p:TreatWarningsAsErrors=true `
    @nugetAuditArguments @buildOutputArguments -o $publishRoot
if ($LASTEXITCODE -ne 0) { throw 'Single-file publish failed.' }

$publishedFiles = @(Get-ChildItem -LiteralPath $publishRoot -Recurse -File)
$publishedExecutables = @(Get-ChildItem -LiteralPath $publishRoot -File -Filter '*.exe')
$expectedExePath = Join-Path $publishRoot 'CodexProfileLauncher.exe'
if ($publishedExecutables.Count -ne 1 -or
    $publishedExecutables[0].FullName -ne $expectedExePath) {
    throw "Publish output must contain exactly one root CodexProfileLauncher.exe; found $($publishedExecutables.Count) root EXEs."
}

$requiredContentDirectories = @(
    (Join-Path $publishRoot 'Assets'),
    (Join-Path $publishRoot 'skills\builtin')
)
foreach ($requiredDirectory in $requiredContentDirectories) {
    if (-not (Test-Path -LiteralPath $requiredDirectory -PathType Container)) {
        throw "Required publish content directory is missing: $requiredDirectory"
    }
}

$builtinSkillFiles = @(
    Get-ChildItem -LiteralPath (Join-Path $publishRoot 'skills\builtin') `
        -Recurse -File -Filter 'SKILL.md'
)
if ($builtinSkillFiles.Count -eq 0) {
    throw 'Published built-in skill library contains no SKILL.md files.'
}

$exe = Get-Item -LiteralPath $expectedExePath
$signature = Get-AuthenticodeSignature -LiteralPath $exe.FullName
$fileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe.FullName)
if ([string]::IsNullOrWhiteSpace($fileVersion.ProductVersion)) {
    throw 'Published EXE has no product version; refusing to name the release archive.'
}

$archivePath = Join-Path $artifactsRoot "CodexProfileLauncher-v$($fileVersion.ProductVersion)-$RuntimeIdentifier.zip"
$safeArchivePath = Assert-InWorkspace $archivePath
$archiveStagingPath = "$safeArchivePath.staging-$PID-$([Guid]::NewGuid().ToString('N'))"
try {
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $publishRoot,
        $archiveStagingPath,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)

    $releaseArchive = [System.IO.Compression.ZipFile]::OpenRead($archiveStagingPath)
    try {
        $archiveFileEntries = @($releaseArchive.Entries | Where-Object { -not [string]::IsNullOrEmpty($_.Name) })
        $normalizedArchivePaths = @($archiveFileEntries | ForEach-Object { $_.FullName.Replace('\', '/') })
        if (@($normalizedArchivePaths | Where-Object { $_ -eq 'CodexProfileLauncher.exe' }).Count -ne 1) {
            throw 'Release archive must contain exactly one root CodexProfileLauncher.exe.'
        }
        if (-not @($normalizedArchivePaths | Where-Object { $_.StartsWith('Assets/', [System.StringComparison]::Ordinal) }).Count -or
            -not @($normalizedArchivePaths | Where-Object { $_.StartsWith('skills/builtin/', [System.StringComparison]::Ordinal) }).Count) {
            throw 'Release archive is missing Assets or skills/builtin content.'
        }
        if ($archiveFileEntries.Count -ne $publishedFiles.Count) {
            throw "Release archive file count $($archiveFileEntries.Count) does not match publish file count $($publishedFiles.Count)."
        }
    }
    finally {
        $releaseArchive.Dispose()
    }

    [System.IO.File]::Move($archiveStagingPath, $safeArchivePath, $true)
}
finally {
    if (Test-Path -LiteralPath $archiveStagingPath) {
        Remove-Item -LiteralPath $archiveStagingPath -Force
    }
}
$archiveFile = Get-Item -LiteralPath $safeArchivePath
$archiveSha256 = (Get-FileHash -LiteralPath $archiveFile.FullName -Algorithm SHA256).Hash

$testResults = foreach ($trxName in @('core-release.trx', 'windows-release.trx')) {
    $trxPath = Join-Path $testRoot $trxName
    [xml] $trx = Get-Content -LiteralPath $trxPath -Raw
    $counters = $trx.TestRun.ResultSummary.Counters
    [ordered]@{
        path = [System.IO.Path]::GetRelativePath($repoRoot, $trxPath).Replace('\', '/')
        total = [int] $counters.total
        passed = [int] $counters.passed
        failed = [int] $counters.failed
        executed = [int] $counters.executed
    }
}

$codexPackage = Get-AppxPackage -Name OpenAI.Codex -ErrorAction SilentlyContinue | Select-Object -First 1
$metadata = [ordered]@{
    generatedUtc = (Get-Date).ToUniversalTime().ToString('O')
    dotnetSdk = (& $dotnet --version).Trim()
    nugetAudit = if ($DisableNuGetAudit) {
        "disabled-for-deterministic-offline-gate"
    } else {
        "enabled"
    }
    runtimeIdentifier = $RuntimeIdentifier
    codexPackageVersion = if ($codexPackage) { $codexPackage.Version.ToString() } else { $null }
    publishedFileCount = $publishedFiles.Count
    builtinSkillCount = $builtinSkillFiles.Count
    executable = [ordered]@{
        path = [System.IO.Path]::GetRelativePath($repoRoot, $exe.FullName).Replace('\', '/')
        lengthBytes = $exe.Length
        sizeMiB = [Math]::Round($exe.Length / 1MB, 2)
        sha256 = (Get-FileHash -LiteralPath $exe.FullName -Algorithm SHA256).Hash
        authenticodeStatus = $signature.Status.ToString()
        productName = $fileVersion.ProductName
        productVersion = $fileVersion.ProductVersion
        fileDescription = $fileVersion.FileDescription
        fileVersion = $fileVersion.FileVersion
        companyName = $fileVersion.CompanyName
    }
    archive = [ordered]@{
        path = [System.IO.Path]::GetRelativePath($repoRoot, $archiveFile.FullName).Replace('\', '/')
        lengthBytes = $archiveFile.Length
        sizeMiB = [Math]::Round($archiveFile.Length / 1MB, 2)
        sha256 = $archiveSha256
        fileEntryCount = $archiveFileEntries.Count
    }
    tests = @($testResults)
}

$metadata | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $metadataPath -Encoding utf8
$metadata | ConvertTo-Json -Depth 8
