<#
.SYNOPSIS
    WCAG 2.2 contrast-ratio report over the PINNED GroupWeaver palette (deterministic),
    with an optional ad-hoc two-region PNG sampling mode.

.DESCRIPTION
    The v0.2 polish pass declares the UI standard as WCAG 2.2 AA (spec
    docs/superpowers/specs/2026-06-21-v0.2-polish-pass-design.md): 1.4.3 text contrast
    (4.5:1 for normal text) and 1.4.11 non-text / graphical-UI contrast (3:1 for node /
    edge / halo distinguishability). The MOST useful form here is deterministic - the
    palette hex values are pinned constants, so this reports the KNOWN token pairs'
    contrast ratios with AA pass/fail, rather than blind image sampling.

    The palette is the single source of truth in the App converters + graph.js; the hex
    values below are cited from those files and MUST NOT drift from them (the C#<->JS
    parity tests pin them). Source files:
      kind     : src/App/Views/AdObjectKindConverters.cs  (User #038387, GG #107C10,
                 DL #A14000, UG #744DA9, OU #0F6CBD, Computer #556070, External #757575)
                 == web/graph.js node background-color rules
      severity : src/App/Views/SeverityConverters.cs      (Error #D13438, Warning #F7A30B,
                 Info #4FA3E3)
      diff     : src/App/Views/GapKindConverters.cs        (Added #2FAE4E, Removed #E0503A,
                 Unchecked #8A8F98)
      page bg  : src/App/web/graph.js                      (#1b1f27)

    The WCAG relative-luminance formula is implemented per the spec: per-channel sRGB
    linearization (c/12.92 if c<=0.03928 else ((c+0.055)/1.055)^2.4), luminance
    L = 0.2126 R + 0.7152 G + 0.0722 B, ratio = (Lmax + 0.05) / (Lmin + 0.05).

    Thresholds: 4.5:1 for normal text (1.4.3); 3:1 for non-text / graphical UI (1.4.11).

    Read-only / analysis only.

.PARAMETER SamplePng
    Ad-hoc mode: a PNG to sample two pixel regions from instead of (well, in addition to)
    the palette table. Requires -RegionA and -RegionB.

.PARAMETER RegionA
    "x,y,w,h" of the first region to average (foreground), e.g. "100,100,20,20".

.PARAMETER RegionB
    "x,y,w,h" of the second region to average (background).

.PARAMETER Gate
    Gate mode: after printing the standard pinned-palette report, exit non-zero if ANY
    pair fails its applicable AA floor (4.5:1 for text-context pairs, 3:1 for
    graphic-context pairs), except pairs on the commented $gateExpectedFail allowlist
    (reported as EXPECTED FAIL; an allowlisted pair that PASSES fails the gate as a
    stale entry - the list may only shrink). Without the switch the script is
    report-only, as before.

.EXAMPLE
    pwsh tools/check-contrast.ps1
    pwsh tools/check-contrast.ps1 -Gate
    pwsh tools/check-contrast.ps1 -SamplePng artifacts/ui/x.png -RegionA "10,10,8,8" -RegionB "200,200,8,8"
#>
[CmdletBinding()]
param(
    [string]$SamplePng,
    [string]$RegionA,
    [string]$RegionB,
    [switch]$Gate
)

$ErrorActionPreference = 'Stop'

# --- WCAG relative-luminance + contrast ratio ---------------------------------
function ConvertFrom-Hex([string]$hex) {
    $h = $hex.TrimStart('#')
    return [pscustomobject]@{
        R = [Convert]::ToInt32($h.Substring(0, 2), 16)
        G = [Convert]::ToInt32($h.Substring(2, 2), 16)
        B = [Convert]::ToInt32($h.Substring(4, 2), 16)
    }
}

function Get-RelativeLuminance([int]$r, [int]$g, [int]$b) {
    # sRGB linearization per WCAG 2.x.
    $lin = {
        param([double]$c)
        $cs = $c / 255.0
        if ($cs -le 0.03928) { return $cs / 12.92 }
        return [Math]::Pow((($cs + 0.055) / 1.055), 2.4)
    }
    $rl = & $lin $r
    $gl = & $lin $g
    $bl = & $lin $b
    return (0.2126 * $rl) + (0.7152 * $gl) + (0.0722 * $bl)
}

function Get-ContrastRatio($fg, $bg) {
    $lf = Get-RelativeLuminance $fg.R $fg.G $fg.B
    $lb = Get-RelativeLuminance $bg.R $bg.G $bg.B
    $lmax = [Math]::Max($lf, $lb)
    $lmin = [Math]::Min($lf, $lb)
    return ($lmax + 0.05) / ($lmin + 0.05)
}

$TEXT_AA = 4.5     # WCAG 1.4.3 normal text
$NONTEXT_AA = 3.0  # WCAG 1.4.11 non-text / graphical UI

# --- the pinned palette (cited above; do not let it diverge from source) ------
$pageBg = '#1b1f27'   # src/App/web/graph.js

# (name, hex, context) - context picks which AA threshold is the meaningful gate:
#   'text'    => label/glyph rendered as text on the page bg (4.5:1 is the bar)
#   'graphic' => node fill / halo / cue distinguishable as a graphical object (3:1 bar)
$palette = @(
    # kind palette (graph node fills - graphical UI; AdObjectKindConverters.cs / graph.js)
    @{ Name = 'User #038387'; Hex = '#038387'; Context = 'graphic' }
    @{ Name = 'GlobalGroup #107C10'; Hex = '#107C10'; Context = 'graphic' }
    @{ Name = 'DomainLocal #A14000'; Hex = '#A14000'; Context = 'graphic' }
    @{ Name = 'Universal #744DA9'; Hex = '#744DA9'; Context = 'graphic' }
    @{ Name = 'OrgUnit #0F6CBD'; Hex = '#0F6CBD'; Context = 'graphic' }
    @{ Name = 'Computer #556070'; Hex = '#556070'; Context = 'graphic' }
    @{ Name = 'External #757575'; Hex = '#757575'; Context = 'graphic' }
    # severity (sidebar glyph squares + graph overlay halos; SeverityConverters.cs)
    @{ Name = 'Error #D13438'; Hex = '#D13438'; Context = 'graphic' }
    @{ Name = 'Warning #F7A30B'; Hex = '#F7A30B'; Context = 'graphic' }
    @{ Name = 'Info #4FA3E3'; Hex = '#4FA3E3'; Context = 'graphic' }
    # diff cues (gap glyph squares + graph diff overlay; GapKindConverters.cs)
    @{ Name = 'DiffAdded #2FAE4E'; Hex = '#2FAE4E'; Context = 'graphic' }
    @{ Name = 'DiffRemoved #E0503A'; Hex = '#E0503A'; Context = 'graphic' }
    @{ Name = 'DiffUnchecked #8A8F98'; Hex = '#8A8F98'; Context = 'graphic' }
    # WCAG 1.4.11 node-lift ring (#90 / ADR-021): the DL/UG/Computer fills above are
    # sub-3:1, so a 2px #8A93A3 ring (BrandTokens.NodeLiftRing == graph.js border-color)
    # carries their graphical-object distinguishability against the page bg instead.
    @{ Name = 'NodeLiftRing #8A93A3'; Hex = '#8A93A3'; Context = 'graphic' }
)

# Label/text contexts: the same palette colors that ALSO carry the redundant text
# letter/symbol (severity E/W/i) are evaluated at the stricter 4.5:1 bar where they
# render as colored text on the page bg (the naming-preview chip). The on-BADGE glyph
# text (E/W/i drawn on the colored fill) uses the #90 / ADR-021 per-hue ink
# (SeverityConverters.ToTextBrush): white on Error, dark #1b1f27 ink on Warning/Info -
# fixing the old white-on-amber 2.06 / white-on-blue 2.73 fails (both now >= 4.5).
$onLightInk = '#1b1f27'   # BrandTokens.OnLightText
$textPairs = @(
    @{ Name = 'Error glyph "E" on bg'; Fg = '#D13438'; Bg = $pageBg }
    @{ Name = 'Warning glyph "W" on bg'; Fg = '#F7A30B'; Bg = $pageBg }
    @{ Name = 'Info glyph "i" on bg'; Fg = '#4FA3E3'; Bg = $pageBg }
    @{ Name = 'White ink on Error badge'; Fg = '#FFFFFF'; Bg = '#D13438' }
    @{ Name = 'Dark ink on Warning badge'; Fg = $onLightInk; Bg = '#F7A30B' }
    @{ Name = 'Dark ink on Info badge'; Fg = $onLightInk; Bg = '#4FA3E3' }
    # ADR-035 D4: the light-theme "No match" text (#gw-status/#find-no-match ink) on the
    # composited near-white light #controls surface. Retoned from #BD7C00 (~3.44:1, FAIL)
    # to #8A5A00 so it clears the WCAG 1.4.3 4.5:1 text floor. == CHROME.light['--gw-no-match'].
    @{ Name = 'Light No-match on controls bg'; Fg = '#8A5A00'; Bg = '#F5F6F8' }
    # ADR-036 D2: the destructive-tier ink (Button.destructive label + 1px hairline;
    # Tokens.axaml DestructiveTextBrush, dark #FF8A8E / light #A4262C) on its three surfaces
    # per theme. Card = the translucent card tint composited over the page (dark #14FFFFFF
    # over #1b1f27 -> #2D3138; light #0A000000 over #ECEEF1 -> #E3E5E8); wash = the HOVER
    # state, the DestructiveSoft red tint composited over that card (dark #29D13438 over
    # #2D3138 -> #473138; light #1FD13438 over #E3E5E8 -> #E1CFD3). Every row must clear
    # the 4.5:1 text floor; the border is the same opaque ink, so 3:1 non-text follows.
    @{ Name = 'Dark destructive on page'; Fg = '#FF8A8E'; Bg = '#1b1f27' }
    @{ Name = 'Dark destructive on card'; Fg = '#FF8A8E'; Bg = '#2D3138' }
    @{ Name = 'Dark destructive on wash'; Fg = '#FF8A8E'; Bg = '#473138' }
    @{ Name = 'Light destructive on page'; Fg = '#A4262C'; Bg = '#ECEEF1' }
    @{ Name = 'Light destructive on card'; Fg = '#A4262C'; Bg = '#E3E5E8' }
    @{ Name = 'Light destructive on wash'; Fg = '#A4262C'; Bg = '#E1CFD3' }
    # #268 findings audit-1/audit-2: AuditView.axaml nested a translucent CardBackgroundBrush Border
    # (the "Open" status pill, the triage/unchecked caveats, the run-history honesty banner) INSIDE an
    # already-CardBackgroundBrush parent card - a DOUBLE composite. src/App/Styles/Tokens.axaml pins the
    # card tint as translucent white-over-page in dark (#14FFFFFF over #1b1f27) and translucent
    # black-over-page in light (#0A000000 over #ECEEF1); compositing that SAME tint a second time over
    # the once-composited card surface (SecondaryForegroundBrush's own background) darkens light's
    # surface further -> #DADCDF, where the SecondaryForegroundBrush ink (#5A636E) reads only 4.44:1 -
    # the exact FAIL the fit-audit's evidence cites (~4.43:1, rounding). Dark's double composite (white
    # over dark -> #3D4148) still clears 4.5:1 at 4.98:1, but with near-zero headroom - both rows below
    # are the REGRESSION PROOF: the fix (opaque PageBackgroundBrush, a SINGLE composite - see the
    # already-fixed run-history drift-tile comment a few lines up, and AuditView.axaml itself) restores
    # comfortable headroom in BOTH themes. A revert to CardBackgroundBrush on any of the four Borders
    # reproduces the FAIL row exactly; tests/GroupWeaver.App.Tests/Views/AuditOpaqueCaveatSurfaceViewTests.cs
    # is the load-bearing regression guard (it reads the actual bound resource per-Border), these rows
    # are the WCAG-math documentation the view test's threshold is drawn from.
    @{ Name = 'Dark ink on double-card composite (bug)'; Fg = '#B0B5BD'; Bg = '#3D4148' }
    @{ Name = 'Dark ink on page (fix)'; Fg = '#B0B5BD'; Bg = $pageBg }
    @{ Name = 'Light ink on double-card composite (bug, FAILS)'; Fg = '#5A636E'; Bg = '#DADCDF' }
    @{ Name = 'Light ink on page (fix)'; Fg = '#5A636E'; Bg = '#ECEEF1' }
)

function Format-Pass([double]$ratio, [double]$threshold) {
    if ($ratio -ge $threshold) { return 'PASS' } else { return 'FAIL' }
}

# Locale-independent ratio string (this lab DC is German-localized -> ',' decimal).
$script:inv = [System.Globalization.CultureInfo]::InvariantCulture
function Format-Ratio([double]$ratio) { return $ratio.ToString('N2', $script:inv) + ':1' }

Write-Host ''
Write-Host "WCAG 2.2 contrast report - palette vs page background $pageBg"
Write-Host "  thresholds: text(1.4.3)=${TEXT_AA}:1   non-text(1.4.11)=${NONTEXT_AA}:1"
Write-Host ''
$fmt = '{0,-22} {1,7}  {2,-9} {3,-9}'
Write-Host ($fmt -f 'pair', 'ratio', 'AA-text', 'AA-nontext')
Write-Host ('-' * 52)

$bg = ConvertFrom-Hex $pageBg
$anyNonTextFail = $false

# Gate-mode bookkeeping: each pair is judged against ITS floor only (the Context
# field picks the meaningful WCAG criterion) - 4.5:1 for text, 3:1 for graphic.
#
# Expected-fail allowlist for -Gate. The RATCHET: this list may only SHRINK. An
# allowlisted pair that starts PASSING (or matches no pinned pair) is a hard gate
# failure until its entry is removed, and nothing may be added here just to turn
# the gate green - every entry carries a WHY.
$gateExpectedFail = @(
    # Sub-3:1 kind fills accepted by design: the 2px NodeLiftRing #8A93A3 ring
    # carries WCAG 1.4.11 graphical-object distinguishability for these nodes
    # instead (ADR-021, #90; see the palette comment above the ring entry).
    'DomainLocal #A14000'
    'Universal #744DA9'
    'Computer #556070'
    # Deliberate regression-proof documentation row of the FIXED #268 bug state;
    # fails by construction and is never a rendered surface (see textPairs note).
    'Light ink on double-card composite (bug, FAILS)'
    # KNOWN GAP: naming-preview Error caption text, tracked as issue #315.
    # Remove this entry in the #315 fix PR.
    'Error glyph "E" on bg'
)
$gateFailures = [System.Collections.Generic.List[string]]::new()
$gateExpected = [System.Collections.Generic.List[string]]::new()
$gateStale = [System.Collections.Generic.List[string]]::new()
$gateAllowlistSeen = [System.Collections.Generic.List[string]]::new()

function Add-GateResult([string]$name, [double]$ratio, [string]$ratioStr, [double]$floor, [string]$floorLabel) {
    $allowlisted = $gateExpectedFail -contains $name
    if ($allowlisted) { $gateAllowlistSeen.Add($name) }
    if ($ratio -lt $floor) {
        if ($allowlisted) { $gateExpected.Add("$name = $ratioStr (floor $floorLabel)") }
        else { $gateFailures.Add("$name = $ratioStr (floor $floorLabel)") }
    }
    elseif ($allowlisted) {
        $gateStale.Add("$name = $ratioStr now clears floor $floorLabel - remove the stale allowlist entry")
    }
}

foreach ($p in $palette) {
    $fg = ConvertFrom-Hex $p.Hex
    $ratio = Get-ContrastRatio $fg $bg
    $rStr = Format-Ratio $ratio
    $textPass = Format-Pass $ratio $TEXT_AA
    $nonTextPass = Format-Pass $ratio $NONTEXT_AA
    if ($nonTextPass -eq 'FAIL') { $anyNonTextFail = $true }
    if ($p.Context -eq 'text') { Add-GateResult $p.Name $ratio $rStr $TEXT_AA '4.5:1 text' }
    else { Add-GateResult $p.Name $ratio $rStr $NONTEXT_AA '3:1 non-text' }
    Write-Host ($fmt -f $p.Name, $rStr, $textPass, $nonTextPass)
}

Write-Host ''
Write-Host 'Text-context pairs (label glyphs / badge text):'
Write-Host ($fmt -f 'pair', 'ratio', 'AA-text', 'AA-nontext')
Write-Host ('-' * 52)
foreach ($p in $textPairs) {
    $fg = ConvertFrom-Hex $p.Fg
    $bgp = ConvertFrom-Hex $p.Bg
    $ratio = Get-ContrastRatio $fg $bgp
    $rStr = Format-Ratio $ratio
    Add-GateResult $p.Name $ratio $rStr $TEXT_AA '4.5:1 text'
    Write-Host ($fmt -f $p.Name, $rStr, (Format-Pass $ratio $TEXT_AA), (Format-Pass $ratio $NONTEXT_AA))
}

# --- optional ad-hoc PNG region sampling --------------------------------------
if ($SamplePng) {
    if (-not $RegionA -or -not $RegionB) { throw '-SamplePng requires both -RegionA and -RegionB ("x,y,w,h")' }
    Add-Type -AssemblyName System.Drawing

    function Get-RegionAverage([System.Drawing.Bitmap]$bmp, [string]$region) {
        $parts = $region.Split(',') | ForEach-Object { [int]$_.Trim() }
        $x = $parts[0]; $y = $parts[1]; $w = $parts[2]; $h = $parts[3]
        [long]$sr = 0; [long]$sg = 0; [long]$sb = 0; [int]$n = 0
        for ($yy = $y; $yy -lt ($y + $h); $yy++) {
            for ($xx = $x; $xx -lt ($x + $w); $xx++) {
                $c = $bmp.GetPixel($xx, $yy)
                $sr += $c.R; $sg += $c.G; $sb += $c.B; $n++
            }
        }
        return [pscustomobject]@{ R = [int]($sr / $n); G = [int]($sg / $n); B = [int]($sb / $n) }
    }

    $resolved = if ([System.IO.Path]::IsPathRooted($SamplePng)) { $SamplePng }
    else { Join-Path (Get-Location) $SamplePng }
    $bmp = New-Object System.Drawing.Bitmap($resolved)
    try {
        $a = Get-RegionAverage $bmp $RegionA
        $b = Get-RegionAverage $bmp $RegionB
        $ratio = Get-ContrastRatio $a $b
        Write-Host ''
        Write-Host "Ad-hoc PNG sample ($SamplePng):"
        Write-Host ("  RegionA avg = #{0:X2}{1:X2}{2:X2}   RegionB avg = #{3:X2}{4:X2}{5:X2}" -f $a.R, $a.G, $a.B, $b.R, $b.G, $b.B)
        Write-Host ("  contrast    = {0}   AA-text {1}   AA-nontext {2}" -f `
            (Format-Ratio $ratio), (Format-Pass $ratio $TEXT_AA), (Format-Pass $ratio $NONTEXT_AA))
    }
    finally { $bmp.Dispose() }
}

# --- opt-in gate mode ----------------------------------------------------------
if ($Gate) {
    Write-Host ''
    foreach ($e in $gateExpected) { Write-Host "EXPECTED FAIL (allowlisted): $e" }
    # Ratchet: allowlist entries that match no pinned pair are stale too (typo,
    # or the pair itself was removed) - they must not linger as dead exemptions.
    foreach ($u in $gateExpectedFail) {
        if ($gateAllowlistSeen -notcontains $u) {
            $gateStale.Add("$u matches no pinned pair - remove the stale allowlist entry")
        }
    }
    if ($gateFailures.Count -gt 0 -or $gateStale.Count -gt 0) {
        if ($gateFailures.Count -gt 0) {
            Write-Host ("GATE FAIL: {0} pair(s) below their AA floor:" -f $gateFailures.Count)
            foreach ($f in $gateFailures) { Write-Host "  $f" }
        }
        if ($gateStale.Count -gt 0) {
            Write-Host ("GATE FAIL: {0} stale allowlist entry(ies):" -f $gateStale.Count)
            foreach ($s in $gateStale) { Write-Host "  $s" }
        }
        Write-Host ''
        exit 1
    }
    Write-Host ("GATE PASS: no pair below its AA floor beyond the {0} allowlisted expected-fail(s)." -f $gateExpected.Count)
}

Write-Host ''
