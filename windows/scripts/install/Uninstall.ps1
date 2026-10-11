# Removes Photo Finder for the current user. Your photos, and the captions and ratings saved in them, aren't touched.
$ErrorActionPreference = 'Stop'
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\Photo Finder'
$exe = Join-Path $installDir 'PhotoFinder.exe'

Get-Process PhotoFinder -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | ForEach-Object {
    $_.CloseMainWindow() | Out-Null
    if (-not $_.WaitForExit(5000)) { $_.Kill() }
}

$helper = Get-AppxPackage -Name 'TheIdeaPlace.PhotoFinder.WindowsAI' -ErrorAction SilentlyContinue
if ($helper) {
    Remove-AppxPackage -Package $helper.PackageFullName
    Write-Host 'Removed the Windows AI helper.'
}

$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Photo Finder.lnk'
if (Test-Path -LiteralPath $shortcut) { Remove-Item -LiteralPath $shortcut -Force }
if (Test-Path -LiteralPath $installDir) { Remove-Item -LiteralPath $installDir -Recurse -Force }
Write-Host 'Photo Finder is uninstalled.'
Write-Host "Its settings and saved details are still in $env:LOCALAPPDATA\PhotoFinder; delete that folder to remove them too."
