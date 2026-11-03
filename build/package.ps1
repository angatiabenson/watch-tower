<#
.SYNOPSIS
    Builds the WatchTower distribution: self-contained zip, SHA-256 checksums and the
    watchtower-update.json manifest consumed by the agent's self-updater.

.DESCRIPTION
    Used by both CI workflows (deploy.yml and release.yml) and runnable locally:

        ./build/package.ps1 -Version 1.6.0 -Repository angatiabenson/watch-tower

    When the environment variable WATCHTOWER_SIGNING_KEY holds an ECDSA P-256 private key
    (PEM), each package is signed; agents configured with the matching public key
    (Updates:PublicKey) only install signed packages.

.OUTPUTS
    <OutputDir>/WatchTower-<Version>-win-x64.zip
    <OutputDir>/watchtower-update.json
    <OutputDir>/SHA256SUMS.txt

    Runs on Windows PowerShell 5.1 and PowerShell 7+. Signing needs PowerShell 7.2+ (PEM
    import); it fails loudly rather than shipping an unsigned package when a key is supplied.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,
    # owner/name on GitHub; used to build the package download URL in the manifest.
    [Parameter(Mandatory)] [string] $Repository,
    [string] $OutputDir = (Join-Path $PSScriptRoot '..\artifacts'),
    [string] $Runtime = 'win-x64',
    [string] $NotesFile,
    # Reuse an existing `dotnet publish` output instead of publishing again.
    [string] $PublishDir,
    [switch] $SkipPublish
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.\-]+)?$') {
    throw "Version '$Version' is not a semantic version (e.g. 1.6.0 or 1.6.0-beta.1)."
}

$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$OutputDir = [IO.Path]::GetFullPath($OutputDir)
$publishDir = if ($PublishDir) { [IO.Path]::GetFullPath($PublishDir) } else { Join-Path $OutputDir "publish-$Runtime" }
$stageDir = Join-Path $OutputDir "stage-$Runtime"

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

if (-not $SkipPublish -and -not $PublishDir) {
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    dotnet publish (Join-Path $root 'src/WatchTower/WatchTower.csproj') `
        --configuration Release --runtime $Runtime --output $publishDir "-p:Version=$Version"
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
}

# Sanity check: the exe must carry exactly the version we are releasing.
$exe = Join-Path $publishDir 'WatchTower.exe'
$productVersion = ((Get-Item $exe).VersionInfo.ProductVersion -split '\+')[0]
if ($productVersion -ne $Version) {
    throw "Published WatchTower.exe reports version '$productVersion', expected '$Version'."
}

# Stage the distributable: binaries + installer script + short readme.
if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stageDir | Out-Null
Copy-Item (Join-Path $publishDir '*') $stageDir -Recurse
Get-ChildItem $stageDir -Include '*.pdb', 'web.config', '*.staticwebassets.*' -Recurse | Remove-Item -Force
Copy-Item (Join-Path $root 'build/install.ps1') $stageDir
Copy-Item (Join-Path $root 'build/INSTALL.txt') $stageDir
Copy-Item (Join-Path $root 'branding/logo-tile.svg') (Join-Path $stageDir 'watchtower.svg')

$zipName = "WatchTower-$Version-$Runtime.zip"
$zipPath = Join-Path $OutputDir $zipName
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $stageDir '*') -DestinationPath $zipPath -CompressionLevel Optimal

$sha256 = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item $zipPath).Length

# Optional signature over "watchtower-update|<version>|<rid>|<sha256>" (must match UpdateSignature.Message).
$signature = $null
if ($env:WATCHTOWER_SIGNING_KEY) {
    if ($PSVersionTable.PSVersion.Major -lt 7) {
        throw 'Signing requires PowerShell 7.2+ (run this script with pwsh).'
    }
    $ecdsa = [System.Security.Cryptography.ECDsa]::Create()
    try {
        $ecdsa.ImportFromPem($env:WATCHTOWER_SIGNING_KEY)
        $message = [Text.Encoding]::UTF8.GetBytes("watchtower-update|$Version|$Runtime|$sha256")
        $bytes = $ecdsa.SignData($message, [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
        $signature = [Convert]::ToBase64String($bytes)
        Write-Host "Package signed."
    }
    finally { $ecdsa.Dispose() }
}
else {
    Write-Host "WATCHTOWER_SIGNING_KEY not set: package is not signed (checksum only)."
}

$notes = if ($NotesFile -and (Test-Path $NotesFile)) { Get-Content $NotesFile -Raw } else { $null }
$manifest = [ordered]@{
    schema      = 1
    version     = $Version
    publishedAt = (Get-Date).ToUniversalTime().ToString('o')
    prerelease  = $Version.Contains('-')
    notes       = $notes
    assets      = @(
        [ordered]@{
            rid       = $Runtime
            url       = "https://github.com/$Repository/releases/download/v$Version/$zipName"
            size      = $size
            sha256    = $sha256
            signature = $signature
        }
    )
}
[IO.File]::WriteAllText((Join-Path $OutputDir 'watchtower-update.json'), ($manifest | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
"$sha256  $zipName" | Set-Content (Join-Path $OutputDir 'SHA256SUMS.txt') -Encoding ascii

Remove-Item $stageDir -Recurse -Force
Write-Host "Packaged $zipName ($([math]::Round($size / 1MB, 1)) MB) sha256=$sha256"
