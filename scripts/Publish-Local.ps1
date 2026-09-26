[CmdletBinding()]
param(
    [switch] $Launch,
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'FtdHullGenerator\FtdHullGenerator.csproj'
$publishDirectory = Join-Path $repositoryRoot 'publish\HullForge'
$shortcutPath = Join-Path $repositoryRoot 'Hull Forge.lnk'
$shortcutDescription = 'Launch Hull Forge'
$applicationExe = Join-Path $publishDirectory 'HullForge.exe'
$brandIconPath = Join-Path $repositoryRoot 'FtdHullGenerator\Assets\HullForge.ico'
$dotnetPath = (Get-Command dotnet -ErrorAction Stop).Source

if (Test-Path -LiteralPath $publishDirectory)
{
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $publishDirectory | Out-Null

$publishArguments = @(
    'publish',
    $projectPath,
    '--configuration', $Configuration,
    '--self-contained', 'false',
    '-p:UseAppHost=true',
    '--output', $publishDirectory
)
if ($Configuration -eq 'Release')
{
    $publishArguments += '-p:DebugType=None', '-p:DebugSymbols=false'
}
& $dotnetPath @publishArguments

if ($LASTEXITCODE -ne 0)
{
    throw "Hull Forge publish failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path -LiteralPath $applicationExe))
{
    throw "The published application executable was not found at $applicationExe."
}

$publishedExecutables = @(Get-ChildItem -LiteralPath $publishDirectory -Filter '*.exe' -File)
if ($publishedExecutables.Count -ne 1 -or $publishedExecutables[0].Name -ne 'HullForge.exe')
{
    throw "The product package must contain exactly one executable named HullForge.exe."
}

if (-not (Test-Path -LiteralPath $brandIconPath))
{
    throw "The Hull Forge brand icon was not found at $brandIconPath."
}

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $applicationExe
$shortcut.Arguments = ''
$shortcut.WorkingDirectory = $publishDirectory
$shortcut.Description = $shortcutDescription
# The launcher targets dotnet.exe, so without this the shortcut would wear the .NET logo
# instead of Hull Forge's own mark.
$shortcut.IconLocation = "$brandIconPath,0"
$shortcut.Save()

Write-Host "Published the single Hull Forge $Configuration product build to $publishDirectory"
Write-Host "Created launcher at $shortcutPath"

if ($Launch)
{
    Start-Process -FilePath $shortcutPath
}
