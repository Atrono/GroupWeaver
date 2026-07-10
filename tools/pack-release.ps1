#Requires -Version 7
<#
.SYNOPSIS
    GroupWeaver release packer: publish -> web-bundle integrity -> launch smoke ->
    stage -> zip -> sha256 sidecar -> zip-entry verify (AP 3.5, ADR-012).

.DESCRIPTION
    Produces the public, portable v0.1 download: a self-contained, single-file,
    win-x64 GroupWeaver.App.exe with the loose web/ bundle and examples/rulesets/
    beside it, packed into one top-level folder inside
    GroupWeaver-<version>-<runtime>.zip, plus a .zip.sha256 sidecar.

    Self-contained / single-file settings live on the CLI here (matching the green
    CI recipe), NOT in the csproj, so dotnet build/test on dev boxes is unchanged.
    No trimming, no ReadyToRun, no compression (ADR-012): Avalonia + STJ reflection +
    MVVM source-gen + WebView2 interop + embedded ruleset/demo resources are
    reflection-reached and would break under trimming.

    The mandatory launch smoke (published exe --check and --demo --dump-graph) gates
    IncludeNativeLibrariesForSelfExtract: a self-extract or missing-web/ regression
    fails the pack here, never the end user.

    Read-only by construction: this script never touches Active Directory.

.PARAMETER Version
    Release version, e.g. 0.1.0. Stamps AssemblyInformationalVersion via -p:Version
    and names the zip; the launch smoke asserts the exe reports "GroupWeaver <Version>".

.PARAMETER Runtime
    The publish RID. Default win-x64 (the only supported v0.1 target).

.PARAMETER OutDir
    Output directory for the staged folder, zip, and sidecar. Default artifacts/release.

.PARAMETER Emit
    none (default) - local pack only.
    github - additionally append zip=/sha256=/hash= to $env:GITHUB_OUTPUT and
    generate <OutDir>/RELEASE_NOTES.md (computed hash + verify block + CHANGELOG link).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [ValidateNotNullOrEmpty()][string]$Runtime = 'win-x64',
    [string]$OutDir = 'artifacts/release',
    [ValidateSet('none', 'github')][string]$Emit = 'none'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$webFiles = @('index.html', 'bridge.js', 'graph.js', 'vendor/cytoscape.min.js')
$rulesetFiles = @('README.md', 'default-strict-agdlp.jsonc', 'pure-agdlp.jsonc', 'gg-nesting-forbidden.jsonc')
$rootFiles = @('LICENSE', 'THIRD-PARTY-NOTICES.md', 'README.md')

$publishDir = Join-Path $repoRoot "artifacts/publish/$Runtime"
$releaseDir = if ([System.IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $repoRoot $OutDir }
$stageName = "GroupWeaver-$Version-$Runtime"
$stageDir = Join-Path $releaseDir $stageName
$zipPath = Join-Path $releaseDir "$stageName.zip"
$shaPath = "$zipPath.sha256"
$publishedExe = Join-Path $publishDir 'GroupWeaver.App.exe'

function Invoke-Step {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][scriptblock]$Action
    )
    Write-Host ''
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $Name (exit code $LASTEXITCODE)" -ForegroundColor Red
        exit $LASTEXITCODE
    }
    Write-Host "OK: $Name" -ForegroundColor Green
}

# ---------- 1+2. publish (self-contained single-file, no trim/R2R/compression) ----------
# Web bundle + examples ship LOOSE (ExcludeFromSingleFile / no csproj ref); natives fold
# into the self-extract exe; runtime is bundled. Canonical command per ADR-012 / the spec.
Invoke-Step "dotnet publish ($Runtime, self-contained single-file)" {
    dotnet publish (Join-Path $repoRoot 'src/App/GroupWeaver.App.csproj') `
        -c Release `
        -r $Runtime `
        --self-contained `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:Version=$Version `
        -o $publishDir
}

# The self-contained publish restore rewrites packages.lock.json files (its implicit
# ILLink.Tasks reference lands in the graph; SelfContained is a global property, so
# every ProjectReference is affected). The canonical committed lock shape is the
# plain-restore shape the build gate checks in locked mode - re-normalize so packing
# a release never leaves a dirty working tree.
Invoke-Step 'dotnet restore (re-normalize lock files after publish)' {
    dotnet restore (Join-Path $repoRoot 'GroupWeaver.sln')
}

# ---------- 3. web-bundle integrity gate (lifted verbatim from ci.yml:75-95) ----------
# The bundle must survive single-file publish as LOOSE files that are byte-identical to
# the vendored source; a silent drop/corruption would only surface at app runtime.
Write-Host ''
Write-Host '==> Verify web bundle survives publish (byte-identical to src/App/web)' -ForegroundColor Cyan
$publishedWeb = Join-Path $publishDir 'web'
$sourceWeb = Join-Path $repoRoot 'src/App/web'
foreach ($f in $webFiles) {
    $published = Join-Path $publishedWeb $f
    if (-not (Test-Path $published)) {
        throw "Web bundle file MISSING from publish output: web/$f"
    }
    $publishedHash = (Get-FileHash $published -Algorithm SHA256).Hash
    $sourceHash = (Get-FileHash (Join-Path $sourceWeb $f) -Algorithm SHA256).Hash
    if ($publishedHash -ne $sourceHash) {
        throw "Web bundle file CORRUPTED in publish output: web/$f (hash $publishedHash != source $sourceHash)"
    }
}
Write-Host "OK: web bundle intact - all $($webFiles.Count) files byte-identical to src/App/web." -ForegroundColor Green

# ---------- 3b. README media currency (NON-BLOCKING reminder) ----------
# Heuristic: warn (never fail) if a media-source path has a commit newer than
# docs/media, i.e. the README demo media may be stale. Runs WITHOUT -Strict so it
# exits 0 and can never abort the pack; refresh via the record-demo-media skill.
Write-Host ''
Write-Host '==> Check README media currency (non-blocking reminder)' -ForegroundColor Cyan
& "$PSScriptRoot/check-media-currency.ps1"

# ---------- 4. launch smoke (the self-extract / missing-web/ guard) ----------
# Runs the PUBLISHED exe (not dotnet run): a self-extract failure or dropped web/ bundle
# fails the pack here. The smoke uses --demo on purpose: it must NOT depend on a reachable
# domain controller (bare --check hits the live LdapProvider, which blocks on the bind and
# would hang here AND fail on every DC-less CI runner). A hard timeout turns any future hang
# into a fast, legible failure instead of an indefinite block.
$SmokeTimeoutSec = 120
# ALWAYS redirect BOTH stdout and stderr. Start-Process on this WinExe deadlocks if only one
# stream is redirected (the other handle is left attached to a console that never drains) - the
# real cause of the original hang, not the provider call. Returns @{ ExitCode; StdOut; StdErr }.
function Invoke-PublishedExe {
    param([string[]]$ExeArgs, [int]$TimeoutSec = $SmokeTimeoutSec)
    $outPath = Join-Path ([System.IO.Path]::GetTempPath()) "gw-smoke-out-$([guid]::NewGuid()).txt"
    $errPath = Join-Path ([System.IO.Path]::GetTempPath()) "gw-smoke-err-$([guid]::NewGuid()).txt"
    try {
        $proc = Start-Process -FilePath $publishedExe -ArgumentList $ExeArgs -NoNewWindow -PassThru `
            -RedirectStandardOutput $outPath -RedirectStandardError $errPath
        if (-not $proc.WaitForExit($TimeoutSec * 1000)) {
            try { $proc.Kill($true) } catch { }
            throw "Launch smoke FAILED: '$($ExeArgs -join ' ')' did not exit within ${TimeoutSec}s (killed). " +
                  'The published self-contained exe hung - likely a self-extract failure.'
        }
        return @{
            ExitCode = $proc.ExitCode
            StdOut   = if (Test-Path $outPath) { Get-Content $outPath -Raw } else { '' }
            StdErr   = if (Test-Path $errPath) { Get-Content $errPath -Raw } else { '' }
        }
    }
    finally {
        Remove-Item $outPath, $errPath -ErrorAction SilentlyContinue
    }
}

Write-Host ''
Write-Host '==> Launch smoke: published exe --check --demo' -ForegroundColor Cyan
$check = Invoke-PublishedExe -ExeArgs @('--check', '--demo')
if ($check.ExitCode -ne 0) {
    throw "Launch smoke FAILED: '--check --demo' exited $($check.ExitCode) (expected 0).`nstdout:`n$($check.StdOut)`nstderr:`n$($check.StdErr)"
}
$expectedVersion = "GroupWeaver $Version"
if ($check.StdOut -notmatch [regex]::Escape($expectedVersion)) {
    throw "Launch smoke FAILED: '--check --demo' stdout did not contain '$expectedVersion'.`nstdout:`n$($check.StdOut)"
}
if ($check.StdOut -notmatch 'connected,\s+\d+\s+groups loaded') {
    throw "Launch smoke FAILED: '--check --demo' did not report a demo connection.`nstdout:`n$($check.StdOut)"
}
Write-Host "OK: --check --demo exit 0, reported '$expectedVersion' + a demo connection." -ForegroundColor Green
Write-Host "    stdout: $($check.StdOut.Trim() -replace '\r?\n', ' | ')"

Write-Host ''
Write-Host '==> Launch smoke: published exe --demo --dump-graph' -ForegroundColor Cyan
$dumpPath = Join-Path ([System.IO.Path]::GetTempPath()) "gw-pack-dump-$([guid]::NewGuid()).json"
try {
    $dump = Invoke-PublishedExe -ExeArgs @('--demo', '--dump-graph', $dumpPath)
    if ($dump.ExitCode -ne 0) {
        throw "Launch smoke FAILED: '--demo --dump-graph' exited $($dump.ExitCode) (expected 0).`nstderr:`n$($dump.StdErr)"
    }
    if (-not (Test-Path $dumpPath)) {
        throw "Launch smoke FAILED: '--demo --dump-graph' produced no file at $dumpPath."
    }
    $dumpBytes = (Get-Item $dumpPath).Length
    if ($dumpBytes -le 0) {
        throw "Launch smoke FAILED: dumped graph JSON is empty ($dumpPath)."
    }
    Write-Host "OK: --demo --dump-graph exit 0, wrote $dumpBytes bytes of graph JSON." -ForegroundColor Green
}
finally {
    Remove-Item $dumpPath -ErrorAction SilentlyContinue
}

# ---------- 5. stage the release folder ----------
Write-Host ''
Write-Host '==> Stage release folder' -ForegroundColor Cyan
if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
New-Item -ItemType Directory -Force $stageDir | Out-Null

# exe + loose web/ from the publish output.
Copy-Item $publishedExe (Join-Path $stageDir 'GroupWeaver.App.exe')
Copy-Item $publishedWeb (Join-Path $stageDir 'web') -Recurse

# examples/rulesets/ (no csproj ref - copied explicitly).
$stageExamples = Join-Path $stageDir 'examples/rulesets'
New-Item -ItemType Directory -Force $stageExamples | Out-Null
foreach ($f in $rulesetFiles) {
    Copy-Item (Join-Path $repoRoot "examples/rulesets/$f") (Join-Path $stageExamples $f)
}

# root docs.
foreach ($f in $rootFiles) {
    Copy-Item (Join-Path $repoRoot $f) (Join-Path $stageDir $f)
}
Write-Host "OK: staged $stageName." -ForegroundColor Green

# ---------- 6. zip (one top-level folder; no tarbomb) ----------
Write-Host ''
Write-Host '==> Compress-Archive -> zip' -ForegroundColor Cyan
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
# Compress the FOLDER (not its contents) so the archive carries exactly one top-level dir.
Compress-Archive -Path $stageDir -DestinationPath $zipPath -CompressionLevel Optimal
Write-Host "OK: $zipPath" -ForegroundColor Green

# ---------- 7. sha256 sidecar (two-column "<hash>  <filename>") ----------
Write-Host ''
Write-Host '==> SHA256 sidecar' -ForegroundColor Cyan
$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$zipFileName = Split-Path $zipPath -Leaf
# Two spaces between hash and name: the de-facto sha256sum / Get-FileHash-friendly format.
Set-Content -Path $shaPath -Value "$hash  $zipFileName" -NoNewline -Encoding ascii
Write-Host "OK: $shaPath" -ForegroundColor Green
Write-Host "    $hash  $zipFileName"

# ---------- 8. zip-layout VERIFY (folded-in S3): entries == expected manifest ----------
Write-Host ''
Write-Host '==> Verify zip entries match the expected manifest' -ForegroundColor Cyan
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    # Normalize separators; ignore pure directory entries (names ending in '/').
    $entries = @($archive.Entries |
        Where-Object { $_.FullName -notmatch '/$' } |
        ForEach-Object { $_.FullName -replace '\\', '/' })
    # Exactly one top-level folder - no tarbomb.
    $topLevel = @($entries | ForEach-Object { ($_ -split '/')[0] } | Sort-Object -Unique)
    if ($topLevel.Count -ne 1) {
        throw "Zip layout FAILED: expected exactly one top-level folder, found $($topLevel.Count): $($topLevel -join ', ')"
    }
    if ($topLevel[0] -ne $stageName) {
        throw "Zip layout FAILED: top-level folder is '$($topLevel[0])', expected '$stageName'."
    }

    $expected = @()
    $expected += 'GroupWeaver.App.exe'
    $expected += ($webFiles | ForEach-Object { "web/$_" })
    $expected += ($rulesetFiles | ForEach-Object { "examples/rulesets/$_" })
    $expected += $rootFiles
    $expectedSet = $expected | ForEach-Object { "$stageName/$_" } | Sort-Object
    $actualSet = $entries | Sort-Object

    $missing = $expectedSet | Where-Object { $_ -notin $actualSet }
    if ($missing) {
        throw "Zip layout FAILED: missing expected entries:`n  $($missing -join "`n  ")"
    }
    $unexpected = $actualSet | Where-Object { $_ -notin $expectedSet }
    if ($unexpected) {
        throw "Zip layout FAILED: unexpected entries:`n  $($unexpected -join "`n  ")"
    }
    Write-Host "OK: $($expectedSet.Count) entries match the manifest; one top-level folder '$stageName'." -ForegroundColor Green
}
finally {
    $archive.Dispose()
}

# ---------- 9. -Emit github: GITHUB_OUTPUT + RELEASE_NOTES.md ----------
if ($Emit -eq 'github') {
    Write-Host ''
    Write-Host '==> Emit github outputs + RELEASE_NOTES.md' -ForegroundColor Cyan
    if ($env:GITHUB_OUTPUT) {
        @(
            "zip=$zipPath"
            "sha256=$shaPath"
            "hash=$hash"
        ) | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
    }
    else {
        Write-Host 'NOTE: $env:GITHUB_OUTPUT is unset - skipping step outputs (not on a runner).' -ForegroundColor Yellow
    }

    $notesPath = Join-Path $releaseDir 'RELEASE_NOTES.md'
    $changelogLink = "https://github.com/Atrono/GroupWeaver/blob/main/CHANGELOG.md"
    $notes = @"
GroupWeaver $Version - portable, self-contained ``win-x64`` build.

## Verify your download

This release is not code-signed. Verify integrity and origin yourself - two commands.

**1. Check the SHA256 hash** (must match the value below and the ``$zipFileName.sha256`` sidecar):

``$hash``

``````powershell
Get-FileHash .\$zipFileName -Algorithm SHA256
``````

**2. Verify build provenance** with the [GitHub CLI](https://cli.github.com/) - this
cryptographically confirms the ``.zip`` was built by this repository's release workflow:

``````powershell
gh attestation verify .\$zipFileName --repo Atrono/GroupWeaver
``````

See the [CHANGELOG]($changelogLink) for the full list of changes.
"@
    Set-Content -Path $notesPath -Value $notes -Encoding utf8
    Write-Host "OK: $notesPath" -ForegroundColor Green
}

Write-Host ''
Write-Host "Release pack complete: $zipFileName" -ForegroundColor Green
Write-Host "  zip    : $zipPath"
Write-Host "  sha256 : $shaPath"
Write-Host "  hash   : $hash"
exit 0
