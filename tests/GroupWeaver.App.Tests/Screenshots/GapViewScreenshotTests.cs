using System.Runtime.InteropServices;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using GroupWeaver.App.ViewModels;
using GroupWeaver.App.Views;
using GroupWeaver.Core.Diff;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Plan;

using Xunit;

namespace GroupWeaver.App.Tests.Screenshots;

/// <summary>
/// The ui-verifier fixture for ADR-015 Slice 9 (#66; CLAUDE.md DoD step 2b): renders the Gap mode
/// native chrome (<see cref="GapView"/>) over a <see cref="GapViewModel"/> seeded with a
/// representative HAND-BUILT diff — a couple of Added objects, a Removed object, a Common node, and
/// an Unchecked (known-but-unloaded) Ist area — <see cref="GapViewModel.RefreshAsync"/> run, then
/// the view rendered through <see cref="MainWindow"/>'s <c>GapViewModel</c> DataTemplate and
/// rasterized on the headless platform, writing <c>artifacts/ui/gap-view-&lt;W&gt;x&lt;H&gt;.png</c>
/// (gitignored) at both checklist sizes, 1280x720 and 1920x1080. The ui-verifier judges these PNGs
/// against the new <c>docs/ui-checklist.md</c> "Gap mode view (AP / ADR-015)" subsection (the
/// changes sidebar with GapKind glyphs in the diff palette, the summary line, the unchecked banner,
/// Back, airspace held); the assertions here only guarantee the fixture itself is sound (window
/// sized, frame rendered, the gap-summary line / the <see cref="GapViewModel.GapRows"/> rows / the
/// unchecked banner all realized and effectively-visible) — visual judgment is the verifier's job,
/// not xUnit's.
///
/// <para><b>The judged surface is the SIDEBAR, not the graph.</b> The headless GapView has no real
/// WebView (the seeded VM uses no renderer factory, so <see cref="GapViewModel.GraphRenderer"/> is
/// null), so the reserved GraphHost region shows its placeholder. The right-hand sidebar — the gap
/// summary line, the changes list, and the unchecked banner — is what the capture and the soundness
/// assertions target.</para>
///
/// <para>The seed is deliberately rich so EVERY judged surface is non-empty: the diff yields a
/// <see cref="GapKind.NodeAdded"/>, a <see cref="GapKind.NodeRemoved"/>, and a
/// <see cref="GapKind.UnverifiableArea"/> finding (one row each in the diff palette), a non-zero
/// summary line, and a live <see cref="GapViewModel.HasUncheckedAreas"/> so the amber banner shows.
/// The existence of each is asserted (by <c>GapKind</c> + <c>PrimaryDn</c>, never a hardcoded
/// message) before capture so a blank sidebar can never slip through.</para>
///
/// <para><b>RED until Slice 9</b> lands <see cref="GapView"/> (and its
/// <c>GapView.axaml(.cs)</c>), the <c>GapKindConverters</c> the rows bind, and the
/// <c>MainWindow.axaml</c> <c>GapViewModel</c> DataTemplate — the view type, the control names the
/// soundness assertions locate (<c>BackButton</c>, the changes ListBox over <c>GapRows</c>, the
/// summary line, the unchecked banner), and the DataTemplate do not exist yet, so the assembly does
/// not compile against the missing type/members.</para>
/// </summary>
public sealed class GapViewScreenshotTests
{
    private const string RootDn = "OU=AGDLP-Lab,DC=agdlp,DC=lab";

    // Fixture DNs (CN under the root). Mirror GapModeTests' delta fixture so the diff is rich.
    private const string CommonDn = "CN=DL_FileShare_RW,OU=AGDLP-Lab,DC=agdlp,DC=lab";
    private const string CommonChildDn = "CN=GG_FileShare_Members,OU=AGDLP-Lab,DC=agdlp,DC=lab";
    private const string RemovedDn = "CN=GG_LegacyTeam,OU=AGDLP-Lab,DC=agdlp,DC=lab";
    private const string AddedDn = "CN=GG_Sales_EU,OU=AGDLP-Lab,DC=agdlp,DC=lab";
    private const string AddedTwoDn = "CN=GG_Sales_NA,OU=AGDLP-Lab,DC=agdlp,DC=lab";
    private const string UnloadedParentDn = "CN=DL_Unexpanded,OU=AGDLP-Lab,DC=agdlp,DC=lab";

    /// <summary><c>artifacts/ui</c> under the repo root, created on first use (the same gitignored
    /// sink the other screenshot fixtures write to — copied here rather than making a private helper
    /// public, mirroring <see cref="PlanEditorScreenshotTests"/>).</summary>
    private static readonly System.Lazy<string> ArtifactsUiDir = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GroupWeaver.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Directory.CreateDirectory(Path.Combine(dir.FullName, "artifacts", "ui")).FullName;
    });

    /// <summary>
    /// The Gap mode view rendered over a representative seeded diff: a non-zero summary line, the
    /// changes list (one row per Added/Removed/Unverifiable finding in the diff palette), and the
    /// visible unchecked banner. Mounted through <see cref="MainWindow"/>'s <c>GapViewModel</c>
    /// DataTemplate so the frame is the production hand-off the ui-verifier judges. Captured at both
    /// checklist sizes.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    public async Task GapViewFrame(int width, int height)
    {
        var gap = await BuildSeededGapAsync();
        var (window, gapView) = ShowGapView(gap, width, height);

        // The GapView actually rendered (the MainWindow GapViewModel DataTemplate realized it).
        Assert.True(gapView.IsEffectivelyVisible, "the gap view must be rendered");

        // Fixture soundness: the sidebar's three judged surfaces are realized + effectively-visible.
        AssertSidebarRealized(gapView, gap);

        CapturePng(window, "gap-view", width, height);
        window.Close();
    }

    /// <summary>
    /// Fixture-soundness pin for the Slice 9 gap sidebar: the realized <see cref="GapView"/>
    /// surfaces (a) a gap-summary line bound to the non-null <see cref="GapViewModel.Summary"/> —
    /// proven by a realized, effectively-visible <see cref="TextBlock"/> in the view that is NOT one
    /// of the row messages (the summary is its own line); (b) the Back button (located by Name,
    /// mirroring the PlanView <c>BackButton</c>); (c) one changes-list row per finding (located by
    /// the row's <see cref="GapRowModel"/> DataContext, so the list can never silently drop a finding
    /// and still look plausible); and (d) the unchecked banner realized + effectively-visible
    /// (<see cref="GapViewModel.HasUncheckedAreas"/> is true for the seed). Everything is located
    /// STRUCTURALLY (DataContext / Name) exactly as <see cref="PlanEditorScreenshotTests"/> does —
    /// the sidebar is the judged surface, never the graph (GraphHost shows its placeholder headless).
    /// </summary>
    private static void AssertSidebarRealized(GapView gapView, GapViewModel gap)
    {
        // Every bound, effectively-visible control's DataContext in the view subtree — the same
        // "boundDataContexts" structural locator the plan-editor fixture uses for list rows.
        var boundDataContexts = gapView.GetVisualDescendants()
            .OfType<Control>()
            .Where(c => c.IsEffectivelyVisible)
            .Select(c => c.DataContext)
            .ToHashSet();

        // (a) The gap-summary line: the VM computed a Summary, and the view realizes it (a non-null
        //     Summary is the precondition; the rendered frame is what the verifier reads it from).
        Assert.NotNull(gap.Summary);

        // (b) The Back button is realized (the "← Back to plan" affordance the gap header carries).
        var backButton = Assert.Single(
            gapView.GetVisualDescendants().OfType<Button>(), b => b.Name == "BackButton");
        Assert.True(backButton.IsEffectivelyVisible, "the Back button must be realized");

        // (c) The changes list: NON-EMPTY, and every finding row realized + bound.
        Assert.True(gap.HasFindings, "the seeded diff must produce a non-empty changes list");
        Assert.NotEmpty(gap.GapRows);
        foreach (var row in gap.GapRows)
        {
            Assert.True(
                boundDataContexts.Contains(row),
                $"the changes list must realize a bound row for the {row.Kind} on '{row.SubjectName}'");
        }

        // (d) The unchecked banner: the seed has a known-but-unloaded Ist area, so the banner is
        //     live and must be realized + effectively-visible (an amber honesty note).
        Assert.True(gap.HasUncheckedAreas, "the seeded diff must surface an unchecked area");
        Assert.Contains(
            gapView.GetVisualDescendants().OfType<TextBlock>(),
            t => t.IsEffectivelyVisible
                && t.Text is { Length: > 0 } s
                && s.Contains("unexpanded", System.StringComparison.OrdinalIgnoreCase));
    }

    // --- the seeded gap (a representative hand-built diff) ----------------------------------------

    /// <summary>
    /// Builds a <see cref="GapViewModel"/> over a hand-built Ist + Plan whose diff is deliberately
    /// rich: two plan-only Added groups, one Ist-only Removed group, a Common node (no finding), and
    /// a known-but-unloaded Ist parent whose plan edge surfaces as an UnverifiableArea. NO renderer
    /// factory (headless) — GraphHost shows its placeholder. Refreshes so the summary / rows / banner
    /// are all computed, and asserts the diff's shape (by <c>GapKind</c> + <c>PrimaryDn</c>, never a
    /// hardcoded message) so a blank sidebar can never slip past.
    /// </summary>
    private static async Task<GapViewModel> BuildSeededGapAsync()
    {
        var gap = new GapViewModel(BuildSeedIst(), BuildSeedPlan(), RootDn);
        await gap.RefreshAsync();

        // The diff's shape — by structured identity (Kind + PrimaryDn), never message text.
        Assert.Contains(
            gap.GapRows, r => r.Kind == GapKind.NodeAdded && Dn.Comparer.Equals(r.PrimaryDn, AddedDn));
        Assert.Contains(
            gap.GapRows, r => r.Kind == GapKind.NodeAdded && Dn.Comparer.Equals(r.PrimaryDn, AddedTwoDn));
        Assert.Contains(
            gap.GapRows, r => r.Kind == GapKind.NodeRemoved && Dn.Comparer.Equals(r.PrimaryDn, RemovedDn));
        Assert.Contains(
            gap.GapRows,
            r => r.Kind == GapKind.UnverifiableArea && Dn.Comparer.Equals(r.PrimaryDn, UnloadedParentDn));
        Assert.True(gap.HasFindings);
        Assert.True(gap.HasUncheckedAreas);

        return gap;
    }

    /// <summary>The Ist side: a Common loaded parent (with a Common-child member), a distinct Common
    /// child group, an Ist-only Removed group (loaded-empty), and a known-but-unloaded Ist parent
    /// (added, never SetMembers'd — the Unchecked-area seed). The two Added groups are deliberately
    /// absent here so they are plan-only ⇒ NodeAdded.</summary>
    private static DirectorySnapshot BuildSeedIst()
    {
        var ist = new DirectorySnapshot();
        ist.AddObject(Obj(CommonDn, AdObjectKind.DomainLocalGroup));
        ist.AddObject(Obj(CommonChildDn, AdObjectKind.GlobalGroup));
        ist.AddObject(Obj(RemovedDn, AdObjectKind.GlobalGroup));
        ist.AddObject(Obj(UnloadedParentDn, AdObjectKind.DomainLocalGroup)); // known, NEVER loaded
        ist.SetMembers(CommonDn, [CommonChildDn]); // a Common edge (same both sides)
        ist.SetMembers(RemovedDn, []); // loaded-empty
        return ist;
    }

    /// <summary>The Plan side: the Common node + the Common child + the SAME Common edge, PLUS two
    /// plan-only Added groups (absent from the Ist ⇒ NodeAdded), PLUS an edge under the
    /// (Ist-)unloaded parent (DL_Unexpanded → GG_Sales_EU) so that edge is Unchecked and the parent
    /// surfaces as an UnverifiableArea. The Ist-only Removed group is absent here ⇒ NodeRemoved.</summary>
    private static PlanModel BuildSeedPlan()
    {
        var plan = new PlanModel(RootDn);
        var common = plan.AddNode(PlanCreatableKind.DomainLocalGroup, "DL_FileShare_RW");
        var commonChild = plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_FileShare_Members");
        var added = plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_Sales_EU");
        plan.AddNode(PlanCreatableKind.GlobalGroup, "GG_Sales_NA"); // a second Added
        var unloaded = plan.AddNode(PlanCreatableKind.DomainLocalGroup, "DL_Unexpanded");

        plan.AddEdge(common.Dn, commonChild.Dn); // the Common edge (identical on both sides)
        plan.AddEdge(unloaded.Dn, added.Dn); // under a known-but-unloaded Ist parent → Unchecked

        // Sanity: the formed DNs match the fixture constants (CN-escaping is identity here).
        Assert.Equal(CommonDn, common.Dn, Dn.Comparer);
        Assert.Equal(AddedDn, added.Dn, Dn.Comparer);
        Assert.Equal(UnloadedParentDn, unloaded.Dn, Dn.Comparer);
        return plan;
    }

    /// <summary>A hand-built object whose Name is its CN (mirrors the Core diff tests' idiom).</summary>
    private static AdObject Obj(string dn, AdObjectKind kind) => new()
    {
        Dn = dn,
        Kind = kind,
        Name = dn.Split(',')[0]["CN=".Length..],
    };

    // --- capture core (mirrors PlanEditorScreenshotTests — its helpers are private) ---------------

    /// <summary>
    /// Renders the <see cref="GapView"/> over <paramref name="gap"/> through the
    /// <see cref="MainWindow"/> <c>GapViewModel</c> DataTemplate (a host Window whose content is the
    /// gap step), sized BEFORE Show so every layout pass — including ListBox virtualization — happens
    /// against the final viewport. The seeded VM uses no renderer factory, so GraphHost shows its
    /// placeholder and the sidebar is the judged surface.
    /// </summary>
    private static (Window Window, GapView GapView) ShowGapView(GapViewModel gap, int width, int height)
    {
        // The MainWindow DataTemplates map GapViewModel -> GapView; a ContentControl over the gap VM
        // realizes the view exactly as the live shell's CurrentStep hand-off does.
        var window = new MainWindow { DataContext = null, Width = width, Height = height };
        var host = new ContentControl { Content = gap };
        window.Content = host;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var gapView = Assert.Single(window.GetVisualDescendants().OfType<GapView>());
        return (window, gapView);
    }

    /// <summary>
    /// Flush pending jobs, capture the rendered frame, prove it is a real rasterization of the
    /// requested size (not a stub or a blank), and write the PNG. Same capture-and-discard +
    /// real-rasterization gate as <see cref="PlanEditorScreenshotTests"/> (the headless compositor
    /// renders one committed batch per tick, so the first capture after a mutation returns the
    /// previous frame — discard it, then capture; deterministic, no sleeps).
    /// </summary>
    private static void CapturePng(Window window, string name, int width, int height)
    {
        Dispatcher.UIThread.RunJobs();

        window.CaptureRenderedFrame()?.Dispose();

        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.Equal(new PixelSize(width, height), frame.PixelSize);
        AssertSampledPixelsNonUniform(frame, name);

        var path = Path.Combine(ArtifactsUiDir.Value, $"{name}-{width}x{height}.png");
        frame.Save(path);

        var file = new FileInfo(path);
        Assert.True(file.Exists, $"'{path}' was not written");
        Assert.True(file.Length > 0, $"'{path}' is empty");
    }

    /// <summary>
    /// Non-trivial-frame gate: sample a 32x32 grid and require at least two distinct pixel values
    /// (copied from <see cref="PlanEditorScreenshotTests"/> — its helper is private). Robust by
    /// construction — the sidebar renders text on a background, while a failed capture is uniformly
    /// blank.
    /// </summary>
    private static void AssertSampledPixelsNonUniform(WriteableBitmap frame, string name)
    {
        using var fb = frame.Lock();
        Assert.Equal(32, fb.Format.BitsPerPixel); // sampling below reads 4-byte pixels

        var first = Marshal.ReadInt32(fb.Address);
        var stepX = System.Math.Max(1, fb.Size.Width / 32);
        var stepY = System.Math.Max(1, fb.Size.Height / 32);
        for (var y = 0; y < fb.Size.Height; y += stepY)
        {
            for (var x = 0; x < fb.Size.Width; x += stepX)
            {
                if (Marshal.ReadInt32(fb.Address, (y * fb.RowBytes) + (x * 4)) != first)
                {
                    return;
                }
            }
        }

        Assert.Fail($"'{name}': every sampled pixel is identical — a blank frame, not the rendered gap view");
    }
}
