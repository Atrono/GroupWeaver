using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

using GroupWeaver.App.Audit;
using GroupWeaver.App.Tests.Screenshots;
using GroupWeaver.App.Views;
using GroupWeaver.App.ViewModels;
using GroupWeaver.Core.Audit;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.App.Tests.Views;

/// <summary>
/// Regression guard for issue #268 (findings audit-1/audit-2 — WCAG 1.4.3 double-composite fail).
/// Four <see cref="Border"/>s in <see cref="AuditView"/> (<c>AuditView.axaml</c>) used to fill with
/// the translucent <c>CardBackgroundBrush</c> while nested inside an ALREADY-<c>CardBackgroundBrush</c>
/// parent card — a double composite that reproducibly measured ~4.43:1 in light theme (below the
/// 4.5:1 text floor, per the fit-audit's own recorded evidence). The fix mirrors the already-shipped
/// run-history drift-tile fix (<c>AuditView.axaml</c>'s own comment at ~886-894): swap the inner
/// Border's fill to the OPAQUE <c>PageBackgroundBrush</c> so it composites only ONCE against the page,
/// never against the translucent card. This fixture pins all four sites (same idiom as
/// <see cref="AuditRingTileLegibilityViewTests"/>'s donut-hole check) so a revert to
/// <c>CardBackgroundBrush</c> on any of them fails loudly here rather than silently reintroducing the
/// contrast fail.
///
/// <list type="number">
///   <item>The "Open" status pill (AuditView.axaml ~658-664, audit-1) — the findings-table row status
///   column, rendered on every non-triaged finding.</item>
///   <item>The triage caveat (~179-191, audit-2) — the ADR-030 D2 "N findings excluded" disclosure
///   beside the health band.</item>
///   <item>The unchecked caveat (~330-342, audit-2) — the "unexpanded areas are unchecked" tri-state
///   honesty disclosure in the categories pane.</item>
///   <item>The run-history honesty banner (~944-955, audit-2) — the compare card's "one of these runs
///   had unexpanded areas" disclosure.</item>
/// </list>
///
/// <para>Each Border is located ROBUSTLY (no x:Name on any of the four): find the caveat/pill's own
/// TEXT content (fixed string, or the VM's rendered <c>StatusLabel</c>/<c>TriageCaveatText</c>), then
/// walk up to its nearest ancestor <see cref="Border"/> — the immediate caveat/pill container, never
/// the surrounding card. Each assertion is the same three-part proof
/// <see cref="AuditRingTileLegibilityViewTests"/> established: (a) the resolved fill equals the
/// resolved <c>PageBackgroundBrush</c> resource, (b) that colour is fully OPAQUE (alpha 255 — the
/// load-bearing point, since a translucent fill re-introduces the double composite), and (c) it is NOT
/// the translucent <c>CardBackgroundBrush</c> (proven translucent, alpha &lt; 255, so a revert fails
/// (b)). Pinned in Dark (<see cref="ThemeVariant.Dark"/>, the app's shipped default) — the resource
/// IDENTITY being asserted (Page vs. Card) and both brushes' opacity relationship hold in Light too
/// (Tokens.axaml), so a single variant is sufficient (mirrors the sibling fixture's choice).</para>
/// </summary>
public sealed class AuditOpaqueCaveatSurfaceViewTests
{
    private const string RootDn = "OU=Lab,DC=stub,DC=lab";

    // ============================================================================================
    // (1) The "Open" status pill (audit-1).
    // ============================================================================================

    /// <summary>
    /// The findings-table "Open" status pill fills with the OPAQUE page surface, not the translucent
    /// card tint (the double composite inside the CardBackgroundBrush findings-table card). Located by
    /// the rendered "Open" <see cref="AuditFindingRowModel.StatusLabel"/> text — BOTH the Open pill and
    /// its sibling Acknowledged/Suppressed pill bind the same <c>StatusLabel</c> text (only one Border
    /// is <c>IsVisible</c> at a time), so the visibility filter is load-bearing to disambiguate.
    /// </summary>
    [AvaloniaFact]
    public void OpenStatusPillBorder_FillsWithOpaquePageSurface_NotTranslucentCardBrush()
    {
        var app = Assert.IsAssignableFrom<Application>(Application.Current);
        app.RequestedThemeVariant = ThemeVariant.Dark;

        using var audit = OneOpenFindingAudit();
        var (window, view) = ShowAudit(audit);

        // The WP1 status FILTER CHIP strip also has a "Open" chip (a Button, not the row pill), so the
        // text alone is ambiguous — the pill's TextBlock sits DIRECTLY inside its Border (no
        // ContentPresenter between them, unlike the chip's Button->ContentPresenter->StackPanel), so
        // requiring the immediate visual parent to be a Border disambiguates the row pill from the chip.
        var pillText = Assert.Single(
            view.GetVisualDescendants().OfType<TextBlock>(),
            t => t.IsEffectivelyVisible && t.Text == "Open" && t.GetVisualParent() is Border);
        var pillBorder = (Border)pillText.GetVisualParent()!;

        AssertOpaquePageSurface(app, pillBorder);

        window.Close();
    }

    // ============================================================================================
    // (2) Triage caveat (audit-2).
    // ============================================================================================

    /// <summary>The ADR-030 D2 triage caveat block fills with the opaque page surface, not the
    /// translucent card tint (double composite inside the health-ring CardBackgroundBrush card).
    /// Located by its rendered <see cref="AuditViewModel.TriageCaveatText"/>.</summary>
    [AvaloniaFact]
    public void TriageCaveatBorder_FillsWithOpaquePageSurface_NotTranslucentCardBrush()
    {
        var app = Assert.IsAssignableFrom<Application>(Application.Current);
        app.RequestedThemeVariant = ThemeVariant.Dark;

        using var audit = OneTriagedFindingAudit();
        Assert.True(audit.HasTriaged, "the fixture must trip the triage caveat");
        var (window, view) = ShowAudit(audit);

        var caveatText = Assert.Single(
            view.GetVisualDescendants().OfType<TextBlock>(),
            t => t.IsEffectivelyVisible && t.Text == audit.TriageCaveatText);
        var caveatBorder = caveatText.GetVisualAncestors().OfType<Border>().First();

        AssertOpaquePageSurface(app, caveatBorder);

        window.Close();
    }

    // ============================================================================================
    // (3) Unchecked caveat (audit-2).
    // ============================================================================================

    /// <summary>The "unexpanded areas are unchecked" caveat block (categories pane) fills with the
    /// opaque page surface, not the translucent card tint (double composite inside the categories-pane
    /// CardBackgroundBrush card). Located by its fixed caption text.</summary>
    [AvaloniaFact]
    public void UncheckedCaveatBorder_FillsWithOpaquePageSurface_NotTranslucentCardBrush()
    {
        var app = Assert.IsAssignableFrom<Application>(Application.Current);
        app.RequestedThemeVariant = ThemeVariant.Dark;

        using var audit = UncheckedAudit();
        Assert.True(audit.UncheckedPresent, "the fixture must trip the unchecked caveat");
        var (window, view) = ShowAudit(audit);

        var caveatText = Assert.Single(
            view.GetVisualDescendants().OfType<TextBlock>(),
            t => t.IsEffectivelyVisible &&
                 t.Text == "Unexpanded areas are unchecked — the score covers checked objects only.");
        var caveatBorder = caveatText.GetVisualAncestors().OfType<Border>().First();

        AssertOpaquePageSurface(app, caveatBorder);

        window.Close();
    }

    // ============================================================================================
    // (4) Run-history honesty banner (audit-2).
    // ============================================================================================

    /// <summary>The run-history compare card's honesty banner fills with the opaque page surface, not
    /// the translucent card tint (double composite inside the compare-card CardBackgroundBrush card —
    /// the SAME site whose sibling drift-tile fix the file's own comment at ~886-894 documents; this
    /// banner sits right below those tiles and was NOT covered by that earlier fix). Located by its
    /// fixed caption text; the comparison is pre-seeded (a prior <see cref="AuditRun"/> with a non-empty
    /// <see cref="AuditRun.UncheckedDns"/>) directly through <see cref="AuditRunStore"/> — no shell,
    /// mirroring <see cref="AuditRunCompareScreenshotTests"/>'s lighter seeding idiom but
    /// skipping the full <c>ShellViewModel</c>/<c>DemoProvider</c> drive (only the VM's own
    /// <see cref="AuditViewModel.UseRunStore"/> seam is needed).</summary>
    [AvaloniaFact]
    public void RunHistoryHonestyBannerBorder_FillsWithOpaquePageSurface_NotTranslucentCardBrush()
    {
        var app = Assert.IsAssignableFrom<Application>(Application.Current);
        app.RequestedThemeVariant = ThemeVariant.Dark;

        using var audit = OneOpenFindingAudit();

        var runsBase = Directory.CreateTempSubdirectory("groupweaver-audit-opaque-caveat-runs-").FullName;
        var runStore = new AuditRunStore(runsBase);
        audit.UseRunStore(runStore);
        Assert.True(audit.CanCompare, "the run-store seam must be armed");

        var prior = new AuditRun(
            AuditRun.CurrentSchemaVersion,
            new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero),
            audit.RootDn,
            "test · prior run",
            "Test ruleset",
            "0000000000000000000000000000000000000000000000000000000000000000",
            new AuditSummary(100, "Excellent", 0, 0, 0, 0, 0, 0, false, new Dictionary<string, int>()),
            Array.Empty<AuditRunFinding>(),
            new[] { "CN=SomeUnloadedGroup,OU=Lab,DC=stub,DC=lab" }); // non-empty -> UncheckedPresent
        runStore.Save(prior);

        audit.CompareToPreviousRunCommand.Execute(null);
        Assert.True(audit.HasComparison, "the seeded prior run for this RootDn must produce a comparison");
        Assert.True(audit.Comparison!.UncheckedPresent, "the prior run's non-empty UncheckedDns must trip the banner");

        var (window, view) = ShowAudit(audit);

        var bannerText = Assert.Single(
            view.GetVisualDescendants().OfType<TextBlock>(),
            t => t.IsEffectivelyVisible &&
                 t.Text == "One of these runs had unexpanded areas — this comparison is partial; " +
                     "a finding that vanished under an unexpanded parent is counted as Now unchecked, not Fixed.");
        var bannerBorder = bannerText.GetVisualAncestors().OfType<Border>().First();

        AssertOpaquePageSurface(app, bannerBorder);

        window.Close();
    }

    // ============================================================================================
    // Shared assertion + helpers
    // ============================================================================================

    /// <summary>The three-part proof (mirrors <see cref="AuditRingTileLegibilityViewTests"/>'s donut-
    /// hole check): (a) the Border's resolved fill equals the resolved <c>PageBackgroundBrush</c>
    /// resource, (b) that colour is fully opaque (alpha 255 — the load-bearing point), and (c) it is
    /// NOT the translucent <c>CardBackgroundBrush</c> (proven translucent here, so a revert to it fails
    /// (b)).</summary>
    private static void AssertOpaquePageSurface(Application app, Border border)
    {
        Assert.True(
            app.TryFindResource("PageBackgroundBrush", ThemeVariant.Dark, out var pageRes),
            "PageBackgroundBrush must resolve");
        var pageBrush = Assert.IsAssignableFrom<ISolidColorBrush>(pageRes);
        var borderBrush = Assert.IsAssignableFrom<ISolidColorBrush>(border.Background);

        // (a) The realized fill resolves to the PageBackgroundBrush App resource (the opaque surface).
        Assert.Equal(pageBrush.Color, borderBrush.Color);

        // (b) The load-bearing point: the fill is FULLY OPAQUE, so no further compositing can occur.
        Assert.Equal((byte)255, borderBrush.Color.A);

        // (c) It is NOT the old translucent CardBackgroundBrush — that resource is proven translucent
        //     (alpha < 255), so a revert to it would fail the alpha==255 check above.
        Assert.True(
            app.TryFindResource("CardBackgroundBrush", ThemeVariant.Dark, out var cardRes),
            "CardBackgroundBrush must resolve");
        var cardBrush = Assert.IsAssignableFrom<ISolidColorBrush>(cardRes);
        Assert.True(cardBrush.Color.A < 255,
            "the old CardBackgroundBrush is translucent — its alpha must be < 255 for the revert guard to bite");
        Assert.NotEqual(cardBrush.Color, borderBrush.Color);
    }

    /// <summary>An audit with EXACTLY one finding (one empty GG => one empty-group Info), so the row is
    /// Open (no triage tags) and uniquely locatable by its rendered "Open" pill text.</summary>
    private static AuditViewModel OneOpenFindingAudit()
    {
        const string ggEmpty = "CN=GG_Empty_Team,OU=Lab,DC=stub,DC=lab";
        var snapshot = new DirectorySnapshot();
        snapshot.AddObject(new AdObject { Dn = RootDn, Kind = AdObjectKind.OrganizationalUnit, Name = "Lab" });
        snapshot.AddObject(new AdObject { Dn = ggEmpty, Kind = AdObjectKind.GlobalGroup, Name = "GG_Empty_Team" });
        snapshot.SetMembers(ggEmpty, Array.Empty<string>()); // loaded-empty => the one empty-group Info

        var ruleset = RulesetLoader.LoadDefault();
        var report = RuleEngine.Evaluate(snapshot, ruleset);
        var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });
        Assert.Single(audit.Findings);
        Assert.False(audit.Findings[0].IsTriaged, "the fixture's only finding must start Open");
        return audit;
    }

    /// <summary>An audit whose live report has trimmed exactly ONE finding relative to the would-be
    /// (full) evaluation, so <see cref="AuditViewModel.TriagedCount"/> == 1 and
    /// <see cref="AuditViewModel.HasTriaged"/> is true (the <see cref="AuditTriageCaveatTextTests"/>
    /// idiom, restated here so this fixture stays independent).</summary>
    private static AuditViewModel OneTriagedFindingAudit()
    {
        var snapshot = new DirectorySnapshot();
        for (var i = 0; i < 3; i++)
        {
            var dn = $"CN=GG_Empty_{i:D2},OU=Lab,DC=stub,DC=lab";
            snapshot.AddObject(new AdObject { Dn = dn, Kind = AdObjectKind.GlobalGroup, Name = dn });
            snapshot.SetMembers(dn, Array.Empty<string>());
        }

        var ruleset = RulesetLoader.LoadDefault();
        var full = RuleEngine.Evaluate(snapshot, ruleset);
        Assert.True(full.Violations.Count > 1, "the fixture must produce more than 1 finding to trim");

        var live = new RuleReport(full.Violations.Skip(1).ToArray(), full.UncheckedDns);
        return new AuditViewModel(snapshot, live, ruleset, RootDn, onBack: () => { });
    }

    /// <summary>An audit whose report carries a non-empty <see cref="RuleReport.UncheckedDns"/> (a
    /// fabricated raw DN — <see cref="AuditSummary.UncheckedPresent"/> only checks the list's count, so
    /// no real unloaded parent group is needed).</summary>
    private static AuditViewModel UncheckedAudit()
    {
        var snapshot = new DirectorySnapshot();
        snapshot.AddObject(new AdObject { Dn = RootDn, Kind = AdObjectKind.OrganizationalUnit, Name = "Lab" });

        var ruleset = RulesetLoader.LoadDefault();
        var report = new RuleReport(
            Array.Empty<RuleViolation>(),
            new[] { "CN=SomeUnloadedGroup,OU=Lab,DC=stub,DC=lab" });
        return new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });
    }

    /// <summary>The audit view in a sized, shown headless window (bindings live) — the
    /// <see cref="AuditRingTileLegibilityViewTests"/>/<see cref="AuditRowTriageViewTests"/> hosting
    /// idiom (AuditView owns no UiStateStore seam, so no temp-dir store injection is needed).</summary>
    private static (Window Window, AuditView View) ShowAudit(AuditViewModel vm)
    {
        var view = new AuditView { DataContext = vm };
        var window = new Window { Content = view, Width = 1280, Height = 960 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view);
    }
}
