<#
.SYNOPSIS
Builds Photo Finder for Windows for release: a portable ZIP per architecture, and the AI helper's
package beside it.

.DESCRIPTION
For each architecture, publishes the app self-contained (no .NET install needed) into
windows\out\app\<arch>, zips it to windows\artifacts\PhotoFinder-<version>-win-<arch>.zip, and packs
the Windows AI helper to windows\artifacts\PhotoFinderAI_<arch>.msix with build-ai-helper.ps1.

The helper's package is unsigned. Windows installs an unsigned package only in Developer Mode
(build-ai-helper.ps1 -Register); for a public release it has to be signed with a certificate whose
subject is the -Publisher passed here.

PowerShell 5.1 compatible.

.PARAMETER Arch
x64, arm64, or both (the default).

.PARAMETER Publisher
The helper package's publisher; must equal the signing certificate's subject.

.PARAMETER Revision
The helper package version's fourth number, unique per build (a workflow's run number).
#>
param(
    [ValidateSet('x64', 'arm64', 'both')]
    [string]$Arch = 'both',
    [string]$Publisher = 'CN=Photo Finder Development',
    [ValidateRange(0, 65535)]
    [int]$Revision = 0,
    [switch]$SkipHelper
)
$ErrorActionPreference = 'Stop'

$windows = Split-Path $PSScriptRoot -Parent
$version = (Get-Content -LiteralPath (Join-Path $windows 'VERSION') -Raw).Trim()
$artifacts = Join-Path $windows 'artifacts'
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
$arches = if ($Arch -eq 'both') { @('x64', 'arm64') } else { @($Arch) }

foreach ($a in $arches) {
    $out = Join-Path $windows "out\app\$a"
    if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }
    dotnet publish (Join-Path $windows 'src\PhotoFinder\PhotoFinder.csproj') -c Release -r "win-$a" --self-contained true -o $out --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $a." }
    Get-ChildItem -LiteralPath $out -Filter *.pdb | Remove-Item -Force

    $zip = Join-Path $artifacts "PhotoFinder-$version-win-$a.zip"
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
    Write-Host "Packed $zip"

    if (-not $SkipHelper) {
        & (Join-Path $PSScriptRoot 'build-ai-helper.ps1') -Arch $a -Publisher $Publisher -Version $version -Revision $Revision -Pack
    }
}
