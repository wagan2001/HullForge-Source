[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'FtdHullGenerator\FtdHullGenerator.csproj'
$instructionsPath = Join-Path $PSScriptRoot 'Portable-START_HERE.txt'
$artifactsDirectory = Join-Path $repositoryRoot 'artifacts'
$dotnetPath = (Get-Command dotnet -ErrorAction Stop).Source

$sourceCommit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[0-9a-f]{40}$')
{
    throw 'Could not identify the source commit.'
}

$trackedChanges = @(& git -C $repositoryRoot status --porcelain --untracked-files=all)
if ($LASTEXITCODE -ne 0)
{
    throw 'Could not check the source worktree status.'
}
if ($trackedChanges.Count -gt 0)
{
    throw 'Commit or remove source worktree changes before creating a shareable package.'
}

New-Item -ItemType Directory -Path $artifactsDirectory -Force | Out-Null
$stageRoot = Join-Path $artifactsDirectory ('.portable-stage-' + [guid]::NewGuid().ToString('N'))
$packageDirectory = Join-Path $stageRoot 'HullForge'
$shortCommit = $sourceCommit.Substring(0, 12)
$archiveBase = 'HullForge-win-x64-{0}-{1}' -f $shortCommit, (Get-Date -Format 'yyyyMMdd-HHmmss')
$archivePath = Join-Path $artifactsDirectory ($archiveBase + '.zip')
$receiptPath = Join-Path $artifactsDirectory ($archiveBase + '.txt')

try
{
    New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
    & $dotnetPath publish $projectPath --configuration Release --runtime win-x64 --self-contained true `
        -p:UseAppHost=true -p:PublishSingleFile=false -p:PublishTrimmed=false `
        -p:DebugType=None -p:DebugSymbols=false --output $packageDirectory
    if ($LASTEXITCODE -ne 0)
    {
        throw "Hull Forge portable publish failed with exit code $LASTEXITCODE."
    }

    foreach ($requiredFile in @('HullForge.exe', 'HullForge.dll', 'hostfxr.dll', 'coreclr.dll', 'PresentationFramework.dll'))
    {
        if (-not (Test-Path -LiteralPath (Join-Path $packageDirectory $requiredFile) -PathType Leaf))
        {
            throw "The self-contained package is missing $requiredFile."
        }
    }

    Copy-Item -LiteralPath $instructionsPath -Destination (Join-Path $packageDirectory 'START_HERE.txt')
    Compress-Archive -LiteralPath $packageDirectory -DestinationPath $archivePath -CompressionLevel Optimal

    Add-Type -AssemblyName System.IO.Compression
    $archive = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
    try
    {
        $entryNames = @($archive.Entries | ForEach-Object FullName)
        foreach ($requiredEntry in @('HullForge/HullForge.exe', 'HullForge/START_HERE.txt', 'HullForge/hostfxr.dll'))
        {
            if ($entryNames -notcontains $requiredEntry)
            {
                throw "The ZIP is missing $requiredEntry."
            }
        }
        if (@($entryNames | Where-Object { -not $_.StartsWith('HullForge/', [StringComparison]::Ordinal) }).Count -gt 0)
        {
            throw 'The ZIP contains a file outside the HullForge folder.'
        }
    }
    finally
    {
        $archive.Dispose()
    }

    $archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    @(
        "Source commit: $sourceCommit"
        'Target: Windows x64, self-contained .NET 8 Desktop Runtime'
        "ZIP SHA-256: $archiveHash"
        "ZIP filename: $($archiveBase + '.zip')"
    ) | Set-Content -LiteralPath $receiptPath -Encoding utf8

    Write-Host "Portable ZIP: $archivePath"
    Write-Host "Receipt: $receiptPath"
    Write-Host "SHA-256: $archiveHash"
}
finally
{
    # Only remove this script's newly created staging directory under artifacts.
    $resolvedArtifacts = [System.IO.Path]::GetFullPath($artifactsDirectory).TrimEnd('\') + '\'
    $resolvedStage = [System.IO.Path]::GetFullPath($stageRoot)
    if (-not $resolvedStage.StartsWith($resolvedArtifacts, [StringComparison]::OrdinalIgnoreCase))
    {
        throw 'Refusing to clean a staging directory outside artifacts.'
    }
    if (Test-Path -LiteralPath $stageRoot)
    {
        Remove-Item -LiteralPath $stageRoot -Recurse -Force
    }
}
