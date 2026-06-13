using System.Globalization;
using System.Runtime.InteropServices;

using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using GroupWeaver.App.Rules;
using GroupWeaver.App.Settings;
using GroupWeaver.App.Startup;
using GroupWeaver.App.ViewModels;
using GroupWeaver.App.Views;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Rules;
using GroupWeaver.Providers;

using Xunit;

namespace GroupWeaver.App.Tests.Screenshots;

/// <summary>
/// The ui-verifier fixture (AP 2.1 S8, CLAUDE.md DoD step 2b): renders every shipped
/// shell state (AP 2.1 + the AP 2.5 detail panel) through the REAL pipeline — real
/// <see cref="DemoProvider"/>, real
/// views, real Skia rasterization on the headless platform — and writes the frames to
/// <c>artifacts/ui/&lt;view&gt;-&lt;W&gt;x&lt;H&gt;.png</c> (gitignored) at both checklist
/// sizes, 1280x720 and 1920x1080. The ui-verifier judges these PNGs against
/// <c>docs/ui-checklist.md</c> section B; the assertions here only guarantee the fixture
/// itself is sound (window actually sized, frame actually rendered, file actually
/// written) — visual judgment is the verifier's job, not xUnit's.
/// </summary>
public sealed class ShellScreenshotTests
{
    private static readonly WebView2RuntimeStatus Present = new(IsInstalled: true, Version: "x");
    private static readonly WebView2RuntimeStatus Missing = new(IsInstalled: false, Version: null);

    /// <summary>The AP 2.5 detail-panel subject: the demo dataset's first user.</summary>
    private const string DemoUserDn =
        "CN=Anna Acker (u001),OU=Users,OU=AGDLP-Demo,DC=weavedemo,DC=example";

    /// <summary><c>artifacts/ui</c> under the repo root, created on first use.</summary>
    private static readonly Lazy<string> ArtifactsUiDir = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GroupWeaver.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Directory.CreateDirectory(Path.Combine(dir.FullName, "artifacts", "ui")).FullName;
    });

    // --- Connect step --------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    public void ConnectionIdle(int width, int height)
    {
        var (window, shell) = ShowShell(Present, width, height);
        Assert.IsType<ConnectionViewModel>(shell.CurrentStep);

        CapturePng(window, "connection-idle", width, height);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    public void ConnectionError(int width, int height)
    {
        var (window, shell) = ShowShell(Present, width, height);
        var connect = Assert.IsType<ConnectionViewModel>(shell.CurrentStep);

        // Sentinel error in the exact shape the live path produces (message + demo-mode
        // hint on a second line) so the verifier judges the real error layout.
        connect.ErrorMessage =
            "No domain controller answered (screenshot sentinel).\n"
            + "No domain is reachable in this user context — try Demo mode for the embedded demo directory.";

        CapturePng(window, "connection-error", width, height);
        window.Close();
    }

    // --- PickRoot step ---------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    public async Task RootPickerDemo(int width, int height)
    {
        var (window, shell) = ShowShell(Present, width, height);
        var picker = await ConnectIntoPickerAsync(shell);
        Dispatcher.UIThread.RunJobs();

        // One candidate selected: the frame must show the selection highlight AND the
        // mandatory entry filter satisfied (Load enabled) — assert the latter so the
        // fixture cannot silently capture a disabled Load button.
        picker.SelectedCandidate = picker.Candidates[0];
        Assert.True(picker.LoadRootCommand.CanExecute(null));

        CapturePng(window, "rootpicker-demo", width, height);

        // Tail frame: the name-sorted list puts every GG_*/UG_* row below the scroll
        // fold, so the head frame above can never evidence those two badge kinds.
        // Selecting the LAST UniversalGroup makes the ListBox (AutoScrollToSelectedItem
        // is on by default) scroll the tail into view: GG and UG badges both in frame,
        // plus the selection highlight re-evidenced on a UG row.
        picker.SelectedCandidate = picker.Candidates
            .Last(c => c.Kind == AdObjectKind.UniversalGroup);
        Assert.True(picker.LoadRootCommand.CanExecute(null));

        CapturePng(window, "rootpicker-demo-tail", width, height);
        window.Close();
    }

    // --- Workspace step ----------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    public async Task WorkspaceDemo(int width, int height)
    {
        var (window, shell) = ShowShell(Present, width, height);
        await DriveToWorkspaceAsync(shell);

        CapturePng(window, "workspace-demo", width, height);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    public async Task WorkspaceWebView2Missing(int width, int height)
    {
        var (window, shell) = ShowShell(Missing, width, height);
        await DriveToWorkspaceAsync(shell);

        CapturePng(window, "workspace-webview2-missing", width, height);
        window.Close();
    }

    /// <summary>
    /// AP 3.4 (ADR-010 §5): the violations sidebar topping the right column, above the
    /// AP 2.5 detail stack (the <c>2*,Auto,3*</c> vertical split, beside GraphHost, never
    /// over it — ADR-001 airspace). Driven to the settled workspace via the same
    /// <see cref="DriveToWorkspaceAsync"/> the other workspace frames use, default ruleset
    /// (the AP 3.2 demo baseline = 19 findings = E 4 · W 3 · i 12).
    ///
    /// Beyond the airspace pin (sidebar renders right of GraphHost), this fixture pins the
    /// header severity-summary chip strip — the ui-verifier B-2 evidence gap. The canonical
    /// errors-first row order pushes Warning/Info below the scroll fold, so the chip strip
    /// is the ONLY above-the-fold evidence that all three severities exist; without a pin it
    /// could silently regress (e.g. a chip collapses, or a glyph drifts off the ADR-010
    /// palette) and every static frame would still look plausible. So
    /// <see cref="AssertSeverityChipStrip"/> addresses the three glyph squares in the visual
    /// tree (the 18×18 chip borders are unique to the strip — rows use 20×20) and asserts,
    /// per severity: the count text (E 4 / W 3 / i 12, the demo baseline AND parity with the
    /// <c>CountForSeverity</c> tally) and that the glyph brush is the pinned ADR-010 palette
    /// color (#D13438 / #F7A30B / #4FA3E3, parity with <c>SeverityConverters.ToBrush</c>).
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    public async Task WorkspaceViolations(int width, int height)
    {
        var (window, shell) = ShowShell(Present, width, height);
        var workspace = await DriveToWorkspaceAsync(shell);
        Assert.IsType<WorkspaceViewModel>(shell.CurrentStep);
        Dispatcher.UIThread.RunJobs();

        // The violations sidebar VIEW actually rendered, and it lives in the right detail
        // column (right of GraphHost — the airspace pin, as in DetailPanelViewTests).
        var sidebar = Assert.Single(
            window.GetVisualDescendants().OfType<ViolationsSidebarView>());
        Assert.True(sidebar.IsEffectivelyVisible, "the violations sidebar must be rendered");

        var graphHost = Assert.Single(window.GetVisualDescendants().OfType<Avalonia.Controls.ContentControl>()
, c => c.Name == "GraphHost");
        var sidebarLeft = sidebar.TranslatePoint(new Point(0, 0), window);
        var graphRight = graphHost.TranslatePoint(
            new Point(graphHost.Bounds.Width, 0), window);
        Assert.NotNull(sidebarLeft);
        Assert.NotNull(graphRight);
        Assert.True(
            sidebarLeft.Value.X >= graphRight.Value.X - 0.5,
            $"the violations sidebar (X={sidebarLeft.Value.X}) must sit right of "
            + $"GraphHost (right edge X={graphRight.Value.X}) — never over the graph");

        // The header chip strip evidences ALL THREE severities above the fold (ui-verifier
        // B-2): the demo baseline is E 4 · W 3 · i 12, each glyph in the ADR-010 palette.
        AssertSeverityChipStrip(workspace, sidebar);

        CapturePng(window, "workspace-violations", width, height);
        window.Close();
    }

    /// <summary>
    /// Pins the AP 3.4 header severity-summary chip strip (ADR-010 §5; ui-verifier B-2):
    /// all THREE severity chips are present, visible, and individually correct — count text
    /// AND glyph-square palette color. The chips are not named controls, so they are located
    /// structurally: the three 18×18 glyph <see cref="Avalonia.Controls.Border"/>s are unique
    /// to the strip (the findings ListBox rows use 20×20), and each chip's letter
    /// (E / W / i) ties it to its severity. Expectations are DERIVED, never hardcoded:
    /// the glyph letter and brush come from the one <see cref="SeverityConverters"/> palette
    /// the XAML binds, and the count from the same <c>CountForSeverity</c> tally — so this
    /// fails the instant the strip drifts off the palette or miscounts. The demo-baseline
    /// totals (E 4 · W 3 · i 12 = 19) are additionally pinned as a literal, so a dataset or
    /// engine drift that silently changes the per-severity mix is also caught here.
    /// </summary>
    private static void AssertSeverityChipStrip(WorkspaceViewModel workspace, ViolationsSidebarView sidebar)
    {
        Assert.True(workspace.HasViolations, "the demo baseline must populate the chip strip");

        // The 18×18 glyph squares are unique to the chip strip (rows use 20×20) — exactly
        // one per severity, all rendered above the fold.
        var chipBorders = sidebar.GetVisualDescendants()
            .OfType<Avalonia.Controls.Border>()
            .Where(b => b.IsEffectivelyVisible && b.Width == 18 && b.Height == 18)
            .ToList();
        Assert.Equal(3, chipBorders.Count);

        // The demo baseline: Error 4 (3 nesting + 1 circular), Warning 3 (naming), Info 12
        // (empty-group) — literal pins so a per-severity mix drift is caught, paired with
        // the live CountForSeverity tally so the chip text can never diverge from the data.
        var expectedCounts = new Dictionary<RuleSeverity, int>
        {
            [RuleSeverity.Error] = 4,
            [RuleSeverity.Warning] = 3,
            [RuleSeverity.Info] = 12,
        };

        foreach (var severity in new[] { RuleSeverity.Error, RuleSeverity.Warning, RuleSeverity.Info })
        {
            // Locate THIS severity's chip by its redundant letter (E / W / i) — derived from
            // the one palette, the colorblind-safe channel the chip renders in the square.
            var glyph = Assert.IsType<string>(SeverityConverters.ToGlyph.Convert(
                severity, typeof(string), null, CultureInfo.InvariantCulture));
            var letterBlocks = chipBorders
                .Select(b => b.Child as Avalonia.Controls.TextBlock)
                .ToList();
            var letterBlock = Assert.Single(letterBlocks, t => t is not null && t.Text == glyph)!;
            var border = (Avalonia.Controls.Border)letterBlock.Parent!;

            // (1) the glyph square is the pinned ADR-010 palette color (parity with ToBrush).
            var expectedBrush = Assert.IsAssignableFrom<ISolidColorBrush>(
                SeverityConverters.ToBrush.Convert(
                    severity, typeof(IBrush), null, CultureInfo.InvariantCulture));
            var actualBrush = Assert.IsAssignableFrom<ISolidColorBrush>(border.Background);
            Assert.Equal(expectedBrush.Color, actualBrush.Color);

            // (2) the chip's count TextBlock — the sibling of the square inside the chip
            // StackPanel (the letter lives INSIDE the square, so exclude the square's
            // subtree) — reads the per-severity tally: the live CountForSeverity AND the
            // pinned demo-baseline literal must agree, and the rendered text must equal it.
            var chip = (Avalonia.Controls.StackPanel)border.Parent!;
            var countBlock = Assert.Single(chip.GetVisualDescendants()
                    .OfType<Avalonia.Controls.TextBlock>()
, t => t.IsEffectivelyVisible && !ReferenceEquals(t, letterBlock));

            var liveCount = Assert.IsType<string>(SeverityConverters.CountForSeverity.Convert(
                new object?[] { workspace.Violations, workspace.Violations.Count },
                typeof(string), severity, CultureInfo.InvariantCulture));
            Assert.Equal(expectedCounts[severity].ToString(CultureInfo.InvariantCulture), liveCount);
            Assert.Equal(liveCount, countBlock.Text);
        }
    }

    /// <summary>
    /// AP 2.5 (ADR-007): a selected demo USER at the 5-row MAXIMUM of the user display
    /// set — description, whenCreated, department, title, primaryGroupID. The demo
    /// dataset gives its users only department + title, so the subject is upserted in
    /// the EXACT live-LDAP shape (all five whitelisted attributes, whenCreated in
    /// LdapEntry's normalized invariant-UTC form) through the same public AddObject
    /// upsert the expand pipeline uses — the sentinel approach of ConnectionError:
    /// the state is staged, the rendering path is the real one (snapshot →
    /// DetailPanelModel.Build → DetailPanelView).
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    public async Task WorkspaceDetail(int width, int height)
    {
        var (window, shell) = ShowShell(Present, width, height);
        var workspace = await DriveToWorkspaceAsync(shell);
        var snapshot = workspace.Snapshot;
        Assert.NotNull(snapshot);

        Assert.True(snapshot.TryGetObject(DemoUserDn, out var demoUser));
        snapshot.AddObject(new AdObject
        {
            Dn = demoUser!.Dn,
            Kind = demoUser.Kind,
            Name = demoUser.Name,
            SamAccountName = demoUser.SamAccountName,
            Attributes = new Dictionary<string, string>(
                demoUser.Attributes, StringComparer.OrdinalIgnoreCase)
            {
                ["description"] = "Sales department staff account (screenshot sentinel).",
                ["whenCreated"] = "2024-03-18T09:30:00Z",
                ["primaryGroupID"] = "513",
            },
        });

        // Selection through the declared AP 2.5 seam (renderer-less workspace:
        // the public SelectedDn setter IS the click).
        workspace.SelectedDn = DemoUserDn;

        // Fixture soundness: the frame must show the 5-row maximum case in whitelist
        // declaration order (ADR-007 D4) — never the dataset's 2-row shape.
        var model = workspace.DetailPanel;
        Assert.NotNull(model);
        Assert.Equal(DetailPanelState.Loaded, model.State);
        Assert.Equal(AdObjectKind.User, model.Kind);
        Assert.Equal(
            new[] { "description", "whenCreated", "department", "title", "primaryGroupID" },
            model.Rows.Select(r => r.Label));

        CapturePng(window, "workspace-detail", width, height);
        window.Close();
    }

    /// <summary>
    /// AP 2.5 (ADR-007 D3): the NOT-LOADED panel state, staged the honest way — a
    /// group-rooted scope puts ONLY the group in Objects, so every member DN is a
    /// genuine frontier endpoint; selecting one renders the External badge, the DN
    /// verbatim, the "not loaded yet" hint, and zero attribute rows.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    public async Task WorkspaceDetailFrontier(int width, int height)
    {
        var (window, shell) = ShowShell(Present, width, height);
        var workspace = await DriveToWorkspaceAsync(
            shell, c => c.Name == "GG_Sales_Staff"); // 20 members, all out of scope
        var snapshot = workspace.Snapshot;
        Assert.NotNull(snapshot);

        var members = snapshot.GetMembers(workspace.RootDn);
        Assert.NotNull(members); // the group root itself is loaded by LoadScopeAsync
        Assert.NotEmpty(members);
        var frontierDn = members[0];
        Assert.False(
            snapshot.TryGetObject(frontierDn, out _),
            $"'{frontierDn}' is in Objects — not a frontier DN; the capture would lie");

        workspace.SelectedDn = frontierDn;

        // Fixture soundness: the frame must show the honest NotLoaded projection.
        var model = workspace.DetailPanel;
        Assert.NotNull(model);
        Assert.Equal(DetailPanelState.NotLoaded, model.State);
        Assert.Equal(AdObjectKind.External, model.Kind);
        Assert.Empty(model.Rows);

        CapturePng(window, "workspace-detail-frontier", width, height);
        window.Close();
    }

    // --- Settings window: Naming tab (AP 3.3 / S5) ----------------------------------------------

    /// <summary>The Naming-tab live-preview chip's success glyph — the ✓ a card shows when
    /// its <c>PreviewSample</c> matches its pattern (parity with
    /// <c>NamingPreviewConverter</c>'s Ok glyph; #45 / ADR-011 §4).</summary>
    private const string OkChipGlyph = "✓";

    /// <summary>
    /// AP 3.3 / S5 (ADR-011, spec "ui-checklist additions"): the modal
    /// <see cref="SettingsWindow"/> rendered STANDALONE — exactly the headless seam the spec
    /// pins (<c>ShowDialog</c> is headless-hostile; open-risk #3), so the fixture constructs
    /// <c>new SettingsWindow { DataContext = vm }</c>, calls <c>.Show()</c> (NOT
    /// <c>ShowDialog</c>), pumps the dispatcher, capture-and-discards the lagging compositor
    /// batch, then captures <c>settings-naming-{W}x{H}.png</c> at both checklist sizes for the
    /// ui-verifier to judge against the new section-B "Settings / rule editor" rows.
    ///
    /// <para>The VM is seeded from <see cref="RulesetLoader.LoadDefault"/> through the mirror
    /// (<see cref="SettingsViewModel.LoadFrom"/>) — the demo-mode-safe default, never a lab
    /// file. The Naming tab is selected so the cards (the three default <c>naming-*</c> rules)
    /// are the captured surface. Two of those cards are given a live <c>PreviewSample</c> so the
    /// frame evidences BOTH preview verdicts at once: the GG card matches its pattern
    /// (<c>GG_Vertrieb_Lesen</c> ⇒ a green ✓ "matches"), the DL card does NOT match its
    /// (<c>^DL_..._(RW|RO)$</c>) pattern (the same sample ⇒ a ✗ "would be flagged" chip in the
    /// rule's own severity color). Without two seeded samples the frame could show only an idle
    /// chip and silently lose the ✓/✗ evidence the checklist row demands.</para>
    ///
    /// <para>Beyond the PNG, <see cref="AssertNamingPreviewChip"/> pins fixture soundness: the
    /// two verdict chips are actually present and individually correct (glyph + palette color),
    /// DERIVED from the one <see cref="NamingPreviewConverter"/> seam the XAML binds (never a
    /// hardcoded hex) — so a static frame can never look plausible while the chip drifts off
    /// the verdict palette or collapses to idle. RED until
    /// <c>src/App/Views/SettingsWindow.axaml(.cs)</c>, the Naming tab, and
    /// <c>NamingRuleEditor.PreviewSample</c> exist.</para>
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    public void Settings_Naming(int width, int height)
    {
        var vm = SettingsViewModel.LoadFrom(RulesetLoader.LoadDefault());

        // The default ships three naming cards (naming-gg / naming-dl / naming-ug); the fixture
        // needs at least one match and one non-match in frame.
        Assert.True(vm.Naming.Count >= 2, "the default ruleset must seed >= 2 naming cards");
        var ggCard = vm.Naming.Single(r => r.Kind == AdObjectKind.GlobalGroup);
        var nonGgCard = vm.Naming.First(r => r.Kind != AdObjectKind.GlobalGroup);

        // One ✓ verdict and one ✗ verdict, live: the GG card's sample matches the GG pattern;
        // the same sample fed to a non-GG card (e.g. the DL_..._(RW|RO) pattern) does not.
        ggCard.PreviewSample = "GG_Vertrieb_Lesen";
        nonGgCard.PreviewSample = "GG_Vertrieb_Lesen";

        // Soundness BEFORE the window: the chosen samples really do straddle the verdict —
        // a match on the GG card and a non-match on the other — through the production
        // preview engine, so the fixture cannot silently capture two identical chips.
        Assert.Equal(
            NamingPreviewKind.Ok,
            NamingPreview.Evaluate(ggCard.Pattern, ggCard.PreviewSample).Kind);
        Assert.Equal(
            NamingPreviewKind.Violation,
            NamingPreview.Evaluate(nonGgCard.Pattern, nonGgCard.PreviewSample).Kind);

        var window = new SettingsWindow { DataContext = vm, Width = width, Height = height };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Bring the Naming tab to the front so its cards (the captured surface) are realized.
        SelectTab(window, "Naming");
        Dispatcher.UIThread.RunJobs();

        // The two verdict chips render correctly in frame — the soundness pin the PNG can't make.
        AssertNamingPreviewChip(window, ggCard, nonGgCard);

        CaptureWindowPng(window, "settings-naming", width, height);
        window.Close();
    }

    /// <summary>
    /// Fixture-soundness pin for the Naming-tab live preview (AP 3.3 / S5): the seeded ✓ and ✗
    /// cards each render a chip whose glyph AND palette color match what the ONE
    /// <see cref="NamingPreviewConverter"/> binding seam produces for that card's
    /// <c>(Pattern, PreviewSample, Severity)</c> — derived, never hardcoded, so this fails the
    /// instant the chip drifts off the verdict palette (the Ok green or the rule's own severity
    /// color) or collapses to idle. Located structurally by glyph + brush (the chips are not
    /// named controls), exactly the <see cref="AssertSeverityChipStrip"/> shape.
    /// </summary>
    private static void AssertNamingPreviewChip(
        SettingsWindow window, NamingRuleEditor okCard, NamingRuleEditor violationCard)
    {
        // The ✓ card: the converter must yield an Ok visual (green ✓) for the matching sample.
        var okVisual = PreviewVisual(okCard);
        Assert.Equal(NamingPreviewKind.Ok, okVisual.Kind);
        Assert.Equal(OkChipGlyph, okVisual.Glyph);
        AssertChipRendered(window, okVisual);

        // The ✗ card: a Violation visual in the rule's OWN severity glyph + palette color.
        var violationVisual = PreviewVisual(violationCard);
        Assert.Equal(NamingPreviewKind.Violation, violationVisual.Kind);
        var expectedGlyph = Assert.IsType<string>(SeverityConverters.ToGlyph.Convert(
            violationCard.Severity, typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Equal(expectedGlyph, violationVisual.Glyph);
        AssertChipRendered(window, violationVisual);

        // The two verdicts must be visually DISTINCT — a ✓ and a ✗ chip, not two of the same.
        var okColor = Assert.IsAssignableFrom<ISolidColorBrush>(okVisual.Brush).Color;
        var violationColor = Assert.IsAssignableFrom<ISolidColorBrush>(violationVisual.Brush).Color;
        Assert.True(
            okVisual.Glyph != violationVisual.Glyph || okColor != violationColor,
            "the ✓ and ✗ preview chips must be visually distinct (glyph or color)");
    }

    /// <summary>The chip descriptor the XAML binds for <paramref name="card"/>: the live
    /// <c>NamingPreviewConverter</c> over <c>[Pattern, PreviewSample]</c> with the card's
    /// severity as the parameter — the exact seam the Naming-tab chip uses.</summary>
    private static NamingPreviewVisual PreviewVisual(NamingRuleEditor card) =>
        Assert.IsType<NamingPreviewVisual>(NamingPreviewConverter.Instance.Convert(
            new object?[] { card.Pattern, card.PreviewSample },
            typeof(NamingPreviewVisual),
            card.Severity,
            CultureInfo.InvariantCulture));

    /// <summary>Asserts the realized visual tree contains a rendered chip carrying
    /// <paramref name="expected"/>'s glyph in its pinned brush color — a glyph
    /// <see cref="Avalonia.Controls.TextBlock"/> whose <c>Text</c> equals the expected glyph and
    /// whose own <c>Foreground</c> OR whose chip <c>Border</c> ancestor's <c>Background</c> is
    /// the expected palette color. Brush parity is derived from the converter output (never a
    /// hardcoded hex), so the chip can never silently drift off the verdict palette.</summary>
    private static void AssertChipRendered(SettingsWindow window, NamingPreviewVisual expected)
    {
        var expectedColor = Assert.IsAssignableFrom<ISolidColorBrush>(expected.Brush).Color;

        var glyphBlocks = window.GetVisualDescendants()
            .OfType<Avalonia.Controls.TextBlock>()
            .Where(t => t.IsEffectivelyVisible && t.Text == expected.Glyph)
            .ToList();
        Assert.NotEmpty(glyphBlocks);

        var painted = glyphBlocks.Any(t =>
            (t.Foreground is ISolidColorBrush fg && fg.Color == expectedColor)
            || t.GetVisualAncestors()
                .OfType<Avalonia.Controls.Border>()
                .Any(b => b.Background is ISolidColorBrush bg && bg.Color == expectedColor));

        Assert.True(
            painted,
            $"a '{expected.Glyph}' preview chip must render in the verdict color "
            + $"#{expectedColor.R:X2}{expectedColor.G:X2}{expectedColor.B:X2} "
            + "(parity with NamingPreviewConverter)");
    }

    // --- Settings window: Rules master grid (AP 3.3 / S6) -----------------------------------------

    /// <summary>
    /// AP 3.3 / S6 (ADR-011; spec "Final slice plan" S6 + "ui-checklist additions"): the
    /// modal <see cref="SettingsWindow"/>'s <b>Rules master grid</b> rendered standalone —
    /// the same headless seam <see cref="Settings_Naming"/> pins (<c>.Show()</c>, NOT
    /// <c>ShowDialog</c>; open-risk #3), capturing <c>settings-rules-{W}x{H}.png</c> at both
    /// checklist sizes for the ui-verifier to judge against the new section-B Rules-grid row.
    ///
    /// <para>The VM is seeded from <see cref="RulesetLoader.LoadDefault"/> through the mirror
    /// (<see cref="SettingsViewModel.LoadFrom"/>) — the demo-mode-safe default, never a lab
    /// file. The default's master grid spans every rule kind the <c>EnumerateRules</c> shape
    /// emits: the nesting matrix (error), the three <c>naming-*</c> rules (warning), circular
    /// (error) and empty-group (info) — so ALL THREE severities are evidenced in one frame
    /// AND the two SimpleRule cards (circular + empty-group) the spec wants in the Rules-tab
    /// capture are present.</para>
    ///
    /// <para>Beyond the PNG, <see cref="AssertRulesGridSeverityParity"/> pins fixture
    /// soundness — the <b>severity-parity assertion on the Rules grid</b> the checklist's
    /// <c>[T:SettingsScreenshotTests — severity parity]</c> tag demands: every
    /// <see cref="RuleRowEditor"/> renders, above the fold, its E/W/i glyph in the pinned
    /// <see cref="SeverityConverters"/> palette (#D13438 / #F7A30B / #4FA3E3), DERIVED from
    /// the live row severity (never a hardcoded hex), so a static frame can never look
    /// plausible while a row's severity selector drifts off the palette or the grid silently
    /// loses a row. <b>RED</b> until <c>src/App/Views/SettingsWindow.axaml(.cs)</c> and the
    /// Rules tab (the <c>EnumerateRules</c>-shaped grid binding <see cref="SettingsViewModel.Rules"/>)
    /// exist — today no grid row realizes, so the parity assertion finds no glyphs.</para>
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    public void Settings_Rules(int width, int height)
    {
        var vm = SettingsViewModel.LoadFrom(RulesetLoader.LoadDefault());

        // Soundness BEFORE the window: the default seeds the full EnumerateRules shape — the
        // nesting row, every naming rule, circular and empty-group — covering all three
        // severities (nesting=Error, naming=Warning, empty-group=Info) so the parity check
        // below has a row in each palette color to verify.
        Assert.Equal(
            vm.Naming.Count + 3, // nesting + each naming rule + circular + empty-group
            vm.Rules.Count);
        var severities = vm.Rules.Select(r => r.Severity).ToHashSet();
        Assert.Contains(RuleSeverity.Error, severities);
        Assert.Contains(RuleSeverity.Warning, severities);
        Assert.Contains(RuleSeverity.Info, severities);

        var window = new SettingsWindow { DataContext = vm, Width = width, Height = height };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Bring the Rules tab to the front so its master grid (the captured surface) is realized.
        SelectTab(window, "Rules");
        Dispatcher.UIThread.RunJobs();

        // The grid renders every rule's severity glyph in the pinned palette — the soundness
        // pin the PNG can't make (and the checklist's "severity parity" test tag).
        AssertRulesGridSeverityParity(window, vm);

        CaptureWindowPng(window, "settings-rules", width, height);
        window.Close();
    }

    /// <summary>
    /// Fixture-soundness pin for the Rules master grid (AP 3.3 / S6; checklist
    /// <c>[T:SettingsScreenshotTests — severity parity]</c>): every <see cref="RuleRowEditor"/>
    /// in <see cref="SettingsViewModel.Rules"/> renders its severity in the ONE pinned
    /// <see cref="SeverityConverters"/> palette — the E/W/i glyph AND its #D13438/#F7A30B/#4FA3E3
    /// square color, DERIVED from the live <see cref="RuleRowEditor.Severity"/> (never a hardcoded
    /// hex), exactly the <see cref="AssertSeverityChipStrip"/> shape. For each distinct severity
    /// present in the grid the realized Rules-tab content must contain at least one rendered glyph
    /// <see cref="Avalonia.Controls.TextBlock"/> whose <c>Text</c> equals that severity's glyph and
    /// whose own <c>Foreground</c> OR a <see cref="Avalonia.Controls.Border"/> ancestor's
    /// <c>Background</c> is the pinned palette color — so the grid can never silently drift off the
    /// palette, miscolor a row, or lose a severity entirely.
    /// </summary>
    private static void AssertRulesGridSeverityParity(SettingsWindow window, SettingsViewModel vm)
    {
        Assert.NotEmpty(vm.Rules);

        // The glyph TextBlocks rendered anywhere in the realized window. The Rules grid binds
        // its severity selector through the same SeverityConverters the sidebar chip strip does,
        // so each row contributes a glyph painted in that severity's palette color.
        var glyphBlocks = window.GetVisualDescendants()
            .OfType<Avalonia.Controls.TextBlock>()
            .Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text))
            .ToList();

        foreach (var severity in vm.Rules.Select(r => r.Severity).Distinct())
        {
            // Expectations DERIVED from the one palette the XAML binds — never hardcoded.
            var glyph = Assert.IsType<string>(SeverityConverters.ToGlyph.Convert(
                severity, typeof(string), null, CultureInfo.InvariantCulture));
            var expectedColor = Assert.IsAssignableFrom<ISolidColorBrush>(
                SeverityConverters.ToBrush.Convert(
                    severity, typeof(IBrush), null, CultureInfo.InvariantCulture)).Color;

            var painted = glyphBlocks.Any(t =>
                t.Text == glyph
                && ((t.Foreground is ISolidColorBrush fg && fg.Color == expectedColor)
                    || t.GetVisualAncestors()
                        .OfType<Avalonia.Controls.Border>()
                        .Any(b => b.Background is ISolidColorBrush bg && bg.Color == expectedColor)));

            Assert.True(
                painted,
                $"the Rules grid must render a '{glyph}' severity glyph in the pinned palette color "
                + $"#{expectedColor.R:X2}{expectedColor.G:X2}{expectedColor.B:X2} "
                + $"for severity {severity} (parity with SeverityConverters)");
        }
    }

    // --- Settings window: Matrix tab (AP 3.3 / S6) ------------------------------------------------

    /// <summary>
    /// AP 3.3 / S6 (ADR-011; spec "Matrix editor" + "ui-checklist additions"): the modal
    /// <see cref="SettingsWindow"/>'s <b>nesting-matrix tab</b> rendered standalone — same
    /// headless seam (<c>.Show()</c>, NOT <c>ShowDialog</c>), capturing
    /// <c>settings-matrix-{W}x{H}.png</c> at both checklist sizes for the ui-verifier to judge
    /// the 3×6 grid (3 parent rows GG/DL/UG × 6 member cols User/Computer/GG/DL/UG/External, no
    /// OU), the kind-badge headers, the per-cell allow/deny/error/warning/info chips, and the
    /// Unlisted-fallback + rule-wide-default-severity controls (the AGUDLP lane readable).
    ///
    /// <para>The VM is seeded from <see cref="RulesetLoader.LoadDefault"/> through the mirror —
    /// the default ships the full strict-AGDLP matrix (every cell present), so the captured grid
    /// is the canonical 18-cell shape, including the AGUDLP lane (DL←UG allow, UG←GG allow) the
    /// checklist wants legible.</para>
    ///
    /// <para><see cref="AssertMatrixGridCells"/> pins fixture soundness the PNG can't: the
    /// realized window contains a rendered, addressable cell surface for all 18 parent×member
    /// pairings the <see cref="NestingEditor"/> grid spans — so a static frame can never look
    /// plausible while the matrix grid is missing or short rows/columns. <b>RED</b> until
    /// <c>src/App/Views/SettingsWindow.axaml(.cs)</c> and the Matrix tab (the 3×6 grid binding
    /// <see cref="NestingEditor.Cells"/>) exist — today no matrix cell control realizes.</para>
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    public void Settings_Matrix(int width, int height)
    {
        var vm = SettingsViewModel.LoadFrom(RulesetLoader.LoadDefault());

        // Soundness BEFORE the window: the default seeds the dense 3×6 grid (18 cells, every one
        // Present) so the captured matrix is the full strict-AGDLP shape — never a sparse stub.
        Assert.Equal(
            NestingEditor.ParentKinds.Count * NestingEditor.MemberKinds.Count,
            vm.Nesting.Cells.Count);
        Assert.All(vm.Nesting.Cells, c => Assert.True(
            c.Present, $"default cell {c.Parent}<-{c.Member} must load Present for the full matrix capture"));

        // The two SimpleRule rules the spec wants surfaced alongside the matrix capture exist and
        // are enabled in the default (circular=error, empty-group=info) — the Rules-tab cards.
        Assert.True(vm.Circular.Enabled);
        Assert.True(vm.EmptyGroup.Enabled);

        var window = new SettingsWindow { DataContext = vm, Width = width, Height = height };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Bring the Matrix tab to the front so its 3×6 grid (the captured surface) is realized.
        SelectTab(window, "Matrix");
        Dispatcher.UIThread.RunJobs();

        // The 18-cell grid actually realized a control per cell — the soundness pin the PNG can't make.
        AssertMatrixGridCells(window, vm);

        CaptureWindowPng(window, "settings-matrix", width, height);
        window.Close();
    }

    /// <summary>
    /// Fixture-soundness pin for the nesting-matrix tab (AP 3.3 / S6): the realized window
    /// surfaces a rendered, bound editor control for EVERY one of the 18
    /// parent×member cells the <see cref="NestingEditor"/> grid spans — located via the
    /// <see cref="NestingCellEditor"/> instances themselves, which the spec binds as each cell's
    /// <c>DataContext</c> (a compact per-cell <see cref="Avalonia.Controls.ComboBox"/> over
    /// <see cref="NestingCellEditor.Choice"/>). Every cell editor must back at least one realized,
    /// effectively-visible <see cref="Avalonia.Controls.Control"/> in the visual tree — so the grid
    /// can never silently drop a row or column and still look plausible in a static frame.
    /// </summary>
    private static void AssertMatrixGridCells(SettingsWindow window, SettingsViewModel vm)
    {
        Assert.Equal(18, vm.Nesting.Cells.Count); // 3 parents × 6 members, the canonical shape

        var boundDataContexts = window.GetVisualDescendants()
            .OfType<Avalonia.Controls.Control>()
            .Where(c => c.IsEffectivelyVisible)
            .Select(c => c.DataContext)
            .ToHashSet();

        foreach (var cell in vm.Nesting.Cells)
        {
            Assert.True(
                boundDataContexts.Contains(cell),
                $"the matrix grid must realize a bound cell control for {cell.Parent}<-{cell.Member}");
        }
    }

    /// <summary>Brings the <see cref="Avalonia.Controls.TabItem"/> whose header text contains
    /// <paramref name="header"/> to the front of the window's <see cref="Avalonia.Controls.TabControl"/>,
    /// so its content is realized for capture. Header match is substring + ordinal-ignore-case so
    /// a "Naming" tab still resolves if the implementer labels it "Naming rules".</summary>
    private static void SelectTab(SettingsWindow window, string header)
    {
        var tabs = Assert.Single(window.GetVisualDescendants().OfType<Avalonia.Controls.TabControl>());
        var item = Assert.Single(
            tabs.GetVisualDescendants().OfType<Avalonia.Controls.TabItem>(),
            t => (t.Header?.ToString() ?? string.Empty)
                .Contains(header, StringComparison.OrdinalIgnoreCase));
        tabs.SelectedItem = item;
    }

    /// <summary>The capture core for a standalone settings <see cref="Window"/> — same
    /// capture-and-discard + real-rasterization gate as <see cref="CapturePng"/> for
    /// <see cref="MainWindow"/>, but typed to <see cref="Avalonia.Controls.Window"/> so the
    /// settings fixtures share it.</summary>
    private static void CaptureWindowPng(Avalonia.Controls.Window window, string name, int width, int height)
    {
        Dispatcher.UIThread.RunJobs();

        // Same lagging-compositor rule as CapturePng: the first capture after a mutation
        // (here: Show + tab select) returns the previous frame — discard it, then capture.
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

    // --- capture core ---------------------------------------------------------------------------

    /// <summary>
    /// Flush pending jobs, capture the rendered frame, prove it is a real rasterization
    /// of the requested size (not a stub or a blank), and write the PNG.
    /// </summary>
    private static void CapturePng(MainWindow window, string name, int width, int height)
    {
        Dispatcher.UIThread.RunJobs();

        // The headless compositor renders ONE committed batch per render-timer tick, so
        // the first capture after a state mutation returns the PREVIOUS frame (verified
        // empirically: single-capture made connection-error byte-identical to
        // connection-idle). Capture-and-discard flushes the pending batch; the second
        // capture is current. Deterministic — no sleeps, no retries.
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
    /// Non-trivial-frame gate: sample a 32x32 grid and require at least two distinct
    /// pixel values. Robust by construction — every shell state renders text on a
    /// background, while a failed capture is uniformly blank. Deliberately NOT a
    /// file-size threshold: PNG compression of large near-empty frames sits exactly
    /// where a byte cutoff turns flaky.
    /// </summary>
    private static void AssertSampledPixelsNonUniform(WriteableBitmap frame, string name)
    {
        using var fb = frame.Lock();
        Assert.Equal(32, fb.Format.BitsPerPixel); // sampling below reads 4-byte pixels

        var first = Marshal.ReadInt32(fb.Address);
        var stepX = Math.Max(1, fb.Size.Width / 32);
        var stepY = Math.Max(1, fb.Size.Height / 32);
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

        Assert.Fail($"'{name}': every sampled pixel is identical — a blank frame, not the rendered shell");
    }

    // --- shell driving ----------------------------------------------------------------------------

    /// <summary>Real DemoProvider behind the factory: these frames are the demo-mode truth.</summary>
    private static (MainWindow Window, ShellViewModel Shell) ShowShell(
        WebView2RuntimeStatus status, int width, int height)
    {
        var shell = new ShellViewModel(
            _ => new DemoProvider(), new StartupOptions(Demo: false), status);

        // Size BEFORE Show so every layout pass — including ListBox virtualization —
        // happens against the final viewport.
        var window = new MainWindow { DataContext = shell, Width = width, Height = height };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, shell);
    }

    private static async Task<RootPickerViewModel> ConnectIntoPickerAsync(ShellViewModel shell)
    {
        var connect = Assert.IsType<ConnectionViewModel>(shell.CurrentStep);
        await connect.ConnectDemoCommand.ExecuteAsync(null);
        var picker = Assert.IsType<RootPickerViewModel>(shell.CurrentStep);
        await picker.LoadCandidates;
        return picker;
    }

    /// <summary>Connect → pick → load, landing on the SETTLED workspace, which is
    /// returned for the AP 2.5 captures. <paramref name="pickRoot"/> chooses the root
    /// candidate; default: the first OU of the demo dataset — deterministic and
    /// representative.</summary>
    private static async Task<WorkspaceViewModel> DriveToWorkspaceAsync(
        ShellViewModel shell, Func<AdObject, bool>? pickRoot = null)
    {
        var picker = await ConnectIntoPickerAsync(shell);
        Dispatcher.UIThread.RunJobs();

        picker.SelectedCandidate = picker.Candidates
            .First(pickRoot ?? (c => c.Kind == AdObjectKind.OrganizationalUnit));
        picker.LoadRootCommand.Execute(null);
        var workspace = Assert.IsType<WorkspaceViewModel>(shell.CurrentStep);

        // S6: the workspace frames must capture the settled post-load state — never a
        // transient progress bar. Capture-and-discard (CapturePng) only fixes compositor
        // lag, not load timing, so the load itself is awaited here; the real
        // DemoProvider behind the shell makes this the genuine demo-mode truth.
        await workspace.Initialization;
        return workspace;
    }
}
