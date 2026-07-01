using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace GroupWeaver.App.Views;

/// <summary>
/// THE single declared source of truth for every GroupWeaver palette hex (ADR-021 / #90).
/// Each role exposes a <c>const string XxxHex</c> (for a string consumer) AND a matching
/// <see cref="ImmutableSolidColorBrush"/> static (for a brush consumer), so a hex literal
/// lives in exactly ONE place app-side: the converters (<see cref="AdObjectKindConverters"/>,
/// <see cref="SeverityConverters"/>, <see cref="CellChoiceConverters"/>,
/// <see cref="GapKindConverters"/>, <see cref="NamingPreviewConverter"/>) all reference these
/// tokens rather than re-parsing their own <c>Color.Parse("#…")</c>.
///
/// <para><b>Parity is BY HAND, review-enforced (ADR-021 Consequences).</b> The graph runs as a
/// file:// bundle with no runtime share, so these are NOT the only copies — <c>src/App/web/graph.js</c>
/// (node fill / overlay / underlay / line rules), <c>src/App/web/index.html</c> (the legend
/// swatches), <c>tests/graph-bundle/verify.mjs</c> (the PALETTE / SEVERITY / DIFF tripwire
/// blocks), and the C# <c>WebBundleTests</c> palette/border assertions are HAND-COPIED MIRRORS.
/// Any change to a value here must move every mirror in lock-step; the reviewer confirms it
/// (there is no compile-time guarantee).</para>
///
/// <para><b>App-chrome light theme landed (ADR-026 D1, WP1a).</b> The page-relative chrome roles
/// now carry a LIGHT value beside the dark one (<see cref="PageBackgroundLightHex"/>,
/// <see cref="CardBackgroundLightHex"/>, <see cref="CardBorderLightHex"/>,
/// <see cref="SecondaryForegroundLightHex"/>, <see cref="GhostHoverLightHex"/>), resolved by
/// <c>ThemeVariant</c> in <c>Styles/Tokens.axaml</c>'s <c>ThemeDictionaries</c>. The kind/severity/
/// diff FILLS stay theme-INVARIANT (all dark enough to read on a light canvas too — ADR-026 D3/D5);
/// the GRAPH canvas itself stays dark in WP1a (WP1b re-tones it). The dark values are byte-identical
/// to the shipped palette so every ADR-021 WCAG ratio holds.</para>
///
/// <para>WCAG ratios cited below are computed against <see cref="PageBackgroundHex"/> (#1b1f27).</para>
/// </summary>
public static class BrandTokens
{
    // --- Kind fills (ADR-004 palette; values UNCHANGED, FILLS are never re-toned — the 1.4.11
    // fix is the border-lift on DL/UG/Computer in graph.js, never a fill change). White badge
    // text reads >= 4.5:1 on every one of these fills. -----------------------------------------

    /// <summary>User node/badge fill — teal #038387.</summary>
    public const string UserHex = "#038387";

    /// <summary>GlobalGroup node/badge fill — green #107C10.</summary>
    public const string GlobalGroupHex = "#107C10";

    /// <summary>DomainLocalGroup node/badge fill — rust #A14000. (Graph fill 1.4.11 = 2.55:1;
    /// lifted by the 2px #8A93A3 ring in graph.js, the fill itself stays #A14000.)</summary>
    public const string DomainLocalGroupHex = "#A14000";

    /// <summary>UniversalGroup node/badge fill — purple #744DA9. (Graph fill 1.4.11 = 2.66:1;
    /// lifted by the 2px #8A93A3 ring in graph.js, the fill itself stays #744DA9.)</summary>
    public const string UniversalGroupHex = "#744DA9";

    /// <summary>OrganizationalUnit node/badge fill — blue #0F6CBD.</summary>
    public const string OrganizationalUnitHex = "#0F6CBD";

    /// <summary>Computer node/badge fill — slate #556070. (Graph fill 1.4.11 = 2.59:1;
    /// lifted by the 2px #8A93A3 ring in graph.js, the fill itself stays #556070.)</summary>
    public const string ComputerHex = "#556070";

    /// <summary>External node/badge fill — gray #757575.</summary>
    public const string ExternalHex = "#757575";

    /// <summary><see cref="UserHex"/> as a brush.</summary>
    public static readonly ImmutableSolidColorBrush User = new(Color.Parse(UserHex));

    /// <summary><see cref="GlobalGroupHex"/> as a brush.</summary>
    public static readonly ImmutableSolidColorBrush GlobalGroup = new(Color.Parse(GlobalGroupHex));

    /// <summary><see cref="DomainLocalGroupHex"/> as a brush.</summary>
    public static readonly ImmutableSolidColorBrush DomainLocalGroup = new(Color.Parse(DomainLocalGroupHex));

    /// <summary><see cref="UniversalGroupHex"/> as a brush.</summary>
    public static readonly ImmutableSolidColorBrush UniversalGroup = new(Color.Parse(UniversalGroupHex));

    /// <summary><see cref="OrganizationalUnitHex"/> as a brush.</summary>
    public static readonly ImmutableSolidColorBrush OrganizationalUnit = new(Color.Parse(OrganizationalUnitHex));

    /// <summary><see cref="ComputerHex"/> as a brush.</summary>
    public static readonly ImmutableSolidColorBrush Computer = new(Color.Parse(ComputerHex));

    /// <summary><see cref="ExternalHex"/> as a brush.</summary>
    public static readonly ImmutableSolidColorBrush External = new(Color.Parse(ExternalHex));

    // --- Severity (ADR-010 overlay palette; values UNCHANGED). White-on-fill: Error 4.93:1
    // (PASS 1.4.3), Warning 2.06:1 and Info 2.73:1 (FAIL) — the #90 fix routes Warning/Info
    // badge TEXT through OnLightText (dark ink), never the fill. -------------------------------

    /// <summary>Error severity fill — red #D13438 (white text 4.93:1 ✓).</summary>
    public const string ErrorHex = "#D13438";

    /// <summary>Warning severity fill — amber #F7A30B (needs dark ink: white 2.06:1 ✗,
    /// <see cref="OnLightTextHex"/> 8.02:1 ✓).</summary>
    public const string WarningHex = "#F7A30B";

    /// <summary>Info severity fill — light blue #4FA3E3 (needs dark ink: white 2.73:1 ✗,
    /// <see cref="OnLightTextHex"/> 6.04:1 ✓).</summary>
    public const string InfoHex = "#4FA3E3";

    /// <summary><see cref="ErrorHex"/> as a brush.</summary>
    public static readonly ImmutableSolidColorBrush Error = new(Color.Parse(ErrorHex));

    /// <summary><see cref="WarningHex"/> as a brush.</summary>
    public static readonly ImmutableSolidColorBrush Warning = new(Color.Parse(WarningHex));

    /// <summary><see cref="InfoHex"/> as a brush.</summary>
    public static readonly ImmutableSolidColorBrush Info = new(Color.Parse(InfoHex));

    // --- Diff (ADR-015 palette; values UNCHANGED). -------------------------------------------

    /// <summary>Diff Added — green #2FAE4E (the plan introduces it).</summary>
    public const string AddedHex = "#2FAE4E";

    /// <summary>Diff Removed — red-orange #E0503A (the plan drops it).</summary>
    public const string RemovedHex = "#E0503A";

    /// <summary>Diff Unchecked — gray #8A8F98 (the Ist side was never expanded).</summary>
    public const string UncheckedHex = "#8A8F98";

    /// <summary><see cref="AddedHex"/> as a brush.</summary>
    public static readonly ImmutableSolidColorBrush Added = new(Color.Parse(AddedHex));

    /// <summary><see cref="RemovedHex"/> as a brush.</summary>
    public static readonly ImmutableSolidColorBrush Removed = new(Color.Parse(RemovedHex));

    /// <summary><see cref="UncheckedHex"/> as a brush.</summary>
    public static readonly ImmutableSolidColorBrush Unchecked = new(Color.Parse(UncheckedHex));

    // --- Cell choice / naming (ADR-011 matrix chips + ADR-009 naming preview; values UNCHANGED) -

    /// <summary>Nesting-matrix Allow chip — green #107C10 (the GG group-green, kept inside the
    /// app's existing color vocabulary).</summary>
    public const string AllowHex = "#107C10";

    /// <summary>Nesting-matrix Deny chip — neutral gray #757575.</summary>
    public const string DenyHex = "#757575";

    /// <summary>Naming-preview Ok chip — green #2EA043 (green-dominant, distinct from every
    /// severity color: Ok is not a finding).</summary>
    public const string NamingOkHex = "#2EA043";

    /// <summary>Naming-preview PatternInvalid chip — amber #B58900 (not green-dominant, so it
    /// can never masquerade as the Ok success color).</summary>
    public const string NamingPatternInvalidHex = "#B58900";

    /// <summary><see cref="AllowHex"/> as a brush.</summary>
    public static readonly ImmutableSolidColorBrush Allow = new(Color.Parse(AllowHex));

    /// <summary><see cref="DenyHex"/> as a brush.</summary>
    public static readonly ImmutableSolidColorBrush Deny = new(Color.Parse(DenyHex));

    /// <summary><see cref="NamingOkHex"/> as a brush.</summary>
    public static readonly ImmutableSolidColorBrush NamingOk = new(Color.Parse(NamingOkHex));

    /// <summary><see cref="NamingPatternInvalidHex"/> as a brush.</summary>
    public static readonly ImmutableSolidColorBrush NamingPatternInvalid = new(Color.Parse(NamingPatternInvalidHex));

    // --- Role tokens (the future-light-theme seam, ADR-021 D4). These name page-RELATIVE roles
    // a light theme would re-bind, not concrete kind/severity/diff colors. ---------------------

    /// <summary>The graph + app page background — #1b1f27. Every WCAG ratio in this file is
    /// computed against this color.</summary>
    public const string PageBackgroundHex = "#1b1f27";

    /// <summary>The LIGHT app-chrome page background — #ECEEF1 (ADR-026 D3, Frame 4). The graph
    /// CANVAS stays dark in WP1a (WP1b re-tones it); this is the Avalonia window/page surface only.</summary>
    public const string PageBackgroundLightHex = "#ECEEF1";

    /// <summary>LIGHT card surface tint — #0A000000 (ADR-026 D3, Frame 4): translucent BLACK over
    /// the light page, mirroring the dark theme's translucent-white-over-dark card language.</summary>
    public const string CardBackgroundLightHex = "#0A000000";

    /// <summary>LIGHT card / separator border — #1A000000 (ADR-026 D3, Frame 4).</summary>
    public const string CardBorderLightHex = "#1A000000";

    /// <summary>LIGHT secondary-neutral chrome foreground — #5A636E (ADR-026 D3, Frame 4): the
    /// light-theme counterpart of the dark theme's #B0B5BD chrome glyph ink.</summary>
    public const string SecondaryForegroundLightHex = "#5A636E";

    /// <summary>LIGHT ghost-button hover wash — #0D000000 (ADR-026 D3, Frame 4): translucent black,
    /// the light counterpart of the dark theme's #14FFFFFF pointer-over wash.</summary>
    public const string GhostHoverLightHex = "#0D000000";

    /// <summary>White text used ON a dark fill (kind badges, Error/Added/Removed/Deny/Allow
    /// chips) — #FFFFFF.</summary>
    public const string OnDarkTextHex = "#FFFFFF";

    /// <summary>Dark ink (= the page-background color) used ON a LIGHT fill — the #90 fix for the
    /// Warning (amber, 8.02:1 ✓) and Info (light-blue, 6.04:1 ✓) badges whose white text failed
    /// 1.4.3. Value #1b1f27.</summary>
    public const string OnLightTextHex = "#1b1f27";

    /// <summary>Pure-black ink (#000000) for the MID-TONE fills the standard <see cref="OnLightTextHex"/>
    /// (#1b1f27) cannot cover at 4.5:1 — the #106 diff-badge fix. The diff Removed fill #E0503A reaches
    /// only 4.23:1 against #1b1f27 (FAILS 1.4.3); black clears all three diff fills (Added 7.28:1,
    /// Removed 5.38:1, Unchecked 6.47:1). Routed through <see cref="GapKindConverters.ToTextBrush"/>.</summary>
    public const string OnLightTextStrongHex = "#000000";

    /// <summary>The 1.4.11 node border-lift ring — #8A93A3 (5.33:1 vs the page bg). The 2px ring
    /// graph.js paints on the DL/UG/Computer fills whose graphical-object contrast fails 3:1; the
    /// fills themselves stay unchanged.</summary>
    public const string NodeLiftRingHex = "#8A93A3";

    /// <summary>DARK validation-error MESSAGE ink — light red #FF8A8E (WP6a). The settings
    /// validation bands tint their background with the error red at low alpha (#22D13438), which
    /// composites over the dark page #1b1f27 to a deep muted red #332229; the raw <see cref="ErrorHex"/>
    /// #D13438 reads only 3.04:1 on it (1.4.3 FAIL — the message is readable text). A LIGHTER red
    /// keeps the error semantic and clears the floor: #FF8A8E reads 6.62:1 on #332229. (ADR-021
    /// per-context-ink pattern, like <see cref="OnLightTextHex"/>/<see cref="DemoBadgeTextHex"/>.)</summary>
    public const string ValidationErrorTextHex = "#FF8A8E";

    /// <summary>LIGHT validation-error MESSAGE ink — deep red #A4262C (WP6a). The error-tinted band
    /// #22D13438 composites over the light page #ECEEF1 to a pale pink #E8D5D8; the raw #D13438 reads
    /// only 3.51:1 on it (1.4.3 FAIL). A DEEPER red reads: #A4262C reads 5.17:1 on #E8D5D8 — still
    /// unmistakably the error red. See <see cref="ValidationErrorTextHex"/>.</summary>
    public const string ValidationErrorTextLightHex = "#A4262C";

    /// <summary>DARK preview-caution ink — the WP6b "vs default · +n criticals" delta when the edited
    /// ruleset produces MORE findings than the default. The severity amber <see cref="WarningHex"/>
    /// #F7A30B on the dark preview card (~#2C2F37) reads ~6.5:1 — clears 1.4.3; the signed delta text
    /// is the redundant non-color channel. Value = #F7A30B (the same amber, kept distinct from the
    /// light variant which must deepen).</summary>
    public const string PreviewCautionTextHex = "#F7A30B";

    /// <summary>LIGHT preview-caution ink — deepened amber #7A4F00 (WP6b). The light preview card
    /// surface renders ~#E3E5E8; the amber #F7A30B is far below the floor there and #8A5A00 lands at
    /// only 4.65:1 (at the edge), so #7A4F00 reads ~5.4:1 for headroom — still unmistakably a caution
    /// amber. See <see cref="PreviewCautionTextHex"/>.</summary>
    public const string PreviewCautionTextLightHex = "#7A4F00";

    // --- Accent (ADR-026 D6, WP2: the redesign's brand purple). Theme-aware, like the WP1a chrome
    // roles. TWO distinct purples per variant, by use: a DECORATIVE accent (DEMO badge text,
    // selection/focus rings) — the lighter Frame-1/Frame-4 hue — and an ACCESSIBLE primary-button
    // FILL whose WHITE text clears 4.5:1 (the lighter decorative hues FAIL: dark #8B7BFF white-text
    // = 3.29:1, light #6A5CFF = 4.58:1 only at rest with no hover headroom). The fill ramp goes
    // hover/pressed DARKER (richer), never lighter — auto-lightening below 4.5:1 is the trap the
    // explicit ramp avoids. The DEMO badge sits on an accent-SOFT pill; the soft + line tints are
    // the Frame translucent-accent values. ----------------------------------------------------

    /// <summary>DARK decorative accent — brand purple #8B7BFF (Frame 1 <c>--accent</c>). NON-text
    /// decorative use only (selection/focus rings, the "GW" mark): white text on this fill is only
    /// 3.29:1 (FAILS 1.4.3), so primary buttons use <see cref="AccentFillHex"/>. Note: the DEMO
    /// badge TEXT does NOT use this — it uses the per-theme <see cref="DemoBadgeTextHex"/> deepened
    /// to clear 4.5:1 on the soft pill (ADR-021 ToTextBrush precedent).</summary>
    public const string AccentHex = "#8B7BFF";

    /// <summary>LIGHT decorative accent — brand purple #6A5CFF (Frame 4 <c>--accent</c>). NON-text
    /// decorative use only; white text 4.58:1 at rest with no hover headroom, so the primary FILL is
    /// the deeper <see cref="AccentFillLightHex"/>.</summary>
    public const string AccentLightHex = "#6A5CFF";

    /// <summary>DARK accent-soft pill background — #298B7BFF (Frame 1 <c>rgba(139,123,255,.16)</c>;
    /// 0.16×255 = 40.8 ⇒ <c>0x29</c>): the DEMO badge's translucent-accent fill, mirrored as #AARRGGBB.</summary>
    public const string AccentSoftHex = "#298B7BFF";

    /// <summary>LIGHT accent-soft pill background — #1F6A5CFF (Frame 4 <c>rgba(106,92,255,.12)</c>).</summary>
    public const string AccentSoftLightHex = "#1F6A5CFF";

    /// <summary>DARK DEMO-badge ink — #A99BFF (Frame 1 <c>--accent-2</c> light value). The badge text
    /// is readable text (1.4.3 applies — a "decorative" label is not exempt; ADR-021 standard), so it
    /// is DEEPENED/LIGHTENED off the raw accent to clear 4.5:1 on the dark soft pill: #A99BFF reads
    /// 5.51:1 on the blended dark accent-soft pill (#2D2E4A). The soft-pill BACKGROUND is unchanged.</summary>
    public const string DemoBadgeTextHex = "#A99BFF";

    /// <summary>LIGHT DEMO-badge ink — #4A3CC8 (= the light primary-fill hover purple). Deepened so
    /// the badge text clears 4.5:1 on the light soft pill: 5.62:1 on the blended light accent-soft
    /// pill (#DCDCF3) and 6.52:1 on the raw page #ECEEF1. The raw accent #6A5CFF read only 3.40:1.</summary>
    public const string DemoBadgeTextLightHex = "#4A3CC8";

    /// <summary>DARK accent-line — #808B7BFF (Frame 1 <c>rgba(139,123,255,.5)</c>): a 50%-accent
    /// hairline (decorative pill border / focus ring), mirrored as #AARRGGBB.</summary>
    public const string AccentLineHex = "#808B7BFF";

    /// <summary>LIGHT accent-line — #806A5CFF (Frame 4 <c>rgba(106,92,255,.5)</c>).</summary>
    public const string AccentLineLightHex = "#806A5CFF";

    /// <summary>DARK accessible primary-button FILL — #6B5BDF (Frame 1 <c>--accent-2</c> gradient
    /// end). WHITE text clears 4.5:1 across the whole interactive ramp: rest 5.02:1, hover
    /// <see cref="AccentFillHoverHex"/> 5.97:1, pressed <see cref="AccentFillPressedHex"/> 6.39:1.
    /// Replaces FluentTheme's default blue accent on the Connect/Save/primary buttons.</summary>
    public const string AccentFillHex = "#6B5BDF";

    /// <summary>DARK primary-fill pointer-over — #5F4FD0 (white text 5.97:1). DARKER than rest by
    /// design, keeping every state ≥ 4.5:1 (auto-lightened hover would fall to ~4.1:1).</summary>
    public const string AccentFillHoverHex = "#5F4FD0";

    /// <summary>DARK primary-fill pressed — #5B4BC8 (white text 6.39:1).</summary>
    public const string AccentFillPressedHex = "#5B4BC8";

    /// <summary>LIGHT accessible primary-button FILL — #5547E6 (Frame 4 <c>--accent-2</c>). WHITE
    /// text: rest 6.12:1, hover <see cref="AccentFillHoverLightHex"/> 7.58:1, pressed
    /// <see cref="AccentFillPressedLightHex"/> 8.20:1 — all clear 4.5:1.</summary>
    public const string AccentFillLightHex = "#5547E6";

    /// <summary>LIGHT primary-fill pointer-over — #4A3CC8 (white text 7.58:1).</summary>
    public const string AccentFillHoverLightHex = "#4A3CC8";

    /// <summary>LIGHT primary-fill pressed — #4537C0 (white text 8.20:1).</summary>
    public const string AccentFillPressedLightHex = "#4537C0";

    // --- Read-only lock pill (ADR-026 D6, WP2): the always-on "Read-only" pill. The redesign uses
    // its severity/ok GREEN ink on a green-soft pill; the GREEN here is the Frame green (distinct
    // from the kind/severity palette — this is chrome, not a finding). Decorative pill text. -----

    /// <summary>DARK read-only-pill ink — green #46C98A (Frame 1 <c>--green</c>). Reads 5.93:1 on the
    /// blended dark green-soft pill (#213836) — clears 1.4.3.</summary>
    public const string ReadOnlyPillTextHex = "#46C98A";

    /// <summary>LIGHT read-only-pill ink — deep forest green #0B6B36. The Frame-4 <c>--green</c>
    /// #1F9D57 reads only 2.65:1 on the blended light green-soft pill (1.4.3 FAIL — the label is
    /// readable text, not exempt; ADR-021 ToTextBrush precedent), so the LIGHT ink is deepened:
    /// #0B6B36 reads 5.03:1 on the blended light pill (#D3E4DF) and 5.70:1 on the raw page #ECEEF1.
    /// The soft-pill BACKGROUND keeps the Frame green-soft tint.</summary>
    public const string ReadOnlyPillTextLightHex = "#0B6B36";

    /// <summary>DARK read-only-pill background — #2646C98A (Frame 1 <c>rgba(70,201,138,.15)</c>):
    /// the green-soft pill fill, mirrored as #AARRGGBB.</summary>
    public const string ReadOnlyPillBackgroundHex = "#2646C98A";

    /// <summary>LIGHT read-only-pill background — #1F1F9D57 (Frame 4 <c>rgba(31,157,87,.12)</c>).</summary>
    public const string ReadOnlyPillBackgroundLightHex = "#1F1F9D57";

    // --- Graph-canvas LIGHT theme (ADR-026 WP1b). These are the DOCUMENTED C# MIRROR of the light
    // values graph.js owns in its THEME.light / CHROME.light tables (the wire carries only the
    // variant string — no token values cross the bridge). They are NOT consumed by any Avalonia
    // converter (the canvas runs in the file:// WebView); they exist so the source of truth for the
    // light-canvas hues lives beside the dark palette, and the reviewer enforces lock-step parity
    // with graph.js / index.html / verify.mjs (the LIGHT block) / WebBundleTests, exactly like the
    // dark palette. WCAG ratios below are computed vs the LIGHT canvas #F5F6F8 (ADR-026 D5 table).
    // Kind FILLS stay theme-INVARIANT (all >= 4.2:1 on the light canvas), so there is NO light kind
    // palette — only the page-relative roles re-tone. ------------------------------------------

    /// <summary>LIGHT graph canvas / body background — #F5F6F8 (Frame 4). The DARK canvas is
    /// <see cref="PageBackgroundHex"/> #1b1f27; this is the WP1b canvas re-tone (distinct from the
    /// app-chrome <see cref="PageBackgroundLightHex"/> #ECEEF1 surface, WP1a).</summary>
    public const string GraphCanvasLightHex = "#F5F6F8";

    /// <summary>LIGHT node label ink — #1C2127 (14.98:1 on the light canvas, >= 4.5:1 text); the
    /// dark counterpart is #E8ECF2. Outline flips to the canvas color #F5F6F8.</summary>
    public const string GraphLabelInkLightHex = "#1C2127";

    /// <summary>LIGHT membership edge (primary directed signal) — #5A6473 (5.54:1, >= 3:1
    /// non-text); dark is #8E9BB4. Kept LIGHTER than the containment edge, preserving the F6
    /// lightness channel.</summary>
    public const string GraphEdgeMemberLightHex = "#5A6473";

    /// <summary>LIGHT containment edge (subordinate, dashed) — #3A424E (9.39:1); dark is #6B788F.</summary>
    public const string GraphEdgeContainsLightHex = "#3A424E";

    /// <summary>LIGHT root node border — #1C2127 (14.98:1); dark is #E8ECF2.</summary>
    public const string GraphRootBorderLightHex = "#1C2127";

    /// <summary>LIGHT External dashed border — #6B7480 (4.38:1); dark is #B0B6BF.</summary>
    public const string GraphExternalBorderLightHex = "#6B7480";

    /// <summary>LIGHT node:selected border — #1C2127 (14.98:1); dark stays white #FFFFFF (which
    /// would vanish on the light canvas).</summary>
    public const string GraphSelectionBorderLightHex = "#1C2127";

    /// <summary>LIGHT severity halo hues (deepened from Frame 4 so the soft transparent overlay reads
    /// at/above its dark counterpart's blended ratio; redundant with the sidebar E/W/i letter + node
    /// shape): error #D63A4A (@0.70 = 2.84:1), warning #BD7C00 (@0.75 = 2.34:1), info #2F6FE0
    /// (@0.70 = 2.68:1). Dark: #D13438/#F7A30B/#4FA3E3 @0.45/0.45/0.40 (blended 1.57/2.65/2.07).</summary>
    public const string GraphSeverityErrorLightHex = "#D63A4A";

    /// <summary>LIGHT warning severity halo hue — amber #BD7C00 (see <see cref="GraphSeverityErrorLightHex"/>).</summary>
    public const string GraphSeverityWarningLightHex = "#BD7C00";

    /// <summary>LIGHT info severity halo hue — blue #2F6FE0 (see <see cref="GraphSeverityErrorLightHex"/>).
    /// Also the LIGHT busy-ring color (@0.55 = 2.12:1, dark #4FA3E3 @0.35).</summary>
    public const string GraphSeverityInfoLightHex = "#2F6FE0";

    /// <summary>LIGHT diff hues (node underlay + edge line; ADR-015 mirror): added green #1F9D57,
    /// removed red #D63A4A (= the severity error hue), unchecked gray #5A6473. Underlay opacities
    /// raised to 0.70/0.70/0.50 (dark 0.5/0.5/0.35) so the soft underlay reads on light; the
    /// near-opaque diff EDGE lines clear ~3:1 (added 3.05, removed 3.51). Dark: #2FAE4E/#E0503A/#8A8F98.</summary>
    public const string GraphDiffAddedLightHex = "#1F9D57";

    /// <summary>LIGHT diff Removed hue — #D63A4A (see <see cref="GraphDiffAddedLightHex"/>).</summary>
    public const string GraphDiffRemovedLightHex = "#D63A4A";

    /// <summary>LIGHT diff Unchecked hue — #5A6473 (see <see cref="GraphDiffAddedLightHex"/>).</summary>
    public const string GraphDiffUncheckedLightHex = "#5A6473";

    /// <summary>DARK graph decorative accent — brand purple #8B7BFF (ADR-027 D4 / WP3; the
    /// canvas-layer mirror of the chrome <see cref="AccentHex"/>). Drives the selection accent
    /// halo/pulse DOM ring in graph.js (THEME.dark.accent / index.html --gw-accent). A LARGE
    /// DECORATIVE non-text graphical object, redundant with the white node:selected border + the
    /// neighbourhood dim — not the sole selection indicator, so not gated by a hard WCAG ratio
    /// (opacity chosen to read clearly on the dark canvas #1b1f27). Wire carries only the variant
    /// string; this is the documented C# source the JS THEME.dark.accent mirrors.</summary>
    public const string GraphAccentHex = "#8B7BFF";

    /// <summary>LIGHT graph decorative accent — brand purple #6A5CFF (ADR-027 D4 / WP3; the
    /// canvas-layer mirror of the chrome <see cref="AccentLightHex"/>). THEME.light.accent /
    /// index.html --gw-accent under the light variant; opacity chosen to read on the light canvas
    /// #F5F6F8. See <see cref="GraphAccentHex"/>.</summary>
    public const string GraphAccentLightHex = "#6A5CFF";

    /// <summary><see cref="PageBackgroundHex"/> as a brush.</summary>
    public static readonly ImmutableSolidColorBrush PageBackground = new(Color.Parse(PageBackgroundHex));

    /// <summary><see cref="OnDarkTextHex"/> as a brush.</summary>
    public static readonly ImmutableSolidColorBrush OnDarkText = new(Color.Parse(OnDarkTextHex));

    /// <summary><see cref="OnLightTextHex"/> as a brush.</summary>
    public static readonly ImmutableSolidColorBrush OnLightText = new(Color.Parse(OnLightTextHex));

    /// <summary><see cref="OnLightTextStrongHex"/> as a brush.</summary>
    public static readonly ImmutableSolidColorBrush OnLightTextStrong = new(Color.Parse(OnLightTextStrongHex));

    /// <summary><see cref="NodeLiftRingHex"/> as a brush.</summary>
    public static readonly ImmutableSolidColorBrush NodeLiftRing = new(Color.Parse(NodeLiftRingHex));

    /// <summary><see cref="AccentSoftHex"/> as a brush — the DARK translucent-accent selection
    /// band (<c>SelectionHighlightConverters</c>). The active-row band is a converter-fed brush
    /// (not a theme-resolved <c>DynamicResource</c> like the <c>ListBoxItem:selected</c> band), so
    /// it uses the DARK accent-soft value; the app ships dark by default (App.axaml
    /// RequestedThemeVariant="Dark").</summary>
    public static readonly ImmutableSolidColorBrush AccentSoft = new(Color.Parse(AccentSoftHex));
}
