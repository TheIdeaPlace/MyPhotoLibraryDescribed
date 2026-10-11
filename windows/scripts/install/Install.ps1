# Installs Photo Finder for the current user: no admin, nothing outside your own profile.
#   App:      %LOCALAPPDATA%\Programs\Photo Finder
#   Shortcut: Start menu, "Photo Finder"
#   Describe Image: registers the Windows AI helper (needs Developer Mode, a Copilot+ PC and the Windows App Runtime 1.8)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\Photo Finder'
$exe = Join-Path $installDir 'PhotoFinder.exe'

# A running copy would hold its files open.
Get-Process PhotoFinder -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | ForEach-Object {
    Write-Host 'Closing the running Photo Finder...'
    $_.CloseMainWindow() | Out-Null
    if (-not $_.WaitForExit(5000)) { $_.Kill() }
}

Write-Host "Installing Photo Finder to $installDir"
if (Test-Path -LiteralPath $installDir) { Remove-Item -LiteralPath (Join-Path $installDir '*') -Recurse -Force -Exclude 'AIHelper' }
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
Copy-Item -Path (Join-Path $here 'App\*') -Destination $installDir -Recurse -Force

$programs = [Environment]::GetFolderPath('Programs')
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut((Join-Path $programs 'Photo Finder.lnk'))
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = $installDir
$shortcut.Description = 'Browse your photos like folders, built for screen readers'
$shortcut.Save()
Write-Host 'Added Photo Finder to the Start menu.'

# Describe Image: the helper is a package, registered from a folder that has to stay put, so it's copied in first.
$devMode = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -ErrorAction SilentlyContinue).AllowDevelopmentWithoutDevLicense -eq 1
$runtime = Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.1.8' -ErrorAction SilentlyContinue
$npu = Get-PnpDevice -Class ComputeAccelerator -PresentOnly -ErrorAction SilentlyContinue
if (-not $npu) {
    Write-Host 'No NPU found, so Describe Image is skipped. Everything else works, Read Text included.'
} elseif (-not $devMode) {
    Write-Host 'Describe Image is skipped: this test build''s AI helper isn''t signed, and Windows only installs it in Developer Mode (Settings, System, For developers).'
} elseif (-not $runtime) {
    Write-Host 'Describe Image is skipped: the Windows App Runtime 1.8 isn''t installed. Get it from https://aka.ms/windowsappsdk/1.8/latest/windowsappruntimeinstall-arm64.exe (or -x64.exe), then run Install again.'
} else {
    # Windows won't register a package already registered from another folder (or an earlier install), so the
    # old registration goes first. Its running copies would hold files open.
    Get-Process PhotoFinderAI -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Get-AppxPackage -Name 'TheIdeaPlace.PhotoFinder.WindowsAI' -ErrorAction SilentlyContinue | ForEach-Object {
        Remove-AppxPackage -Package $_.PackageFullName
    }
    $helperDir = Join-Path $installDir 'AIHelper'
    if (Test-Path -LiteralPath $helperDir) { Remove-Item -LiteralPath $helperDir -Recurse -Force }
    Copy-Item -LiteralPath (Join-Path $here 'AIHelper') -Destination $helperDir -Recurse
    try {
        Add-AppxPackage -Register (Join-Path $helperDir 'AppxManifest.xml') -ForceUpdateFromAnyVersion
        Write-Host 'Registered the Windows AI helper for Describe Image.'
    } catch {
        Write-Host "Describe Image couldn't be set up: $($_.Exception.Message)"
    }
}

Write-Host ''
Write-Host 'Done. Starting Photo Finder.'
Start-Process explorer.exe -ArgumentList "`"$exe`""
