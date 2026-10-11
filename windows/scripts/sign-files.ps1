<#
.SYNOPSIS
Signs files with Kelly Ford's Azure Artifact Signing certificate, on this PC.

.DESCRIPTION
The same signing as the other Windows apps' release workflows: signtool with Microsoft's Artifact Signing add-on,
the kellyford-public certificate profile, SHA-256, and a timestamp so the signature stays valid after the
short-lived certificate expires. Each file is checked afterwards; the script fails unless every one is validly
signed and timestamped.

Needs, once:
  winget install Microsoft.Azure.TrustedSigningClientTools
  az login   (as an account with the Artifact Signing Certificate Profile Signer role)

See The-Idea-Place-Projects/signing/windows.md. PowerShell 5.1 compatible.

.PARAMETER ExpectedSubject
The subject the signing certificate must have, which a package's Publisher has to equal for Windows to install it.
Each signed executable is checked against it, so a changed certificate fails the build instead of the install.

.EXAMPLE
.\scripts\sign-files.ps1 -ExpectedSubject 'CN=kelly ford, O=kelly ford, L=Madison, S=wi, C=US' out\msix\x64\App\PhotoFinder.exe
#>
[CmdletBinding()]
param(
    [string]$ExpectedSubject,
    [Parameter(Mandatory, ValueFromRemainingArguments)]
    [string[]]$Path
)
$ErrorActionPreference = 'Stop'

# @() keeps one file a list: a single path would otherwise splat into one argument per character.
$files = @(foreach ($p in $Path) { (Resolve-Path -LiteralPath $p).Path })

# The add-on is x64, and signtool has to match it, so the x64 signtool is used on Arm PCs too. The newest one: an
# older signtool (10.0.22621) can't sign an MSIX bundle ("file format cannot be signed"). Restoring the projects puts
# a current one in the NuGet cache.
$dlib = Join-Path $env:LOCALAPPDATA 'Microsoft\MicrosoftTrustedSigningClientTools\Azure.CodeSigning.Dlib.dll'
if (-not (Test-Path -LiteralPath $dlib)) {
    throw "The Artifact Signing add-on isn't installed. Run: winget install Microsoft.Azure.TrustedSigningClientTools"
}
$packages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin", (Join-Path $packages 'microsoft.windows.sdk.buildtools') `
        -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.Directory.Name -eq 'x64' -and $_.Directory.Parent.Name -match '^\d+(\.\d+)+$' } |
    Sort-Object { [version]$_.Directory.Parent.Name } -Descending |
    Select-Object -First 1
if (-not $signtool) { throw "signtool.exe (x64) wasn't found. Run dotnet restore on PhotoFinder.sln, which puts one in the NuGet cache." }

# UTF-8 without a byte order mark: the add-on can't read the file with one.
$metadata = Join-Path ([IO.Path]::GetTempPath()) "signing-metadata-$([guid]::NewGuid().ToString('N')).json"
$json = [ordered]@{
    Endpoint               = 'https://eus.codesigning.azure.net/'
    CodeSigningAccountName = 'kellylford'
    CertificateProfileName = 'kellyford-public'
} | ConvertTo-Json
[IO.File]::WriteAllText($metadata, $json, (New-Object Text.UTF8Encoding $false))

try {
    Write-Host "Signing $($files.Count) file(s)."
    # signtool writes progress to stderr; with ErrorActionPreference Stop that would end the script before its
    # exit code could be read.
    $ErrorActionPreference = 'Continue'
    $log = & $signtool.FullName sign /v /fd SHA256 /tr http://timestamp.acs.microsoft.com /td SHA256 /dlib $dlib /dmdf $metadata @files 2>&1
    $signExit = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($signExit -ne 0) { $log | Write-Host; throw "signtool failed (exit code $signExit). If it mentions credentials, run az login." }
} finally {
    Remove-Item -LiteralPath $metadata -Force -ErrorAction SilentlyContinue
}

$bad = 0
foreach ($f in $files) {
    # Get-AuthenticodeSignature can't read an MSIX's signature; signtool verify can, against the PC's trusted roots.
    if ($f -match '\.(msix|msixbundle|appx|appxbundle)$') {
        $ErrorActionPreference = 'Continue'
        & $signtool.FullName verify /pa /q $f 2>&1 | Out-Null
        $ok = $LASTEXITCODE -eq 0
        $ErrorActionPreference = 'Stop'
        Write-Host "$f`n  verified: $ok"
    } else {
        $sig = Get-AuthenticodeSignature -LiteralPath $f
        $ok = $sig.Status -eq 'Valid' -and [bool]$sig.TimeStamperCertificate
        Write-Host "$f`n  status: $($sig.Status); signer: $($sig.SignerCertificate.Subject); timestamped: $([bool]$sig.TimeStamperCertificate)"
        if ($ExpectedSubject -and $sig.SignerCertificate.Subject -ne $ExpectedSubject) {
            Write-Host "  expected signer: $ExpectedSubject"
            $ok = $false
        }
    }
    if (-not $ok) { $bad++ }
}
if ($bad) { throw "$bad file(s) aren't validly signed and timestamped." }
