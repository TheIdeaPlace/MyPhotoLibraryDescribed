<#
.SYNOPSIS
Builds My Photo Library Described's Windows installer: an MSIX bundle for x64 and Arm64, the Windows App Runtime it depends on,
and a PhotoFinder.appinstaller file that installs both.

.DESCRIPTION
Opening PhotoFinder.appinstaller shows Windows' App Installer: My Photo Library Described's name, publisher and icon and an
Install button. It installs the Windows App Runtime first if the PC doesn't have it, then My Photo Library Described for the
PC's architecture, with a Start menu entry and an uninstall entry in Settings > Apps. Later builds published to
the same place are installed as updates: Windows checks every few hours, and Help > Check for Updates checks now.

For each architecture it lays out the package in windows\out\msix\<arch>:
  App\        My Photo Library Described, self-contained (no .NET install needed)
  AI\         the Windows AI helper, self-contained; My Photo Library Described starts it from here
  Assets\     the logos, with resources.pri at the root so Windows picks the right size
  AppxManifest.xml   from windows\packaging, with the architecture, version and publisher filled in
then packs it, bundles both architectures, and writes everything to -Destination:
  PhotoFinder.appinstaller
  PhotoFinder_<version>.msixbundle
  Dependencies\Microsoft.WindowsAppRuntime.1.8_<arch>.msix

Windows installs only signed packages from a publisher it trusts. -Sign signs with Kelly Ford's Azure Artifact
Signing certificate (scripts\sign-files.ps1), which every PC trusts; unsigned builds are for checking the layout.

The version is windows\VERSION plus a fourth number from the git history: ten times the number of commits, so a
build of main made here and one made by a workflow agree, and each later commit counts higher. A build with
uncommitted changes adds 1 to 9 (which part of the day it is), so it installs over the committed build it started
from. Testers should get builds of main: a branch's extra commits count higher than main has once they're merged.

PowerShell 5.1 compatible.

.PARAMETER Destination
Where the installer files go. Defaults to windows\artifacts\msix.

.PARAMETER BaseUri
Where the files will be downloaded from, which App Installer also checks for updates: an https address for a
release, or a folder. Defaults to -Destination itself. A folder is written into the .appinstaller as that exact
path, so it works on PCs where the same path exists (OneDrive on Kelly's own PCs), not for other people.

.PARAMETER Arch
x64, arm64, or both (the default). The .appinstaller lists the runtime for each architecture built.

.PARAMETER Revision
The version's fourth number, instead of the one from the git history (see above).

.PARAMETER Sign
Sign the executables, then the bundle, with the kellyford-public certificate.

.EXAMPLE
.\scripts\build-msix.ps1 -Sign -Destination "$env:USERPROFILE\OneDrive\testing\photos\PhotoFinder"
#>
param(
    [string]$Destination,
    [string]$BaseUri,
    # The Windows App Runtime packages beside the bundle rather than in Dependencies\: a GitHub release's files can't
    # be in folders.
    [switch]$Flat,
    [ValidateSet('x64', 'arm64', 'both')]
    [string]$Arch = 'both',
    [int]$Revision = -1,
    [switch]$Sign
)
$ErrorActionPreference = 'Stop'

$windows = Split-Path $PSScriptRoot -Parent
$packaging = Join-Path $windows 'packaging'
if (-not $Destination) { $Destination = Join-Path $windows 'artifacts\msix' }
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
$Destination = (Resolve-Path -LiteralPath $Destination).Path
if (-not $BaseUri) { $BaseUri = $Destination }
# A folder (C:\..., \\server\...) becomes a file: address; an address is used as it is.
if ($BaseUri -notmatch '^[a-z][a-z0-9+.-]*://') { $BaseUri = ([Uri](Resolve-Path -LiteralPath $BaseUri).Path).AbsoluteUri }
$BaseUri = $BaseUri.TrimEnd('/')
if ($BaseUri.StartsWith('file:')) {
    Write-Warning "The installer points at $BaseUri. It installs and updates only on PCs where that path exists; give -BaseUri an https address to share it."
}
$arches = if ($Arch -eq 'both') { @('x64', 'arm64') } else { @($Arch) }

# The signing certificate's subject, which the package's Publisher must equal exactly; an unsigned build uses a
# stand-in. sign-files.ps1 checks each signature against it.
$certificateSubject = 'CN=kelly ford, O=kelly ford, L=Madison, S=wi, C=US'
$publisher = if ($Sign) { $certificateSubject } else { 'CN=Photo Finder Development' }

# The display name, from the one place the app keeps it (AppInfo.Name, #34).
$appInfo = Get-Content -LiteralPath (Join-Path $windows 'src\PhotoFinder\AppInfo.cs') -Raw -Encoding UTF8
if ($appInfo -notmatch 'const string Name = "([^"]+)"') { throw "AppInfo.cs has no Name constant." }
$displayName = [Security.SecurityElement]::Escape($Matches[1])

$text = (Get-Content -LiteralPath (Join-Path $windows 'VERSION') -Raw).Trim()
if ($text -notmatch '^(\d+)\.(\d+)\.(\d+)$') { throw "windows\VERSION should be three numbers, not '$text'." }
if ($Revision -lt 0) {
    $ErrorActionPreference = 'Continue'
    $commits = git -C $windows rev-list --count HEAD 2>$null
    $dirty = git -C $windows status --porcelain 2>$null
    $ErrorActionPreference = 'Stop'
    if ($LASTEXITCODE -ne 0 -or -not ($commits -match '^\d+$')) {
        throw "Couldn't count the git history (a shallow checkout?). Fetch it all (fetch-depth: 0) or pass -Revision."
    }
    $Revision = [int]$commits * 10
    if ($dirty) { $Revision += 1 + [int][Math]::Floor((Get-Date).ToUniversalTime().TimeOfDay.TotalMinutes / 160) }
}
if ($Revision -gt 65535) { throw "The version's fourth number would be $Revision; Windows allows up to 65535. Raise windows\VERSION and count again from it." }
$version = "$text.$Revision"

# makeappx and makepri come with the Windows SDK build tools package, which restoring the projects puts in the
# NuGet cache. The newest, for this process's own architecture, so they run natively or emulated alike.
$packages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$hostArch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
function Find-Tool([string]$name) {
    $tool = Get-ChildItem (Join-Path $packages 'microsoft.windows.sdk.buildtools') -Recurse -Filter $name -ErrorAction SilentlyContinue |
        Where-Object { $_.Directory.Name -eq $hostArch -and $_.Directory.Parent.Name -match '^\d+(\.\d+)+$' } |
        Sort-Object { [version]$_.Directory.Parent.Name } -Descending | Select-Object -First 1
    if (-not $tool) { throw "$name isn't in the NuGet cache ($packages); run dotnet restore on PhotoFinder.sln first." }
    $tool.FullName
}
function Invoke-Tool([string]$tool, [string[]]$arguments, [string]$what) {
    # These tools list every file they touch; only worth showing when they fail. Continue, because Windows
    # PowerShell turns any line on stderr into a stopping error, even from a tool that succeeds.
    $ErrorActionPreference = 'Continue'
    $output = & $tool @arguments 2>&1
    $exit = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($exit -ne 0) { $output | Write-Host; throw "$what failed." }
}
$makeappx = Find-Tool 'makeappx.exe'
$makepri = Find-Tool 'makepri.exe'

# The runtime the helper is built against, and that the manifest depends on: the version PhotoFinderAI.csproj
# references, not just the newest in a cache other projects share.
$runtimeName = 'Microsoft.WindowsAppRuntime.1.8'
[xml]$helperProject = Get-Content -LiteralPath (Join-Path $windows 'src\PhotoFinderAI\PhotoFinderAI.csproj') -Raw
$runtimeVersion = @($helperProject.Project.ItemGroup.PackageReference | Where-Object { $_.Include -eq 'Microsoft.WindowsAppSDK.Runtime' })[0].Version
if (-not $runtimeVersion) { throw "PhotoFinderAI.csproj doesn't reference Microsoft.WindowsAppSDK.Runtime." }
$runtimeDir = Join-Path $packages "microsoft.windowsappsdk.runtime\$runtimeVersion"

# Read the runtime packages first: a missing one should stop the build before anything in -Destination changes.
# They're Microsoft's own, already signed; their version is read from each package's manifest, as App Installer
# matches on it.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$runtimes = foreach ($a in $arches) {
    $source = Join-Path $runtimeDir "tools\MSIX\win10-$a\$runtimeName.msix"
    if (-not (Test-Path -LiteralPath $source)) { throw "$source is missing; run dotnet restore on PhotoFinder.sln first." }
    $zip = [IO.Compression.ZipFile]::OpenRead($source)
    try {
        $reader = New-Object IO.StreamReader($zip.GetEntry('AppxManifest.xml').Open())
        [xml]$manifest = $reader.ReadToEnd()
        $reader.Dispose()
    } finally { $zip.Dispose() }
    [pscustomobject]@{ Arch = $a; Source = $source; File = "$($runtimeName)_$a.msix"; Identity = $manifest.Package.Identity }
}

$work = Join-Path $windows 'out\msix'
if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
$packed = Join-Path $work 'packed'
New-Item -ItemType Directory -Force -Path $packed | Out-Null

# resources.pri: lets Windows choose among the logo sizes (scale-200, the unplated taskbar sizes). Made from the
# Assets folder alone, so it doesn't index every file in the app, and as one file: makepri's default config splits
# the scale-200 logos into a resources.scale-200.pri for resource packages, which this package doesn't use.
$priRoot = Join-Path $work 'pri'
New-Item -ItemType Directory -Force -Path $priRoot | Out-Null
Copy-Item -LiteralPath (Join-Path $packaging 'Assets') -Destination (Join-Path $priRoot 'Assets') -Recurse
$priConfig = Join-Path $work 'priconfig.xml'
Invoke-Tool $makepri @('createconfig', '/cf', $priConfig, '/dq', 'en-US', '/o') 'makepri createconfig'
[xml]$config = Get-Content -LiteralPath $priConfig -Raw
foreach ($node in @($config.SelectNodes('//packaging'))) { [void]$node.ParentNode.RemoveChild($node) }
$config.Save($priConfig)
# makepri needs a manifest to name the package's resources.
(Get-Content -LiteralPath (Join-Path $packaging 'AppxManifest.xml') -Raw).Replace('$ARCH$', 'x64').Replace('$VERSION$', $version).
    Replace('$PUBLISHER$', [Security.SecurityElement]::Escape($publisher)).Replace('$DISPLAYNAME$', $displayName) |
    Set-Content -LiteralPath (Join-Path $priRoot 'AppxManifest.xml') -Encoding UTF8
Invoke-Tool $makepri @('new', '/pr', $priRoot, '/cf', $priConfig, '/mn', (Join-Path $priRoot 'AppxManifest.xml'),
    '/of', (Join-Path $work 'resources.pri'), '/o') 'makepri new'

foreach ($a in $arches) {
    $layout = Join-Path $work $a
    New-Item -ItemType Directory -Force -Path $layout | Out-Null

    dotnet publish (Join-Path $windows 'src\PhotoFinder\PhotoFinder.csproj') -c Release -r "win-$a" --self-contained true `
        -o (Join-Path $layout 'App') --nologo "-p:Version=$text" "-p:FileVersion=$version"
    if ($LASTEXITCODE -ne 0) { throw "Publishing My Photo Library Described for $a failed." }
    dotnet publish (Join-Path $windows 'src\PhotoFinderAI\PhotoFinderAI.csproj') -c Release -r "win-$a" `
        -o (Join-Path $layout 'AI') --nologo "-p:Version=$text" "-p:FileVersion=$version"
    if ($LASTEXITCODE -ne 0) { throw "Publishing the Windows AI helper for $a failed." }
    # Debug symbols are for development, not every installed copy; the helper's own package files belong to its
    # development registration, not this package.
    # (A filter, not -Include: Windows PowerShell ignores -Include with -LiteralPath and would delete everything.)
    Get-ChildItem -LiteralPath $layout -Recurse -File |
        Where-Object { $_.Extension -eq '.pdb' -or $_.Name -in 'AppxManifest.xml', 'resources.pri' } | Remove-Item -Force

    (Get-Content -LiteralPath (Join-Path $packaging 'AppxManifest.xml') -Raw).
        Replace('$ARCH$', $a).Replace('$VERSION$', $version).Replace('$PUBLISHER$', [Security.SecurityElement]::Escape($publisher)).Replace('$DISPLAYNAME$', $displayName) |
        Set-Content -LiteralPath (Join-Path $layout 'AppxManifest.xml') -Encoding UTF8
    Copy-Item -LiteralPath (Join-Path $packaging 'Assets') -Destination (Join-Path $layout 'Assets') -Recurse
    Copy-Item -LiteralPath (Join-Path $work 'resources.pri') -Destination $layout

    if ($Sign) {
        & (Join-Path $PSScriptRoot 'sign-files.ps1') -ExpectedSubject $certificateSubject `
            (Join-Path $layout 'App\PhotoFinder.exe') (Join-Path $layout 'AI\PhotoFinderAI.exe')
    }
    # Fully validated, so a manifest mistake fails here rather than when someone installs it.
    Invoke-Tool $makeappx @('pack', '/d', $layout, '/p', (Join-Path $packed "PhotoFinder_$($version)_$a.msix"), '/o') "Packing $a"
    Write-Host "Packed My Photo Library Described $version for $a"
}

$bundleName = "PhotoFinder_$version.msixbundle"
$bundle = Join-Path $work $bundleName
Invoke-Tool $makeappx @('bundle', '/d', $packed, '/p', $bundle, '/bv', $version, '/o') 'Bundling'
if ($Sign) { & (Join-Path $PSScriptRoot 'sign-files.ps1') -ExpectedSubject $certificateSubject $bundle }

$dependencyXml = foreach ($r in $runtimes) {
    "    <Package Name=`"$($r.Identity.Name)`" Publisher=`"$([Security.SecurityElement]::Escape($r.Identity.Publisher))`" Version=`"$($r.Identity.Version)`" ProcessorArchitecture=`"$($r.Arch)`" Uri=`"$BaseUri/$(if ($Flat) { '' } else { 'Dependencies/' })$($r.File)`" />"
}
$appInstaller = @"
<?xml version="1.0" encoding="utf-8"?>
<!-- Opening this installs My Photo Library Described with App Installer: the Windows App Runtime first if needed, then My Photo Library Described
     for this PC's architecture. Updates are looked for here. -->
<AppInstaller xmlns="http://schemas.microsoft.com/appx/appinstaller/2021" Version="$version" Uri="$BaseUri/PhotoFinder.appinstaller">
  <MainBundle Name="TheIdeaPlace.PhotoFinder" Publisher="$([Security.SecurityElement]::Escape($publisher))" Version="$version" Uri="$BaseUri/$bundleName" />
  <Dependencies>
$($dependencyXml -join "`r`n")
  </Dependencies>
  <!-- Windows checks every few hours and updates My Photo Library Described while it isn't running; Help > Check for Updates
       checks now. Not OnLaunch: checking at each launch stopped the AI helper's photofinder-ai.exe alias from
       starting ("The file cannot be accessed by the system"), found 10/11/2026. -->
  <UpdateSettings>
    <AutomaticBackgroundTask />
  </UpdateSettings>
</AppInstaller>
"@
[void][xml]$appInstaller

# Into -Destination in an order that never leaves the .appinstaller pointing at a missing file: the new bundle and
# runtimes first, then the .appinstaller, and the previous build's bundle last.
$dependencies = if ($Flat) { $Destination } else { Join-Path $Destination 'Dependencies' }
New-Item -ItemType Directory -Force -Path $dependencies | Out-Null
foreach ($r in $runtimes) { Copy-Item -LiteralPath $r.Source -Destination (Join-Path $dependencies $r.File) -Force }
Copy-Item -LiteralPath $bundle -Destination $Destination -Force
[IO.File]::WriteAllText((Join-Path $Destination 'PhotoFinder.appinstaller'), $appInstaller, (New-Object Text.UTF8Encoding $false))
Get-ChildItem -LiteralPath $Destination -Filter 'PhotoFinder_*.msixbundle' | Where-Object { $_.Name -ne $bundleName } | Remove-Item -Force

if ($Sign) {
    Write-Host "My Photo Library Described $($version): open $(Join-Path $Destination 'PhotoFinder.appinstaller') to install."
} else {
    Write-Host "My Photo Library Described $version, unsigned, in $Destination. Windows won't install it; build with -Sign for that."
}
