#Requires -RunAsAdministrator
<#
.SYNOPSIS
  GroupWeaver lab bootstrap - idempotent, safe to re-run at any time (after the
  DC-promotion reboot, after a full box rebuild). See CLAUDE.md "Bootstrap".
.PARAMETER SkipDcPromotion
  Do everything except the AD DS role install / forest promotion. Promotion
  REBOOTS THE MACHINE when it runs.
#>
[CmdletBinding()]
param([switch]$SkipDcPromotion)

$ErrorActionPreference = 'Stop'
function Log([string]$msg) { Write-Host "[bootstrap $(Get-Date -Format HH:mm:ss)] $msg" }

# --- 1. Chocolatey itself ----------------------------------------------------
if (-not (Get-Command choco -ErrorAction SilentlyContinue)) {
    Log 'Installing Chocolatey...'
    Set-ExecutionPolicy Bypass -Scope Process -Force
    [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol -bor 3072
    Invoke-Expression ((New-Object System.Net.WebClient).DownloadString('https://community.chocolatey.org/install.ps1'))
    $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' +
                [Environment]::GetEnvironmentVariable('Path', 'User')
}
else { Log 'Chocolatey present.' }
$choco = (Get-Command choco).Source

# --- 2. Toolchain packages ---------------------------------------------------
$chocoInstalled = & $choco list --limit-output
function Test-ChocoPkg([string]$pkg) { [bool]($chocoInstalled -match "^$([regex]::Escape($pkg))\|") }

$packages = @(
    @{ Pkg = 'git';              Present = { [bool](Get-Command git -ErrorAction SilentlyContinue) } }
    @{ Pkg = 'gh';               Present = { [bool](Get-Command gh -ErrorAction SilentlyContinue) } }
    @{ Pkg = 'dotnet-8.0-sdk';   Present = { (Get-Command dotnet -ErrorAction SilentlyContinue) -and ((& dotnet --list-sdks) -match '^8\.') } }
    @{ Pkg = 'nodejs-lts';       Present = { [bool](Get-Command node -ErrorAction SilentlyContinue) } }
    @{ Pkg = 'powershell-core';  Present = { [bool](Get-Command pwsh -ErrorAction SilentlyContinue) } }
    # WebView2 Evergreen Runtime - not preinstalled on Server 2022; choco-list guard only
    @{ Pkg = 'webview2-runtime'; Present = { $false } }
    # ffmpeg: assembles the demo-mode evidence GIFs (tools/record-demo-gif.ps1, M2; AP 3.5)
    @{ Pkg = 'ffmpeg';           Present = { [bool](Get-Command ffmpeg -ErrorAction SilentlyContinue) } }
)
foreach ($p in $packages) {
    if ((Test-ChocoPkg $p.Pkg) -or (& $p.Present)) { Log "$($p.Pkg) present."; continue }
    Log "Installing $($p.Pkg)..."
    & $choco install -y $p.Pkg --no-progress
    if ($LASTEXITCODE -notin 0, 3010) { throw "choco install $($p.Pkg) failed (exit $LASTEXITCODE)" }
}

# Refresh this process' PATH so the rest of the session resolves the new tools
# (merge - keep process-scoped entries the caller may have added).
$machineUser = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' +
               [Environment]::GetEnvironmentVariable('Path', 'User')
$env:Path = (($env:Path + ';' + $machineUser) -split ';' | Where-Object { $_ } | Select-Object -Unique) -join ';'

# --- 2b. Git Bash (bash.exe/sh.exe) on the Machine PATH -----------------------
# Git's installer only puts ...\Git\cmd on PATH. Append ...\Git\bin so bash/sh
# resolve everywhere (added 2026-06-12). Deliberately NOT usr\bin: its ~250
# Unix tools risk shadowing Windows' find/sort depending on PATH order.
$gitBin = Join-Path $env:ProgramFiles 'Git\bin'
if (Test-Path (Join-Path $gitBin 'bash.exe')) {
    $machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    if (($machinePath -split ';') -notcontains $gitBin) {
        Log "Adding $gitBin to the Machine PATH..."
        [Environment]::SetEnvironmentVariable('Path', ($machinePath.TrimEnd(';') + ';' + $gitBin), 'Machine')
        $env:Path += ';' + $gitBin
    }
    else { Log 'Git\bin already on the Machine PATH.' }
}
else { Log "WARNING: $gitBin\bash.exe not found - Git layout changed?" }

# --- 3. AD DS role + new forest agdlp.lab (REBOOTS THE MACHINE) ---------------
# DomainRole: 0/2 standalone, 1/3 member, 4 backup DC, 5 primary DC
$role = (Get-CimInstance Win32_ComputerSystem).DomainRole
if ($role -ge 4) {
    Log "Host is already a DC (DomainRole=$role); skipping promotion."
}
elseif ($SkipDcPromotion) {
    Log 'SkipDcPromotion set - NOT promoting. Re-run without the switch to promote.'
}
else {
    Log 'Installing AD DS role and promoting to new forest agdlp.lab. THE BOX WILL REBOOT.'
    # ADDSDeployment is unreliable under PowerShell 7 module compat -> run the
    # whole promotion inside Windows PowerShell 5.1.
    $promo = @'
$ErrorActionPreference = 'Stop'
Install-WindowsFeature AD-Domain-Services -IncludeManagementTools | Out-Null
Import-Module ADDSDeployment
# Throwaway DSRM password, generated at runtime, never stored anywhere
# (CLAUDE.md: never commit secrets; never needed again on this disposable box).
$dsrm = ConvertTo-SecureString ('Dsrm!' + [guid]::NewGuid().ToString('N') + 'aZ9') -AsPlainText -Force
Install-ADDSForest `
    -DomainName 'agdlp.lab' `
    -DomainNetbiosName 'AGDLP' `
    -InstallDns `
    -SafeModeAdministratorPassword $dsrm `
    -Force `
    -WarningAction SilentlyContinue
'@
    # -EncodedCommand: immune to quoting/newline mangling; -NonInteractive: never hang headless
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($promo))
    & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $encoded
    if ($LASTEXITCODE -ne 0) { throw "DC promotion failed (exit $LASTEXITCODE) - see C:\Windows\debug\dcpromo.log" }
}

# --- 4. Graph-bundle test harness deps (npm + Playwright chromium) ------------
# tests/graph-bundle (AP 2.2, ADR-004) verifies the shipped web bundle headlessly;
# chromium lands under %LOCALAPPDATA%\ms-playwright. Both commands are idempotent.
$graphBundleDir = Join-Path $PSScriptRoot '..\tests\graph-bundle'
if (Test-Path (Join-Path $graphBundleDir 'package.json')) {
    Log 'Installing graph-bundle harness deps (npm ci + Playwright chromium)...'
    Push-Location $graphBundleDir
    try {
        npm ci
        if ($LASTEXITCODE -ne 0) { throw "npm ci in tests/graph-bundle failed (exit $LASTEXITCODE)" }
        npx playwright install chromium
        if ($LASTEXITCODE -ne 0) { throw "npx playwright install chromium failed (exit $LASTEXITCODE)" }
    }
    finally { Pop-Location }
}
else { Log 'tests/graph-bundle not present - skipping Playwright harness setup.' }

Log 'Bootstrap finished (if promotion just ran, a reboot is imminent).'
