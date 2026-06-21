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

.EXAMPLE
    pwsh tools/check-contrast.ps1
    pwsh tools/check-contrast.ps1 -SamplePng artifacts/ui/x.png -RegionA "10,10,8,8" -RegionB "200,200,8,8"
#>
[CmdletBinding()]
param(
    [string]$SamplePng,
    [string]$RegionA,
    [string]$RegionB
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
)

# Label/text contexts: the same palette colors that ALSO carry the redundant text
# letter/symbol (severity E/W/i, diff +/-/?) are evaluated at the stricter 4.5:1 bar,
# since they render as text on the page bg in the sidebar rows. White text on the
# colored badge is a separate, always-padded surface - included for completeness.
$textPairs = @(
    @{ Name = 'Error glyph "E" on bg'; Fg = '#D13438'; Bg = $pageBg }
    @{ Name = 'Warning glyph "W" on bg'; Fg = '#F7A30B'; Bg = $pageBg }
    @{ Name = 'Info glyph "i" on bg'; Fg = '#4FA3E3'; Bg = $pageBg }
    @{ Name = 'White text on Error badge'; Fg = '#FFFFFF'; Bg = '#D13438' }
    @{ Name = 'White text on Warning badge'; Fg = '#FFFFFF'; Bg = '#F7A30B' }
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
foreach ($p in $palette) {
    $fg = ConvertFrom-Hex $p.Hex
    $ratio = Get-ContrastRatio $fg $bg
    $rStr = Format-Ratio $ratio
    $textPass = Format-Pass $ratio $TEXT_AA
    $nonTextPass = Format-Pass $ratio $NONTEXT_AA
    if ($nonTextPass -eq 'FAIL') { $anyNonTextFail = $true }
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

Write-Host ''
