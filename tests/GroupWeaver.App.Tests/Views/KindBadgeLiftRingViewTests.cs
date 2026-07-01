using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

using GroupWeaver.App.Settings;
using GroupWeaver.App.Tests.Fakes;
using GroupWeaver.App.ViewModels;
using GroupWeaver.App.Views;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Providers;

using Xunit;

namespace GroupWeaver.App.Tests.Views;

/// <summary>
/// Pins issue #225 (Lever 4) at the VIEW layer: the kind-badge WCAG 1.4.11 lift-ring on the
/// three fill-contrast-failing kinds (DL 2.55 / UG 2.66 / Computer 2.59 vs the dark page). The
/// implementer added a 2px <c>#8A93A3</c> (<see cref="BrandTokens.NodeLiftRing"/>) border to the
/// kind badge in BOTH <see cref="RootPickerView"/> and <see cref="DetailPanelView"/>, driven by
/// the new <see cref="AdObjectKindConverters.ToBadgeBorderBrush"/> (ring for DL/UG/Computer,
/// transparent otherwise) — mirroring graph.js's per-kind ring (the fills themselves stay
/// unchanged). The converter itself is unit-pinned across all seven kinds in
/// <see cref="AdObjectKindConvertersTests"/>; THIS file closes the view gap: it realizes each real
/// view headless and asserts the header/candidate badge <see cref="Border"/> actually carries the
/// lift-ring brush at 2px for a ringed kind and a transparent (invisible) border for a non-ringed
/// kind — so a dropped <c>BorderBrush=</c> binding, a wrong thickness, or a badge that lost its
/// border box fails here, not in a PNG review.
///
/// <para>The badge <see cref="Border"/> is located ROBUSTLY without depending on template shape:
/// it is the ancestor <see cref="Border"/> of the realized badge-LABEL <see cref="TextBlock"/>
/// (the "DL"/"UG"/… glyph the <see cref="AdObjectKindConverters.ToBadgeLabel"/> converter emits) —
/// the label lives INSIDE the badge box, so its nearest <see cref="Border"/> ancestor is that box.
/// The label text is derived from the converter (never hardcoded), keeping badge/converter parity.</para>
///
/// <para>The DetailPanel arm renders <see cref="DetailPanelView"/> directly over a constructed
/// <see cref="DetailPanelModel"/> (a pure projection — one per kind), which needs no workspace or
/// UiStateStore. The RootPicker arm realizes the real picker over a connected stub, so it injects a
/// fresh temp-dir <see cref="UiStateStore"/> (the lab-environment rule: never read real
/// <c>%APPDATA%\GroupWeaver\ui-state.json</c>, whose persisted <c>RailCollapsed:true</c> could zero
/// realized views locally while CI stays green).</para>
/// </summary>
public sealed class KindBadgeLiftRingViewTests
{
    private const string RootDn = "OU=Lab,DC=stub,DC=lab";

    // The three fill-contrast-failing kinds that get the lift-ring, and two representative
    // non-ring kinds (a group and a user) that must keep a transparent, invisible border box.
    public static TheoryData<AdObjectKind> RingKinds() => new()
    {
        AdObjectKind.DomainLocalGroup,
        AdObjectKind.UniversalGroup,
        AdObjectKind.Computer,
    };

    public static TheoryData<AdObjectKind> NonRingKinds() => new()
    {
        AdObjectKind.GlobalGroup,
        AdObjectKind.User,
    };

    // --- DetailPanelView: the header badge -----------------------------------------------

    /// <summary>The DetailPanel header badge for a DL/UG/Computer object carries the #8A93A3
    /// lift-ring at 2px (the WCAG 1.4.11 contrast lift for these fills).</summary>
    [AvaloniaTheory]
    [MemberData(nameof(RingKinds))]
    public void DetailPanelBadge_ForRingedKind_CarriesTheLiftRingAt2px(AdObjectKind kind)
    {
        var (window, view) = ShowDetailPanel(kind);
        try
        {
            var badge = BadgeBorderFor(view, kind);
            var brush = Assert.IsAssignableFrom<ISolidColorBrush>(badge.BorderBrush);
            Assert.Equal(BrandTokens.NodeLiftRing.Color, brush.Color);
            Assert.Equal(Color.Parse("#8A93A3"), brush.Color);
            Assert.Equal(new Avalonia.Thickness(2), badge.BorderThickness);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The DetailPanel header badge for a non-ring kind (GG / User) keeps a TRANSPARENT
    /// border — the 2px box still exists (so badge geometry stays uniform) but paints invisibly,
    /// never the lift-ring color.</summary>
    [AvaloniaTheory]
    [MemberData(nameof(NonRingKinds))]
    public void DetailPanelBadge_ForNonRingKind_HasATransparentBorder(AdObjectKind kind)
    {
        var (window, view) = ShowDetailPanel(kind);
        try
        {
            var badge = BadgeBorderFor(view, kind);
            var brush = Assert.IsAssignableFrom<ISolidColorBrush>(badge.BorderBrush);
            Assert.Equal(Colors.Transparent, brush.Color);
            Assert.NotEqual(BrandTokens.NodeLiftRing.Color, brush.Color);
        }
        finally
        {
            window.Close();
        }
    }

    // --- RootPickerView: the candidate-row badge -----------------------------------------

    /// <summary>The RootPicker candidate-row badge for a DL/UG/Computer candidate carries the
    /// #8A93A3 lift-ring at 2px (parity with the DetailPanel badge and the graph node ring).</summary>
    [AvaloniaTheory]
    [MemberData(nameof(RingKinds))]
    public async Task RootPickerBadge_ForRingedKind_CarriesTheLiftRingAt2px(AdObjectKind kind)
    {
        var (window, view) = await ShowPickerWithOneCandidateAsync(kind);
        try
        {
            var badge = BadgeBorderFor(view, kind);
            var brush = Assert.IsAssignableFrom<ISolidColorBrush>(badge.BorderBrush);
            Assert.Equal(BrandTokens.NodeLiftRing.Color, brush.Color);
            Assert.Equal(Color.Parse("#8A93A3"), brush.Color);
            Assert.Equal(new Avalonia.Thickness(2), badge.BorderThickness);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The RootPicker candidate-row badge for a non-ring kind (GG / User) keeps a
    /// TRANSPARENT border box — never the lift-ring color.</summary>
    [AvaloniaTheory]
    [MemberData(nameof(NonRingKinds))]
    public async Task RootPickerBadge_ForNonRingKind_HasATransparentBorder(AdObjectKind kind)
    {
        var (window, view) = await ShowPickerWithOneCandidateAsync(kind);
        try
        {
            var badge = BadgeBorderFor(view, kind);
            var brush = Assert.IsAssignableFrom<ISolidColorBrush>(badge.BorderBrush);
            Assert.Equal(Colors.Transparent, brush.Color);
            Assert.NotEqual(BrandTokens.NodeLiftRing.Color, brush.Color);
        }
        finally
        {
            window.Close();
        }
    }

    // --- helpers -------------------------------------------------------------------------

    /// <summary>The badge <see cref="Border"/> = the nearest <see cref="Border"/> ancestor of the
    /// realized badge-LABEL <see cref="TextBlock"/> (the converter-derived "DL"/"UG"/… glyph inside
    /// the badge box). Located by the label rather than the template so a template reshape can't
    /// silently make this vacuous; <see cref="Assert.Single{T}(IEnumerable{T}, System.Func{T, bool})"/>
    /// makes the label match non-vacuous (a missing badge fails loudly).</summary>
    private static Border BadgeBorderFor(Visual scope, AdObjectKind kind)
    {
        var expectedLabel = BadgeLabelFor(kind);
        var label = Assert.Single(
            scope.GetVisualDescendants().OfType<TextBlock>(),
            t => t.IsEffectivelyVisible && t.Text == expectedLabel);
        return label.GetVisualAncestors().OfType<Border>().First();
    }

    /// <summary>The parity oracle: the badge label THE converter emits for <paramref name="kind"/>
    /// (never hardcoded), so the locator agrees with the view's own <c>ToBadgeLabel</c> binding.</summary>
    private static string BadgeLabelFor(AdObjectKind kind) =>
        Assert.IsType<string>(AdObjectKindConverters.ToBadgeLabel.Convert(
            kind, typeof(string), null, CultureInfo.InvariantCulture));

    /// <summary>Render <see cref="DetailPanelView"/> headless over a constructed
    /// <see cref="DetailPanelModel"/> of the given <paramref name="kind"/> (a pure projection —
    /// Loaded, no attributes, no chips: enough to realize the header badge).</summary>
    private static (Window Window, DetailPanelView View) ShowDetailPanel(AdObjectKind kind)
    {
        var model = new DetailPanelModel
        {
            Dn = "CN=Badge," + RootDn,
            Kind = kind,
            Name = "Badge",
            SamAccountName = null,
            State = DetailPanelState.Loaded,
            Rows = [],
            AuditChips = [],
        };
        var view = new DetailPanelView { DataContext = model };
        var window = new Window { Content = view, Width = 400, Height = 300 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view);
    }

    /// <summary>Realize the real <see cref="RootPickerView"/> headless over a live picker whose
    /// single candidate is of the given <paramref name="kind"/> (so exactly one badge realizes).</summary>
    private static async Task<(Window Window, RootPickerView View)> ShowPickerWithOneCandidateAsync(
        AdObjectKind kind)
    {
        var candidate = new AdObject
        {
            Dn = "CN=Candidate," + RootDn,
            Kind = kind,
            Name = "Candidate",
            SamAccountName = "Candidate",
        };
        var provider = new StubDirectoryProvider(
            Task.FromResult(new DirectoryConnection("stub directory", 5)))
        {
            RootCandidatesResult = Task.FromResult<IReadOnlyList<AdObject>>([candidate]),
        };
        var picker = new RootPickerViewModel(
            provider,
            new DirectoryConnection("stub directory", 5),
            onBack: () => { },
            onConfirmed: _ => { },
            uiStateStore: new UiStateStore(
                Directory.CreateTempSubdirectory("groupweaver-badgering-uistate-").FullName));
        await picker.LoadCandidates;

        var view = new RootPickerView { DataContext = picker };
        var window = new Window { Content = view, Width = 800, Height = 600 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view);
    }
}
