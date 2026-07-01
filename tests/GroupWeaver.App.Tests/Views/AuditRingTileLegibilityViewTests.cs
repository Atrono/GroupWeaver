using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

using GroupWeaver.App.Views;
using GroupWeaver.App.ViewModels;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.App.Tests.Views;

/// <summary>
/// Regression guard for issue #228 (Lever 5 — Audit health-ring + count-tile legibility). The
/// implementer made a PRESENTATION-ONLY change in <see cref="AuditView"/> (<c>AuditView.axaml</c>);
/// this fixture pins the three reversible drifts so a revert fails loudly. Sister to
/// <see cref="AuditBandProminenceViewTests"/> — same ring/band card, same headless AuditView hosting
/// idiom (a plain table UserControl; no graph renderer and no <c>UiStateStore</c> seam, so the #124
/// rail-collapse realization hazard does not apply).
///
/// <list type="number">
///   <item><b>Donut-hole opacity fix.</b> The ring's inner disc (the 70x70 <see cref="Border"/> that
///   punches the score back over the conic band) now fills with the OPAQUE
///   <c>PageBackgroundBrush</c> (alpha 255), NOT the translucent <c>CardBackgroundBrush</c>
///   (alpha 0x14 dark / 0x0A light) whose alpha let the coloured band hue bleed through behind the
///   score/band text. Asserted BOTH ways: the resolved brush colour equals the resolved
///   <c>PageBackgroundBrush</c> resource AND its alpha is fully opaque (255) — while the old
///   <c>CardBackgroundBrush</c> resource is proven translucent (alpha &lt; 255), so a revert to it
///   fails the opacity check.</item>
///   <item><b>Un-muted "/ 100" denominator.</b> The always-present numeric fallback run now carries
///   <c>Classes="body"</c> (Opacity 1.0, theme-default ink), NOT <c>Classes="caption"</c>
///   (Opacity 0.6). FontSize stays 11.</item>
///   <item><b>Un-muted "+ n info" sub-count.</b> The Warnings-tile info sub-count run now carries
///   <c>Classes="body"</c> (Opacity 1.0), NOT <c>Classes="caption"</c> (Opacity 0.6). FontSize stays
///   11.</item>
/// </list>
///
/// <para>Each run/element is located ROBUSTLY: the inner disc by its role/size in the ring visual
/// tree (a 70x70 <see cref="Border"/> with <see cref="CornerRadius"/> 35 — it has no x:Name), the
/// "/ 100" run by its content text, the "+ n info" run by its rendered content (the VM's Info count
/// formatted as "+ {n} info"). Each located via <see cref="Assert.Single{T}(System.Collections.Generic.IEnumerable{T},System.Func{T,bool})"/>
/// so an ambiguous or missing match fails loudly, never as a null deref.</para>
///
/// <para>The audit is built over the GG_Circle_A &lt;-&gt; GG_Circle_B cycle (A-&gt;B-&gt;A), so the
/// report build the ring/tiles project MUST terminate over it (the circular-case charter). The app
/// ships the Dark theme; the theme variant is pinned so the resolved-resource comparison is exact,
/// and the alpha-opacity check holds in BOTH variants regardless.</para>
/// </summary>
public sealed class AuditRingTileLegibilityViewTests
{
    private const string RootDn = "OU=Lab,DC=stub,DC=lab";
    private const string CircleADn = "CN=GG_Circle_A,OU=Lab,DC=stub,DC=lab";
    private const string CircleBDn = "CN=GG_Circle_B,OU=Lab,DC=stub,DC=lab";
    private const string GgEmptyDn = "CN=GG_Empty_Team,OU=Lab,DC=stub,DC=lab";

    // ============================================================================================
    // (1) Donut-hole opacity fix: the inner disc is the OPAQUE PageBackgroundBrush, not the
    //     translucent CardBackgroundBrush.
    // ============================================================================================

    /// <summary>
    /// The ring's inner "donut hole" disc fills with the OPAQUE page surface. Located by its role/size
    /// (the unique 70x70 <see cref="Border"/> with <see cref="CornerRadius"/> 35 inside the ring
    /// <see cref="Panel"/> — it has no x:Name). Asserts (a) its resolved <see cref="Border.Background"/>
    /// colour equals the resolved <c>PageBackgroundBrush</c> App resource, (b) that colour is fully
    /// opaque (alpha 255), and (c) it is NOT the translucent <c>CardBackgroundBrush</c> — proven by
    /// showing that resource's alpha is &lt; 255, so a revert to it fails (b).
    /// </summary>
    [AvaloniaFact]
    public void RingInnerDisc_FillsWithOpaquePageSurface_NotTranslucentCardBrush()
    {
        var app = Assert.IsAssignableFrom<Application>(Application.Current);
        app.RequestedThemeVariant = ThemeVariant.Dark;

        var audit = NewAudit();
        var (window, view) = ShowAudit(audit);

        // The inner disc: the unique 70x70 Border with CornerRadius 35 (the donut hole). No x:Name, so
        // locate by role/size in the ring visual tree — Assert.Single fails loudly if that shape drifts.
        var disc = Assert.Single(
            view.GetVisualDescendants().OfType<Border>(),
            b => b.Width == 70d && b.Height == 70d && b.CornerRadius == new CornerRadius(35d));

        // (a) The realized fill resolves to the PageBackgroundBrush App resource (the opaque surface).
        Assert.True(
            app.TryFindResource("PageBackgroundBrush", ThemeVariant.Dark, out var pageRes),
            "PageBackgroundBrush must resolve");
        var pageBrush = Assert.IsAssignableFrom<ISolidColorBrush>(pageRes);
        var discBrush = Assert.IsAssignableFrom<ISolidColorBrush>(disc.Background);
        Assert.Equal(pageBrush.Color, discBrush.Color);

        // (b) The load-bearing point: the disc colour is FULLY OPAQUE, so the band hue cannot bleed
        //     through behind the score/band text.
        Assert.Equal((byte)255, discBrush.Color.A);

        // (c) It is NOT the old translucent CardBackgroundBrush — that resource is proven translucent
        //     (alpha < 255), so a revert to it would fail the alpha==255 check above.
        Assert.True(
            app.TryFindResource("CardBackgroundBrush", ThemeVariant.Dark, out var cardRes),
            "CardBackgroundBrush must resolve");
        var cardBrush = Assert.IsAssignableFrom<ISolidColorBrush>(cardRes);
        Assert.True(cardBrush.Color.A < 255,
            "the old CardBackgroundBrush is translucent — its alpha must be < 255 for the revert guard to bite");
        Assert.NotEqual(cardBrush.Color, discBrush.Color);

        window.Close();
        audit.Dispose();
    }

    // ============================================================================================
    // (2) Un-muted "/ 100" denominator run: body (Opacity 1.0), not caption (0.6).
    // ============================================================================================

    /// <summary>
    /// The always-present "/ 100" numeric-fallback run under the score now carries <c>Classes="body"</c>
    /// (theme-default ink at full opacity), NOT the 0.6-opacity <c>Classes="caption"</c> mute. Located
    /// by its content text. Asserts BOTH the class set (body present, caption absent) AND the realized
    /// effective <see cref="Visual.Opacity"/> (1.0, not 0.6), so a revert to caption fails both ways.
    /// FontSize is still 11 (the presentation change is opacity/ink only).
    /// </summary>
    [AvaloniaFact]
    public void ScoreDenominatorRun_IsBodyNotCaption_FullOpacity()
    {
        var audit = NewAudit();
        var (window, view) = ShowAudit(audit);

        var denom = Assert.Single(
            view.GetVisualDescendants().OfType<TextBlock>(),
            t => t.IsEffectivelyVisible && t.Text == "/ 100");

        // New state present: the body class (full-opacity, theme-default ink).
        Assert.Contains("body", denom.Classes);
        // Old state absent: never the 0.6-opacity caption mute.
        Assert.DoesNotContain("caption", denom.Classes);
        // Redundant belt-and-braces: the realized opacity is full (body=1.0), never the caption 0.6.
        Assert.Equal(1.0d, denom.Opacity);
        // The FontSize was deliberately preserved at the caption 11 (only opacity/ink changed).
        Assert.Equal(11d, denom.FontSize);

        window.Close();
        audit.Dispose();
    }

    // ============================================================================================
    // (3) Un-muted "+ n info" sub-count run: body (Opacity 1.0), not caption (0.6).
    // ============================================================================================

    /// <summary>
    /// The Warnings-tile "+ {n} info" sub-count run now carries <c>Classes="body"</c> (full opacity),
    /// NOT <c>Classes="caption"</c> (0.6). Located by its rendered content (the VM's Info count formatted
    /// as "+ {n} info", so this also proves the run is the meaning-bearing sub-count, not some other
    /// body run). Asserts BOTH the class set (body present, caption absent) AND the realized effective
    /// opacity (1.0). FontSize is still 11.
    /// </summary>
    [AvaloniaFact]
    public void InfoSubCountRun_IsBodyNotCaption_FullOpacity()
    {
        var audit = NewAudit();
        var (window, view) = ShowAudit(audit);

        // The rendered content the VM produces for the Info sub-count (StringFormat '+ {0} info').
        var expected = $"+ {audit.Info} info";

        var infoSub = Assert.Single(
            view.GetVisualDescendants().OfType<TextBlock>(),
            t => t.IsEffectivelyVisible && t.Text == expected);

        // New state present: the body class (full-opacity, theme-default ink).
        Assert.Contains("body", infoSub.Classes);
        // Old state absent: never the 0.6-opacity caption mute.
        Assert.DoesNotContain("caption", infoSub.Classes);
        // Redundant belt-and-braces: the realized opacity is full (body=1.0), never the caption 0.6.
        Assert.Equal(1.0d, infoSub.Opacity);
        // The FontSize was deliberately preserved at the caption 11 (only opacity/ink changed).
        Assert.Equal(11d, infoSub.FontSize);

        window.Close();
        audit.Dispose();
    }

    // ============================================================================================
    // Helpers
    // ============================================================================================

    /// <summary>An <see cref="AuditViewModel"/> over a fully-loaded scope carrying the GG_Circle_A
    /// &lt;-&gt; GG_Circle_B cycle (so the report build MUST terminate) PLUS an empty GG (an
    /// empty-group Info), guaranteeing a NON-ZERO Info count so the "+ {n} info" sub-count run renders
    /// with meaningful content the test can locate by text.</summary>
    private static AuditViewModel NewAudit()
    {
        var snapshot = CycleScope();
        var ruleset = RulesetLoader.LoadDefault();
        var report = RuleEngine.Evaluate(snapshot, ruleset); // MUST terminate over the A<->B cycle
        var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });
        // Guard: the fixture must produce >= 1 Info so the "+ {n} info" run is present and locatable.
        Assert.True(audit.Info >= 1, "the fixture must yield at least one Info finding (the empty GG)");
        return audit;
    }

    /// <summary>Root OU + the GG_Circle_A &lt;-&gt; GG_Circle_B nesting cycle (A-&gt;B-&gt;A) + an empty
    /// GG. The cycle exercises the circular-case charter (the report build the ring projects must
    /// terminate over it); the empty GG contributes an empty-group Info so the info sub-count is
    /// non-zero.</summary>
    private static DirectorySnapshot CycleScope()
    {
        var snapshot = new DirectorySnapshot();
        snapshot.AddObject(new AdObject { Dn = RootDn, Kind = AdObjectKind.OrganizationalUnit, Name = "Lab" });
        snapshot.AddObject(new AdObject { Dn = CircleADn, Kind = AdObjectKind.GlobalGroup, Name = "GG_Circle_A" });
        snapshot.AddObject(new AdObject { Dn = CircleBDn, Kind = AdObjectKind.GlobalGroup, Name = "GG_Circle_B" });
        snapshot.AddObject(new AdObject { Dn = GgEmptyDn, Kind = AdObjectKind.GlobalGroup, Name = "GG_Empty_Team" });
        snapshot.SetMembers(CircleADn, new[] { CircleBDn });
        snapshot.SetMembers(CircleBDn, new[] { CircleADn }); // closes the A->B->A cycle (must terminate)
        snapshot.SetMembers(GgEmptyDn, System.Array.Empty<string>()); // loaded-empty => empty-group Info
        return snapshot;
    }

    /// <summary>The audit view in a sized, shown headless window (bindings live, the ring/tiles
    /// realized) — the <see cref="AuditBandProminenceViewTests"/> hosting idiom (AuditView owns no
    /// UiStateStore seam, so no temp-dir store injection is needed).</summary>
    private static (Window Window, AuditView View) ShowAudit(AuditViewModel vm)
    {
        var view = new AuditView { DataContext = vm };
        var window = new Window { Content = view, Width = 1280, Height = 720 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view);
    }
}
