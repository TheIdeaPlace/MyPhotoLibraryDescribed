<#
.SYNOPSIS
Builds Photo Finder's Windows installer: an MSIX bundle for x64 and Arm64, the Windows App Runtime it depends on,
and a PhotoFinder.appinstaller file that installs both.

.DESCRIPTION
Opening PhotoFinder.appinstaller shows Windows' App Installer: Photo Finder's name, publisher and icon and an
Install button. It installs the Windows App Runtime first if the PC doesn't have it, then Photo Finder for the
PC's architecture, with a Start menu entry and an uninstall entry in Settings > Apps. Later builds published to
the same place are installed as updates: Windows checks every few hours, and Help > Check for Updates checks now.

For each architecture it lays out the package in windows\out\msix\<arch>:
  App\        Photo Finder, self-contained (no .NET install needed)
  AI\         the Windows AI helper, self-contained, started through its photofinder-ai.exe alias
  Assets\     the logos, with resources.pri at the root so Windows picks the right size
  AppxManifest.xml   from windows\packaging, with the architecture, version and publisher filled in
then packs it, bundles both architectures, and writes everything to -Destination:
  PhotoFinder.appinstaller
  PhotoFinder_<version>.msixbundle
  Dependencies\Microsoft.WindowsAppRuntime.1.8_<arch>.msix

Windows installs only signed packages from a publisher it trusts. -Sign signs with Kelly Ford's Azure Artifact
Signing certificate (scripts\sign-files.ps1), which every PC trusts; unsigned builds are for checking the layout.

PowerShell 5.1 compatible.

.PARAMETER Destination
Where the installer files go. Defaults to windows\artifacts\msix.

.PARAMETER BaseUri
Where the files will be downloaded from, which App Installer also checks for updates: an https address for a
release, or a folder. Defaults to -Destination itself (a file: address), for builds tried from a folder.

.PARAMETER Arch
x64, arm64, or both (the default). The .appinstaller lists the runtime for each architecture built.

.PARAMETER Revision
The version's fourth number. Windows won't install a package over one of the same version with different
contents, and every build differs, so each build needs its own: a workflow passes its run number. Defaults to
the number of ten-minute periods since October 1, 2026, so builds made here count up on their own.

.PARAMETER Sign
Sign the executables, then the bundle, with the kellyford-public certificate.

.EXAMPLE
.\scripts\build-msix.ps1 -Sign -Destination "$env:USERPROFILE\OneDrive\testing\photos\PhotoFinder"
#>
param(
    [string]$Destination,
    [string]$BaseUri,
    [ValidateSet('x64', 'arm64', 'both')]
    [string]$Arch = 'both',
    [ValidateRange(0, 65535)]
    [int]$Revision = [int][Math]::Floor(((Get-Date).ToUniversalTime() - [datetime]'2026-10-01').TotalMinutes / 10),
    [switch]$Sign
)
$ErrorActionPreference = 'Stop'

$windows = Split-Path $PSScriptRoot -Parent
$packaging = Join-Path $windows 'packaging'
if (-not $Destination) { $Destination = Join-Path $windows 'artifacts\msix' }
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
$Destination = (Resolve-Path -LiteralPath $Destination).Path
if (-not $BaseUri) { $BaseUri = ([Uri]$Destination).AbsoluteUri }
$BaseUri = $BaseUri.TrimEnd('/')
$arches = if ($Arch -eq 'both') { @('x64', 'arm64') } else { @($Arch) }

# The signing certificate's subject; an unsigned build uses a stand-in.
$publisher = if ($Sign) { 'CN=kelly ford, O=kelly ford, L=Madison, S=wi, C=US' } else { 'CN=Photo Finder Development' }

$text = (Get-Content -LiteralPath (Join-Path $windows 'VERSION') -Raw).Trim()
if ($text -notmatch '^(\d+)\.(\d+)\.(\d+)$') { throw "windows\VERSION should be three numbers, not '$text'." }
$version = "$text.$Revision"

# makeappx and makepri come with the Windows SDK build tools package, which restoring the projects puts in the
# NuGet cache. This process's own architecture, so they run natively or emulated alike.
$packages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$hostArch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
function Find-Tool([string]$name) {
    $tool = Get-ChildItem (Join-Path $packages 'microsoft.windows.sdk.buildtools') -Recurse -Filter $name -ErrorAction SilentlyContinue |
        Where-Object { $_.Directory.Name -eq $hostArch } | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $tool) { throw "$name isn't in the NuGet cache ($packages); run dotnet restore on PhotoFinder.sln first." }
    $tool.FullName
}
function Invoke-Tool([string]$tool, [string[]]$arguments, [string]$what) {
    # These tools list every file they touch; only worth showing when they fail.
    $output = & $tool @arguments 2>&1
    if ($LASTEXITCODE -ne 0) { $output | Write-Host; throw "$what failed." }
}
$makeappx = Find-Tool 'makeappx.exe'
$makepri = Find-Tool 'makepri.exe'

$runtimeName = 'Microsoft.WindowsAppRuntime.1.8'
$runtimeDir = Get-ChildItem (Join-Path $packages 'microsoft.windowsappsdk.runtime') -Directory -ErrorAction SilentlyContinue |
    Sort-Object { [version]($_.Name -replace '-.*$', '') } -Descending | Select-Object -First 1
if (-not $runtimeDir) { throw "The Windows App SDK runtime package isn't in the NuGet cache; run dotnet restore on PhotoFinder.sln first." }

$work = Join-Path $windows 'out\msix'
if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
$packed = Join-Path $work 'packed'
New-Item -ItemType Directory -Force -Path $packed | Out-Null

# resources.pri: lets Windows choose among the logo sizes (scale-200, the unplated taskbar sizes). Made from the
# Assets folder alone, so it doesn't index every file in the app.
$priRoot = Join-Path $work 'pri'
New-Item -ItemType Directory -Force -Path $priRoot | Out-Null
Copy-Item -LiteralPath (Join-Path $packaging 'Assets') -Destination (Join-Path $priRoot 'Assets') -Recurse
Invoke-Tool $makepri @('createconfig', '/cf', (Join-Path $work 'priconfig.xml'), '/dq', 'en-US', '/o') 'makepri createconfig'
# makepri needs a manifest to name the package's resources.
(Get-Content -LiteralPath (Join-Path $packaging 'AppxManifest.xml') -Raw).Replace('$ARCH$', 'x64').Replace('$VERSION$', $version).
    Replace('$PUBLISHER$', [Security.SecurityElement]::Escape($publisher)) |
    Set-Content -LiteralPath (Join-Path $priRoot 'AppxManifest.xml') -Encoding UTF8
Invoke-Tool $makepri @('new', '/pr', $priRoot, '/cf', (Join-Path $work 'priconfig.xml'), '/mn', (Join-Path $priRoot 'AppxManifest.xml'),
    '/of', (Join-Path $work 'resources.pri'), '/o') 'makepri new'

foreach ($a in $arches) {
    $layout = Join-Path $work $a
    New-Item -ItemType Directory -Force -Path $layout | Out-Null

    dotnet publish (Join-Path $windows 'src\PhotoFinder\PhotoFinder.csproj') -c Release -r "win-$a" --self-contained true `
        -o (Join-Path $layout 'App') --nologo "-p:Version=$text" "-p:FileVersion=$version"
    if ($LASTEXITCODE -ne 0) { throw "Publishing Photo Finder for $a failed." }
    dotnet publish (Join-Path $windows 'src\PhotoFinderAI\PhotoFinderAI.csproj') -c Release -r "win-$a" `
        -o (Join-Path $layout 'AI') --nologo "-p:Version=$text" "-p:FileVersion=$version"
    if ($LASTEXITCODE -ne 0) { throw "Publishing the Windows AI helper for $a failed." }
    # Debug symbols are for development, not every installed copy; the helper's own package files belong to its
    # development registration, not this package.
    # (A filter, not -Include: Windows PowerShell ignores -Include with -LiteralPath and would delete everything.)
    Get-ChildItem -LiteralPath $layout -Recurse -File |
        Where-Object { $_.Extension -eq '.pdb' -or $_.Name -in 'AppxManifest.xml', 'resources.pri' } | Remove-Item -Force

    (Get-Content -LiteralPath (Join-Path $packaging 'AppxManifest.xml') -Raw).
        Replace('$ARCH$', $a).Replace('$VERSION$', $version).Replace('$PUBLISHER$', [Security.SecurityElement]::Escape($publisher)) |
        Set-Content -LiteralPath (Join-Path $layout 'AppxManifest.xml') -Encoding UTF8
    Copy-Item -LiteralPath (Join-Path $packaging 'Assets') -Destination (Join-Path $layout 'Assets') -Recurse
    Copy-Item -LiteralPath (Join-Path $work 'resources.pri') -Destination $layout

    if ($Sign) {
        & (Join-Path $PSScriptRoot 'sign-files.ps1') (Join-Path $layout 'App\PhotoFinder.exe') (Join-Path $layout 'AI\PhotoFinderAI.exe')
    }
    Invoke-Tool $makeappx @('pack', '/d', $layout, '/p', (Join-Path $packed "PhotoFinder_$($version)_$a.msix"), '/o', '/nv') "Packing $a"
    Write-Host "Packed Photo Finder $version for $a"
}

$bundleName = "PhotoFinder_$version.msixbundle"
$bundle = Join-Path $work $bundleName
Invoke-Tool $makeappx @('bundle', '/d', $packed, '/p', $bundle, '/bv', $version, '/o') 'Bundling'
if ($Sign) { & (Join-Path $PSScriptRoot 'sign-files.ps1') $bundle }

# Replace the previous build's files rather than piling them up.
Get-ChildItem -LiteralPath $Destination -Filter 'PhotoFinder_*.msixbundle' -ErrorAction SilentlyContinue | Remove-Item -Force
Copy-Item -LiteralPath $bundle -Destination $Destination
$dependencies = Join-Path $Destination 'Dependencies'
New-Item -ItemType Directory -Force -Path $dependencies | Out-Null

# The runtime packages come from Microsoft's own NuGet package, already signed by Microsoft. Their version is read
# from each package's manifest, as App Installer matches on it.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$dependencyXml = foreach ($a in $arches) {
    $source = Join-Path $runtimeDir.FullName "tools\MSIX\win10-$a\$runtimeName.msix"
    if (-not (Test-Path -LiteralPath $source)) { throw "$source is missing." }
    $file = "$($runtimeName)_$a.msix"
    Copy-Item -LiteralPath $source -Destination (Join-Path $dependencies $file) -Force
    $zip = [IO.Compression.ZipFile]::OpenRead($source)
    try {
        $reader = New-Object IO.StreamReader($zip.GetEntry('AppxManifest.xml').Open())
        [xml]$manifest = $reader.ReadToEnd()
        $reader.Dispose()
    } finally { $zip.Dispose() }
    $identity = $manifest.Package.Identity
    "    <Package Name=`"$($identity.Name)`" Publisher=`"$([Security.SecurityElement]::Escape($identity.Publisher))`" Version=`"$($identity.Version)`" ProcessorArchitecture=`"$a`" Uri=`"$BaseUri/Dependencies/$file`" />"
}

$appInstaller = @"
<?xml version="1.0" encoding="utf-8"?>
<!-- Opening this installs Photo Finder with App Installer: the Windows App Runtime first if needed, then Photo Finder
     for this PC's architecture. Updates are looked for here. -->
<AppInstaller xmlns="http://schemas.microsoft.com/appx/appinstaller/2021" Version="$version" Uri="$BaseUri/PhotoFinder.appinstaller">
  <MainBundle Name="TheIdeaPlace.PhotoFinder" Publisher="$([Security.SecurityElement]::Escape($publisher))" Version="$version" Uri="$BaseUri/$bundleName" />
  <Dependencies>
$($dependencyXml -join "`r`n")
  </Dependencies>
  <!-- Windows checks every few hours and updates Photo Finder while it isn't running; Help > Check for Updates
       checks now. Not OnLaunch: checking at each launch stops the AI helper's photofinder-ai.exe alias from
       starting ("The file cannot be accessed by the system"), found 10/11/2026. -->
  <UpdateSettings>
    <AutomaticBackgroundTask />
  </UpdateSettings>
</AppInstaller>
"@
[IO.File]::WriteAllText((Join-Path $Destination 'PhotoFinder.appinstaller'), $appInstaller, (New-Object Text.UTF8Encoding $false))

Write-Host "Photo Finder $version$(if (-not $Sign) { ' (unsigned)' }): open $(Join-Path $Destination 'PhotoFinder.appinstaller') to install."
