<#
.SYNOPSIS
Builds a test install of Photo Finder for Windows: a folder (and a zip of it) to run Install.cmd from.

.DESCRIPTION
For each architecture, makes PhotoFinder-<version>-<arch>\ containing:
  App\        the app, self-contained (no .NET install needed)
  AIHelper\   the Windows AI helper's package layout, for Describe Image on Copilot+ PCs
  Install.cmd / Install.ps1     installs for the current user, no admin needed
  Uninstall.cmd / Uninstall.ps1
  README.txt

The helper isn't signed, so Install registers it the way development does, which needs Developer Mode.
Without Developer Mode (or without an NPU), Photo Finder installs and runs, just without Describe Image.

PowerShell 5.1 compatible.

.PARAMETER Destination
Where the folders and zips go.

.PARAMETER Arch
arm64, x64, or both (the default).
#>
param(
    [Parameter(Mandatory = $true)] [string]$Destination,
    [ValidateSet('x64', 'arm64', 'both')] [string]$Arch = 'both'
)
$ErrorActionPreference = 'Stop'

$windows = Split-Path $PSScriptRoot -Parent
$version = (Get-Content -LiteralPath (Join-Path $windows 'VERSION') -Raw).Trim()
$arches = if ($Arch -eq 'both') { @('arm64', 'x64') } else { @($Arch) }
New-Item -ItemType Directory -Force -Path $Destination | Out-Null

foreach ($a in $arches) {
    $name = "PhotoFinder-$version-$a"
    $stage = Join-Path $windows "out\package\$name"
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $stage | Out-Null

    dotnet publish (Join-Path $windows 'src\PhotoFinder\PhotoFinder.csproj') -c Release -r "win-$a" --self-contained true -o (Join-Path $stage 'App') --nologo
    if ($LASTEXITCODE -ne 0) { throw "Publishing Photo Finder for $a failed." }
    Get-ChildItem -LiteralPath (Join-Path $stage 'App') -Filter *.pdb | Remove-Item -Force

    & (Join-Path $PSScriptRoot 'build-ai-helper.ps1') -Arch $a -Version $version
    Copy-Item -LiteralPath (Join-Path $windows "out\ai\$a") -Destination (Join-Path $stage 'AIHelper') -Recurse

    Copy-Item -Path (Join-Path $PSScriptRoot 'install\*') -Destination $stage
    (Get-Content -LiteralPath (Join-Path $stage 'README.txt') -Raw).Replace('$VERSION$', $version).Replace('$ARCH$', $a) |
        Set-Content -LiteralPath (Join-Path $stage 'README.txt') -Encoding UTF8

    $target = Join-Path $Destination $name
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
    Copy-Item -LiteralPath $stage -Destination $target -Recurse
    $zip = "$target.zip"
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
    Write-Host "Made $target and $zip"
}
