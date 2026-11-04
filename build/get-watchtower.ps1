<#
.SYNOPSIS
    Downloads a WatchTower release, verifies it and runs its installer: a one-line install.

.DESCRIPTION
    1. Resolves the version: -Version, or the latest release from the GitHub releases API.
    2. Downloads WatchTower-<version>-win-x64.zip and SHA256SUMS.txt from that release.
    3. Verifies the zip's SHA-256 against SHA256SUMS.txt and stops, installing nothing, on a
       mismatch.
    4. Extracts it to a new temporary folder and runs the extracted install.ps1 with every other
       parameter you gave (-InstallDir, -DataDir, -ServiceName, -ConfigFile, -EnrollmentCode,
       -AdminPassword, -NoStart, -OpenConsole; see install.ps1).
    5. Deletes the temporary folder, whether the install worked or not.

    Run it from an elevated PowerShell (Windows PowerShell 5.1 or PowerShell 7). It does not
    rely on $PSScriptRoot, so it also runs straight from the web:

        & ([scriptblock]::Create((irm https://github.com/angatiabenson/watch-tower/releases/latest/download/get-watchtower.ps1))) -InstallDir C:\Services\WatchTower

    The checksum proves the zip is the one the release lists. It comes from the same place as
    the zip, so it does not protect against a compromised release; the HTTPS download and, when
    WATCHTOWER_SIGNING_KEY is configured, the package signature checked by the agent's updater do.
    To read this script before running it: download it, read it, then run the file.

.PARAMETER Version
    Release to install, e.g. 1.0.20 (a leading "v" is ignored). Default: the latest release.

.PARAMETER Repository
    GitHub repository (owner/name) to download from. Default: angatiabenson/watch-tower.

.PARAMETER DownloadBaseUrl
    Mirrors and tests: a folder (https:// or http:// URL, or a local or UNC path) that holds
    WatchTower-<version>-win-x64.zip and SHA256SUMS.txt, used instead of the GitHub release.
    Without -Version, the version is read from the zip named in SHA256SUMS.txt.

.EXAMPLE
    # Latest release, default folders; asks for the console password.
    & ([scriptblock]::Create((irm https://github.com/angatiabenson/watch-tower/releases/latest/download/get-watchtower.ps1)))

.EXAMPLE
    # A given version, connected to a portal, unattended.
    .\get-watchtower.ps1 -Version 1.0.20 -EnrollmentCode WT1.eyJ2IjoxLC... -AdminPassword $securePassword
#>

# Deliberately not [CmdletBinding()]: every parameter this script does not declare is collected
# in $args and handed to install.ps1.
param(
    [string] $Version,
    [string] $Repository = 'angatiabenson/watch-tower',
    [string] $DownloadBaseUrl
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# Invoke-WebRequest on Windows PowerShell 5.1 is many times slower with its progress bar.
$ProgressPreference = 'SilentlyContinue'

$runtime = 'win-x64'

function Write-Step([string] $Text) { Write-Host "==> $Text" -ForegroundColor Green }

# The parameters for install.ps1, from this script's unbound arguments: "-Name value",
# "-Name:value" and switches ("-NoStart", or "-NoStart:$false"). Values keep their type, so a
# SecureString stays one.
function ConvertTo-InstallParameters([object[]] $Arguments) {
    $parameters = [ordered]@{}
    $i = 0
    while ($i -lt $Arguments.Count) {
        $token = $Arguments[$i]
        if (-not ($token -is [string] -and $token -match '^-([A-Za-z][A-Za-z0-9]*)(:?)$')) {
            throw "Unexpected argument '$token'. Pass installer options by name, e.g. -InstallDir C:\Services\WatchTower."
        }
        $name = $Matches[1]
        $hasColon = $Matches[2] -eq ':'
        $next = if ($i + 1 -lt $Arguments.Count) { $Arguments[$i + 1] } else { $null }
        $nextIsName = $next -is [string] -and $next -match '^-[A-Za-z][A-Za-z0-9]*:?$'
        if ($hasColon -or ($i + 1 -lt $Arguments.Count -and -not $nextIsName)) {
            $parameters[$name] = $next
            $i += 2
        }
        else {
            $parameters[$name] = $true   # a switch
            $i += 1
        }
    }
    return $parameters
}

function Test-Administrator {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Test-LocalSource([string] $Source) {
    return $Source -match '^(file:|[A-Za-z]:[\\/]|\\\\)' -or -not ($Source -match '^[A-Za-z][A-Za-z0-9+.-]*://')
}

# Downloads (or, for a local folder, copies) $Name from $Source to $Destination.
function Get-ReleaseFile([string] $Source, [string] $Name, [string] $Destination) {
    if (Test-LocalSource $Source) {
        $folder = if ($Source -match '^file:') { ([Uri] $Source).LocalPath } else { $Source }
        $path = Join-Path $folder $Name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "$Name was not found in $folder." }
        Copy-Item -LiteralPath $path -Destination $Destination
        return
    }
    $url = $Source.TrimEnd('/') + '/' + [Uri]::EscapeDataString($Name)
    try {
        Invoke-WebRequest -Uri $url -OutFile $Destination -UseBasicParsing
    }
    catch {
        throw "Downloading $url failed: $($_.Exception.Message)"
    }
}

$installParameters = ConvertTo-InstallParameters $args

if (-not (Test-Administrator)) {
    throw 'Run this from an elevated PowerShell (Run as administrator): installing WatchTower registers a Windows service.'
}

# Windows PowerShell 5.1 (.NET Framework) may still default to TLS 1.0/1.1: set exactly TLS 1.2,
# replacing the session's mask (OR-ing into it would keep the old protocols enabled). TLS 1.3 is
# left out on purpose: .NET Framework can offer it on Windows builds whose SChannel lacks it, which
# breaks the handshake. PowerShell 7 already uses the operating system's TLS 1.2+ defaults, so it is
# left alone. The session's previous setting is restored when the script ends.
$previousProtocols = [Net.ServicePointManager]::SecurityProtocol
if ($PSVersionTable.PSEdition -ne 'Core') {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
}

$work = Join-Path ([IO.Path]::GetTempPath()) ('WatchTower-get-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$policyChanged = $false
$previousPolicy = $null
try {
    $Version = "$Version".Trim().TrimStart('v')
    if ($DownloadBaseUrl) {
        $source = $DownloadBaseUrl
        if ($source -match '^http://') {
            Write-Warning "Downloading over plain HTTP from ${source}: the checksum comes from the same place, so it only detects corruption, not tampering."
        }
    }
    else {
        if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw "Repository '$Repository' is not owner/name." }
        if (-not $Version) {
            Write-Step "Looking up the latest release of $Repository"
            $api = "https://api.github.com/repos/$Repository/releases/latest"
            try {
                $release = Invoke-RestMethod -Uri $api -UseBasicParsing -Headers @{ Accept = 'application/vnd.github+json' }
            }
            catch {
                throw "Could not read the latest release from $api : $($_.Exception.Message)"
            }
            $Version = "$($release.tag_name)".Trim().TrimStart('v')
        }
        $source = "https://github.com/$Repository/releases/download/v$Version"
    }

    $sumsPath = Join-Path $work 'SHA256SUMS.txt'
    Write-Step "Fetching SHA256SUMS.txt from $source"
    Get-ReleaseFile $source 'SHA256SUMS.txt' $sumsPath
    $sums = @{}
    foreach ($line in Get-Content -LiteralPath $sumsPath) {
        if ($line -match '^\s*([0-9A-Fa-f]{64})\s+\*?(\S+)\s*$') { $sums[$Matches[2]] = $Matches[1].ToLowerInvariant() }
    }
    if (-not $Version) {
        $zips = @($sums.Keys | Where-Object { $_ -match "^WatchTower-(.+)-$runtime\.zip$" })
        if ($zips.Count -ne 1) { throw "SHA256SUMS.txt in $source lists $($zips.Count) WatchTower $runtime zips; pass -Version." }
        $Version = $zips[0] -replace "^WatchTower-(.+)-$runtime\.zip$", '$1'
    }
    if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.\-]+)?$') { throw "'$Version' is not a WatchTower version (e.g. 1.0.20)." }

    $zipName = "WatchTower-$Version-$runtime.zip"
    if (-not $sums.ContainsKey($zipName)) { throw "SHA256SUMS.txt does not list $zipName; nothing was installed." }

    $zipPath = Join-Path $work $zipName
    Write-Step "Fetching $zipName"
    Get-ReleaseFile $source $zipName $zipPath

    Write-Step 'Verifying the SHA-256 checksum'
    $actual = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $sums[$zipName]) {
        throw "SHA-256 mismatch for $zipName (expected $($sums[$zipName]), got $actual). The download is damaged or was altered; nothing was installed."
    }
    Write-Host "    $zipName  sha256=$actual  OK"

    $package = Join-Path $work 'package'
    Write-Step "Extracting to $package"
    Expand-Archive -LiteralPath $zipPath -DestinationPath $package
    $installer = Join-Path $package 'install.ps1'
    if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw "$zipName has no install.ps1." }

    # Unknown or misspelt options fail here, before anything is installed. (Read from the
    # script's syntax tree, which needs no execution policy.)
    $ast = [Management.Automation.Language.Parser]::ParseFile($installer, [ref] $null, [ref] $null)
    $known = @($ast.ParamBlock.Parameters | ForEach-Object { $_.Name.VariablePath.UserPath }) +
        @([Management.Automation.PSCmdlet]::CommonParameters)
    $unknown = @($installParameters.Keys | Where-Object { $known -notcontains $_ })
    if ($unknown.Count -gt 0) {
        throw "install.ps1 of $Version has no parameter $(($unknown | ForEach-Object { "-$_" }) -join ', '). " +
            "Its parameters: $(($ast.ParamBlock.Parameters | ForEach-Object { '-' + $_.Name.VariablePath.UserPath }) -join ', ')."
    }

    # The extracted installer is a script file: let it run in this session even under a
    # Restricted or AllSigned policy (process scope only, restored below; it was just verified).
    if ((Get-ExecutionPolicy) -in 'Restricted', 'AllSigned') {
        $previousPolicy = Get-ExecutionPolicy -Scope Process
        Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
        $policyChanged = $true
    }

    $shown = ($installParameters.Keys | ForEach-Object {
            $value = $installParameters[$_]
            if ($value -is [Security.SecureString] -or $_ -match 'Password|Code') { "-$_ ***" }
            elseif ($value -is [bool] -and $value) { "-$_" }
            else { "-$_ $value" }
        }) -join ' '
    Write-Step ("Running install.ps1 $shown".TrimEnd())
    & $installer @installParameters
}
finally {
    [Net.ServicePointManager]::SecurityProtocol = $previousProtocols
    if ($policyChanged) {
        Set-ExecutionPolicy -Scope Process -ExecutionPolicy $previousPolicy -Force
    }
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $work) {
        Write-Warning "Could not delete the temporary folder $work; delete it manually."
    }
}
