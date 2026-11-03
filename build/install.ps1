<#
.SYNOPSIS
    Installs (or upgrades) WatchTower on this server as a Windows service.

.DESCRIPTION
    Copies WatchTower.exe from this folder to the install folder, registers the service
    (delayed auto-start, automatic restart on failure), imports optional settings, sets the
    web console password, starts the service and prints the console address.

    Re-running it upgrades an existing installation in place; settings, history and logs in
    the data folder are kept.

.EXAMPLE
    # Interactive: you are asked for the console password.
    .\install.ps1

.EXAMPLE
    # Unattended (GPO, Intune, SCCM, Ansible, Invoke-Command), pre-configured for a portal:
    .\install.ps1 -ConfigFile .\watchtower.seed.json -AdminPassword (ConvertTo-SecureString $pw -AsPlainText -Force)

.EXAMPLE
    # Install on several servers from one admin workstation:
    $servers = 'SRV-01','SRV-02'
    $servers | ForEach-Object { Copy-Item .\WatchTower-1.6.0-win-x64 "\\$_\C$\Temp\WatchTower" -Recurse -Force }
    Invoke-Command -ComputerName $servers -ScriptBlock { & C:\Temp\WatchTower\install.ps1 -ConfigFile C:\Temp\WatchTower\seed.json -AdminPassword $using:securePw }
#>
[CmdletBinding()]
param(
    # Where the binaries go.
    [string] $InstallDir = (Join-Path $env:ProgramFiles 'WatchTower'),

    # Where settings, the database and logs go (default: %ProgramData%\WatchTower).
    [string] $DataDir,

    # Windows service name; change it only to run several instances side by side.
    [string] $ServiceName = 'WatchTower',

    # JSON settings to merge into the operator settings (portal URL, agent ID/token, SMTP...).
    [string] $ConfigFile,

    # Web console administrator password. Prompted for when omitted and no password exists yet.
    [SecureString] $AdminPassword,

    # Do not start the service after installing.
    [switch] $NoStart,

    # Open the web console in the default browser when done.
    [switch] $OpenConsole
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-Administrator {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this script from an elevated PowerShell (Run as administrator).'
    }
}

function Invoke-WatchTower {
    param([string[]] $Arguments, [string] $StandardInput, [string] $Exe)
    $exe = if ($Exe) { $Exe } else { Join-Path $InstallDir 'WatchTower.exe' }
    if ($StandardInput) {
        # Pipe through cmd's echo-free path: write the secret to the process's stdin directly.
        $psi = New-Object System.Diagnostics.ProcessStartInfo $exe
        $psi.Arguments = ($Arguments | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '
        $psi.UseShellExecute = $false
        $psi.RedirectStandardInput = $true
        $p = [System.Diagnostics.Process]::Start($psi)
        $p.StandardInput.WriteLine($StandardInput)
        $p.StandardInput.Close()
        $p.WaitForExit()
        $code = $p.ExitCode
    }
    else {
        & $exe @Arguments
        $code = $LASTEXITCODE
    }
    if ($code -ne 0) { throw "WatchTower.exe $($Arguments[0]) failed with exit code $code." }
}

Assert-Administrator

$source = $PSScriptRoot
if (-not (Test-Path (Join-Path $source 'WatchTower.exe'))) {
    throw "WatchTower.exe was not found next to install.ps1 ($source)."
}
$InstallDir = [IO.Path]::GetFullPath($InstallDir)
$common = @('--service-name', $ServiceName)
if ($DataDir) { $common += @('--data-dir', [IO.Path]::GetFullPath($DataDir)) }
$newExe = Join-Path $source 'WatchTower.exe'

Write-Host "Installing WatchTower to $InstallDir" -ForegroundColor Green

# ── 1. Validate everything BEFORE touching the running installation ─────────────────────
if ($ConfigFile) {
    $ConfigFile = [IO.Path]::GetFullPath($ConfigFile)
    # Checked by the NEW version against its own defaults; nothing is written.
    Invoke-WatchTower (@('config', 'validate', $ConfigFile) + $common) -Exe $newExe
}

$settings = (& $newExe config path @common | Select-Object -Last 1)
$hasPassword = (Test-Path $settings) -and ((Get-Content $settings -Raw) -match 'AdminPasswordHash')
if (-not $AdminPassword -and -not $hasPassword -and [Environment]::UserInteractive) {
    Write-Host ''
    Write-Host 'Choose a password for the WatchTower web console (at least 10 characters).'
    $AdminPassword = Read-Host -AsSecureString 'Console password'
}
$plain = $null
if ($AdminPassword) {
    $plain = [Runtime.InteropServices.Marshal]::PtrToStringUni(
        [Runtime.InteropServices.Marshal]::SecureStringToGlobalAllocUnicode($AdminPassword))
    if ($plain.Length -lt 10) { throw 'The console password must be at least 10 characters.' }
}

# ── 2. Stop the existing service and back up its binaries ───────────────────────────────
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
$wasRunning = $existing -and $existing.Status -ne 'Stopped'
$sameFolder = [IO.Path]::GetFullPath($source).TrimEnd('\') -eq $InstallDir.TrimEnd('\')
$backup = $null
if ((Test-Path (Join-Path $InstallDir 'WatchTower.exe')) -and -not $sameFolder) {
    $backup = Join-Path ([IO.Path]::GetTempPath()) ("WatchTower-upgrade-backup-" + (Get-Date -Format 'yyyyMMddHHmmss'))
}

if ($wasRunning) {
    Write-Host "Stopping the existing '$ServiceName' service..."
    Stop-Service -Name $ServiceName -Force
    $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(90))
}

# ── 3. Install; on any failure restore the previous binaries and running state ──────────
try {
    if ($backup) { Copy-Item $InstallDir $backup -Recurse -Force }

    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    if (-not $sameFolder) {
        Get-ChildItem $source -Exclude 'install.ps1', '*.seed.json' | Copy-Item -Destination $InstallDir -Recurse -Force
    }

    $installArgs = @('install') + $common
    if ($ConfigFile) { $installArgs += @('--config', $ConfigFile) }
    Invoke-WatchTower $installArgs

    if ($plain) { Invoke-WatchTower (@('set-password', '--password-stdin') + $common) -StandardInput $plain }

    if (-not $NoStart) { Invoke-WatchTower (@('start') + $common) }
}
catch {
    Write-Warning "Installation failed: $($_.Exception.Message)"
    if ($backup -and (Test-Path $backup)) {
        Write-Warning 'Restoring the previous version...'
        # Mirror the backup back (also removes files the failed version added).
        robocopy $backup $InstallDir /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($wasRunning) { Start-Service -Name $ServiceName -ErrorAction SilentlyContinue }
        Write-Warning 'The previous version was restored.'
    }
    elseif ($wasRunning) {
        Start-Service -Name $ServiceName -ErrorAction SilentlyContinue
    }
    throw
}
finally {
    $plain = $null
    if ($backup -and (Test-Path $backup)) { Remove-Item $backup -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Host ''
Invoke-WatchTower (@('status') + $common)
if ($OpenConsole) { Invoke-WatchTower (@('open') + $common) }

Write-Host ''
Write-Host 'Done. Open the web console from the Start menu ("WatchTower Console") or run:' -ForegroundColor Green
Write-Host "  & '$InstallDir\WatchTower.exe' open"
