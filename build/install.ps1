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
    param([string[]] $Arguments, [string] $StandardInput)
    $exe = Join-Path $InstallDir 'WatchTower.exe'
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

Write-Host "Installing WatchTower to $InstallDir" -ForegroundColor Green

# Upgrade in place: stop the running service so its executable can be replaced.
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne 'Stopped') {
    Write-Host "Stopping the existing '$ServiceName' service..."
    Stop-Service -Name $ServiceName -Force
    $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(90))
}

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
if ([IO.Path]::GetFullPath($source).TrimEnd('\') -ne $InstallDir.TrimEnd('\')) {
    Get-ChildItem $source -Exclude 'install.ps1', '*.seed.json' | Copy-Item -Destination $InstallDir -Recurse -Force
}

$installArgs = @('install') + $common
if ($ConfigFile) { $installArgs += @('--config', [IO.Path]::GetFullPath($ConfigFile)) }
Invoke-WatchTower $installArgs

# Console password: use the supplied one, otherwise prompt on a fresh install.
$settings = (& (Join-Path $InstallDir 'WatchTower.exe') config path @common | Select-Object -Last 1)
$hasPassword = (Test-Path $settings) -and ((Get-Content $settings -Raw) -match 'AdminPasswordHash')
if (-not $AdminPassword -and -not $hasPassword -and [Environment]::UserInteractive) {
    Write-Host ''
    Write-Host 'Choose a password for the WatchTower web console (at least 10 characters).'
    $AdminPassword = Read-Host -AsSecureString 'Console password'
}
if ($AdminPassword) {
    $plain = [Runtime.InteropServices.Marshal]::PtrToStringUni(
        [Runtime.InteropServices.Marshal]::SecureStringToGlobalAllocUnicode($AdminPassword))
    try { Invoke-WatchTower (@('set-password', '--password-stdin') + $common) -StandardInput $plain }
    finally { $plain = $null }
}

if (-not $NoStart) {
    Invoke-WatchTower (@('start') + $common)
}

Write-Host ''
Invoke-WatchTower (@('status') + $common)
if ($OpenConsole) { Invoke-WatchTower (@('open') + $common) }

Write-Host ''
Write-Host 'Done. Open the web console from the Start menu ("WatchTower Console") or run:' -ForegroundColor Green
Write-Host "  & '$InstallDir\WatchTower.exe' open"
