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
    # SDK presence is checked against global.json's pin (rollForward latestPatch:
    # any installed patch of the pinned feature band AT OR ABOVE the pin counts),
    # not just "some 8.x SDK" - a stale patch below the pin triggers a reinstall.
    @{ Pkg = 'dotnet-8.0-sdk';   Present = {
            if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { return $false }
            $pin = [Version](Get-Content (Join-Path $PSScriptRoot '..\global.json') -Raw | ConvertFrom-Json).sdk.version
            [bool]((& dotnet --list-sdks) | ForEach-Object { [Version]($_ -split ' ')[0] } |
                Where-Object { $_.Major -eq $pin.Major -and $_.Minor -eq $pin.Minor -and $_ -ge $pin })
        } }
    @{ Pkg = 'nodejs-lts';       Present = { [bool](Get-Command node -ErrorAction SilentlyContinue) } }
    @{ Pkg = 'powershell-core';  Present = { [bool](Get-Command pwsh -ErrorAction SilentlyContinue) } }
    # WebView2 Evergreen Runtime - not preinstalled on Server 2022; choco-list guard only
    @{ Pkg = 'webview2-runtime'; Present = { $false } }
    # ffmpeg: assembles the demo-mode evidence GIFs (tools/record-demo-gif.ps1, M2; AP 3.5)
    @{ Pkg = 'ffmpeg';           Present = { [bool](Get-Command ffmpeg -ErrorAction SilentlyContinue) } }
    # Display: a glyph-rich console font + Windows Terminal (the only Win terminal
    # with font fallback) so Claude Code's TUI renders cleanly instead of OEM-850
    # '?' boxes - configured in section 2d.
    @{ Pkg = 'nerd-fonts-CascadiaMono';    Present = { [bool]((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts' -ErrorAction SilentlyContinue).PSObject.Properties.Name -match 'CaskaydiaMono NFM') } }
    @{ Pkg = 'microsoft-windows-terminal'; Present = { [bool](Get-Command wt.exe -ErrorAction SilentlyContinue) } }
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

# --- 2c. PowerShell profile (UTF-8 console + interactive niceties) ------------
# Install the versioned profile (tools/powershell-profile.ps1) to PowerShell 7's
# CurrentUserAllHosts profile so pwsh and Claude Code's TUI render Unicode
# cleanly (the box defaults to OEM codepage 850). Target the pwsh 7 path
# explicitly via Documents\PowerShell so this is correct even if bootstrap is
# run under Windows PowerShell 5.1 (whose $PROFILE would point elsewhere).
# Idempotent: only (re)writes on content drift. Repo file is the source of truth.
$profileSource = Join-Path $PSScriptRoot 'powershell-profile.ps1'
$profileTarget = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'PowerShell\profile.ps1'
if (Test-Path -LiteralPath $profileSource) {
    $srcText = Get-Content -Raw -LiteralPath $profileSource
    $dstText = if (Test-Path -LiteralPath $profileTarget) { Get-Content -Raw -LiteralPath $profileTarget } else { $null }
    if ($srcText -ne $dstText) {
        Log "Installing PowerShell profile -> $profileTarget"
        $profileDir = Split-Path -Parent $profileTarget
        if (-not (Test-Path -LiteralPath $profileDir)) { New-Item -ItemType Directory -Path $profileDir -Force | Out-Null }
        Copy-Item -LiteralPath $profileSource -Destination $profileTarget -Force
    }
    else { Log 'PowerShell profile already up to date.' }
}
else { Log "WARNING: $profileSource not found - skipping profile install." }

# --- 2d. conhost console font (Unicode-rich console out of the box) -----------
# Server 2022 ships only Consolas/Lucida; both lack glyphs Claude Code's TUI uses
# (e.g. dingbats), so legacy conhost prints '?' boxes. Make CaskaydiaMono NFM
# (installed in section 2) the selectable + default conhost font. conhost has NO
# font fallback, so the handful of glyphs even Cascadia lacks (e.g. U+273B) still
# need Windows Terminal (also section 2), which falls back to Segoe UI Symbol.
# All idempotent.
$consoleFace = 'CaskaydiaMono NFM'
$ttfKey = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Console\TrueTypeFont'
$ttfVals = (Get-ItemProperty -Path $ttfKey).PSObject.Properties | Where-Object { $_.Name -match '^0+$' }
if (-not ($ttfVals.Value -contains $consoleFace)) {
    $len = ($ttfVals.Name | Measure-Object -Property Length -Maximum).Maximum
    $name = '0' * ([int]$len + 1); if ($name.Length -lt 3) { $name = '000' }
    Log "Registering '$consoleFace' in the console font allow-list ('$name')"
    New-ItemProperty -Path $ttfKey -Name $name -Value $consoleFace -PropertyType String -Force | Out-Null
}
else { Log "'$consoleFace' already in console font allow-list." }
$consKey = 'HKCU:\Console'
if (-not (Test-Path $consKey)) { New-Item -Path $consKey -Force | Out-Null }
if ((Get-ItemProperty -Path $consKey -Name FaceName -ErrorAction SilentlyContinue).FaceName -ne $consoleFace) {
    Log "Setting default conhost font to '$consoleFace' (16px TrueType)"
    New-ItemProperty -Path $consKey -Name FaceName   -Value $consoleFace -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $consKey -Name FontFamily -Value 54          -PropertyType DWord  -Force | Out-Null
    New-ItemProperty -Path $consKey -Name FontWeight -Value 400         -PropertyType DWord  -Force | Out-Null
    New-ItemProperty -Path $consKey -Name FontSize   -Value 0x00100000  -PropertyType DWord  -Force | Out-Null
}
else { Log "Default conhost font already '$consoleFace'." }

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

# --- 4b. WER LocalDumps for GroupWeaver.App.exe (ADR-037 D7) ------------------
# Crash-dump capture for the E2E harness + postmortems: Windows Error Reporting
# writes minidumps (DumpType=1) for the app to %LOCALAPPDATA%\CrashDumps, keeping
# the last 10. Lab-box setup ONLY - the shipped app never writes HKLM (ADR-037 D7);
# this section is idempotent (re-runs overwrite the same values).
$werKey = 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\GroupWeaver.App.exe'
if (-not (Test-Path $werKey)) { New-Item -Path $werKey -Force | Out-Null }
New-ItemProperty -Path $werKey -Name DumpFolder -Value '%LOCALAPPDATA%\CrashDumps' -PropertyType ExpandString -Force | Out-Null
New-ItemProperty -Path $werKey -Name DumpType   -Value 1  -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $werKey -Name DumpCount  -Value 10 -PropertyType DWord -Force | Out-Null
Log 'WER LocalDumps configured for GroupWeaver.App.exe (minidumps -> %LOCALAPPDATA%\CrashDumps).'

# --- 5. Plugins & MCP servers (MANUAL - not scriptable) -----------------------
# Marketplace plugins (code-review, security-guidance, frontend-design, superpowers)
#   are enabled via Claude Code '/plugin' Discover - they live in the user profile,
#   not the repo. claude.ai MCP servers (Microsoft Learn, Context7) connect via
#   claude.ai and are account-scoped. Neither is choco/npm-installable, so a fresh
#   box needs them as manual post-bootstrap steps (CLAUDE.md "Bootstrap" step 7).

Log 'Bootstrap finished (if promotion just ran, a reboot is imminent).'
