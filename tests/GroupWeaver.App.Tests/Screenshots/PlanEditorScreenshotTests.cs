using System.Runtime.InteropServices;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using GroupWeaver.App.Startup;
using GroupWeaver.App.ViewModels;
using GroupWeaver.App.Views;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Plan;
using GroupWeaver.Core.Rules;
using GroupWeaver.Providers;

using Xunit;

namespace GroupWeaver.App.Tests.Screenshots;

/// <summary>
/// The ui-verifier fixture for AP 4.2.3 (ADR-014; CLAUDE.md DoD step 2b): renders the Plan
/// Mode EDITOR through the REAL pipeline — real <see cref="DemoProvider"/> shell driven into
/// Plan Mode via the workspace's <c>Design plan</c> command, a representative plan SEEDED on
/// the resulting <see cref="PlanViewModel"/> through its PUBLIC editor command/model surface,
/// <see cref="PlanViewModel.RevalidateAsync"/> run, then <see cref="PlanView"/> rendered via
/// <see cref="MainWindow"/>'s <c>PlanViewModel</c> DataTemplate and rasterized on the headless
/// platform — writing <c>artifacts/ui/plan-editor-&lt;W&gt;x&lt;H&gt;.png</c> (gitignored) at
/// both checklist sizes, 1280x720 and 1920x1080. The ui-verifier judges these PNGs against the
/// new <c>docs/ui-checklist.md</c> "Plan mode editor (AP 4.2.3)" subsection; the assertions
/// here only guarantee the fixture itself is sound (window sized, frame rendered, the editor
/// panel's add-object form / Objects list rows / Memberships rows / findings list all realized
/// and effectively-visible) — visual judgment is the verifier's job, not xUnit's.
///
/// <para><b>The judged surface is the EDITOR PANEL, not the graph.</b> The headless plan has no
/// real WebView (no renderer factory reaches the plan: <see cref="ShowShell"/> uses the 3-arg
/// shell ctor, so <see cref="PlanViewModel.GraphRenderer"/> is null), so GraphHost shows its
/// placeholder. The right-hand editor panel (the add-object form, the membership form, the
/// Objects list, the Memberships list, and the findings list) is what the capture and the
/// soundness assertions target.</para>
///
/// <para>The seed is deliberately AGDLP-meaningful so the findings list is NON-EMPTY: two
/// groups (a DL and a GG) plus a user, with the user authored DIRECTLY into the DL — the
/// canonical AGDLP violation (an account belongs in a global group routed G→DL, never directly
/// in a DL) — plus the conformant G→DL membership. The single nesting Error is the one finding
/// the frame must evidence; its existence is asserted (by RuleId/severity, never a hardcoded
/// message) before capture so a blank findings list can never slip through.</para>
///
/// <para><b>RED until AP 4.2.3</b> lands the <see cref="PlanViewModel"/> editor surface and the
/// editor layout in <c>PlanView.axaml</c> (the add-object form controls and the Objects /
/// Memberships lists do not exist yet, so the soundness assertions find nothing to realize and
/// the assembly does not compile against the missing VM members).</para>
/// </summary>
public sealed class PlanEditorScreenshotTests
{
    private static readonly WebView2RuntimeStatus Present = new(IsInstalled: true, Version: "x");

    /// <summary>The demo dataset's first OU — the root the workspace loads before the
    /// Ist→Plan switch (the plan's base OU = the workspace root DN).</summary>
    private static readonly System.Func<AdObject, bool> PickFirstOu =
        c => c.Kind == AdObjectKind.OrganizationalUnit;

    /// <summary><c>artifacts/ui</c> under the repo root, created on first use (the same
    /// gitignored sink <see cref="ShellScreenshotTests"/> writes to — copied here rather than
    /// making that fixture's private helper public).</summary>
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
    /// The Plan Mode editor rendered with a representative seeded plan: the add-object form, the
    /// Objects list (one row per seeded node), the Memberships list, and a NON-EMPTY findings
    /// list (the one AGDLP nesting Error). Driven the production way — the real DemoProvider
    /// shell into a workspace, then the workspace's <c>Design plan</c> command into Plan Mode,
    /// then the plan seeded through the public editor commands — so the frame is the demo-mode
    /// truth the ui-verifier judges. Captured at both checklist sizes.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    public async Task PlanEditor(int width, int height)
    {
        var (window, shell) = ShowShell(Present, width, height);
        var plan = await DriveToSeededPlanAsync(shell);

        // Evidence the friendly "Domain-local group" label in the kind combo (the ui-verifier's
        // [S:plan-editor] kind-selector row): seeding adds the user LAST, so the closed combo would
        // otherwise show "User" and never exercise the spaced GROUP label the verifier FAILed on.
        plan.NewObjectKind = PlanCreatableKind.DomainLocalGroup;
        Dispatcher.UIThread.RunJobs();

        // The PlanView actually rendered (the MainWindow PlanViewModel DataTemplate realized it).
        var planView = Assert.Single(window.GetVisualDescendants().OfType<PlanView>());
        Assert.True(planView.IsEffectivelyVisible, "the plan editor view must be rendered");

        // Fixture soundness: the editor panel's four surfaces are realized + effectively-visible.
        AssertEditorPanelRealized(planView, plan);

        // Fixture soundness: the closed kind combo's selection renders the FRIENDLY spaced label,
        // not the raw enum name — the exact thing the captured frame must evidence.
        AssertKindComboShowsFriendlyLabel(planView, PlanCreatableKind.DomainLocalGroup);

        CapturePng(window, "plan-editor", width, height);
        window.Close();
    }

    /// <summary>
    /// Fixture-soundness pin for the AP 4.2.3 editor panel: the realized <see cref="PlanView"/>
    /// surfaces (a) the add-object form controls — the kind ComboBox over the four
    /// <see cref="PlanCreatableKind"/> values, the Name TextBox, and the Add button; (b) the
    /// add-membership parent/child combos + the Add-member button; (c) one Objects-list row per
    /// seeded node (located by the row's <see cref="PlanNodeRowModel"/> DataContext, so the list
    /// can never silently drop a node and still look plausible); (d) one Memberships row per
    /// seeded edge (by its <see cref="PlanEdgeRowModel"/> DataContext); and (e) the findings list
    /// with the seeded AGDLP finding's row realized (by its <see cref="ViolationRowModel"/>
    /// DataContext). Everything is located STRUCTURALLY (DataContext / Name / content) exactly as
    /// <see cref="ShellScreenshotTests"/> does — the editor panel is the judged surface, never the
    /// graph (GraphHost shows its placeholder in headless).
    /// </summary>
    private static void AssertEditorPanelRealized(PlanView planView, PlanViewModel plan)
    {
        // Every bound, effectively-visible control's DataContext in the panel's subtree — the
        // same "boundDataContexts" structural locator ShellScreenshotTests uses for list rows.
        var boundDataContexts = planView.GetVisualDescendants()
            .OfType<Control>()
            .Where(c => c.IsEffectivelyVisible)
            .Select(c => c.DataContext)
            .ToHashSet();

        // (a) Add-object form: a ComboBox over the four PlanCreatableKind values, a Name TextBox,
        //     and the Add button (located by Name, mirroring the BackButton lookup the AP 4.2.2
        //     view already names).
        var kindCombo = Assert.Single(
            planView.GetVisualDescendants().OfType<ComboBox>(),
            cb => cb.IsEffectivelyVisible && cb.Items.OfType<PlanCreatableKind>().Count() == 4);
        Assert.True(kindCombo.IsEffectivelyVisible, "the add-object kind combo must be realized");

        var addObjectButton = Assert.Single(
            planView.GetVisualDescendants().OfType<Button>(), b => b.Name == "AddObjectButton");
        Assert.True(addObjectButton.IsEffectivelyVisible, "the Add (object) button must be realized");

        // A name TextBox is realized (the add-object name box). At least one visible TextBox.
        Assert.Contains(
            planView.GetVisualDescendants().OfType<TextBox>(), t => t.IsEffectivelyVisible);

        // (b) Add-membership: the Add-member button is realized (the parent/child combos bind
        //     GroupNodes/Nodes — proven realized by the Objects rows below).
        var addMemberButton = Assert.Single(
            planView.GetVisualDescendants().OfType<Button>(), b => b.Name == "AddMemberButton");
        Assert.True(addMemberButton.IsEffectivelyVisible, "the Add member button must be realized");

        // (c) The Objects list: one realized, bound row per seeded node.
        Assert.NotEmpty(plan.Nodes);
        foreach (var node in plan.Nodes)
        {
            Assert.True(
                boundDataContexts.Contains(node),
                $"the Objects list must realize a bound row for node '{node.Name}'");
        }

        // (d) The Memberships list: one realized, bound row per seeded edge.
        Assert.NotEmpty(plan.Memberships);
        foreach (var edge in plan.Memberships)
        {
            Assert.True(
                boundDataContexts.Contains(edge),
                $"the Memberships list must realize a bound row for '{edge.Display}'");
        }

        // (e) The findings list: NON-EMPTY, and every finding row realized + bound.
        Assert.True(plan.HasViolations, "the seeded plan must produce a non-empty findings list");
        Assert.NotEmpty(plan.Violations);
        foreach (var violation in plan.Violations)
        {
            Assert.True(
                boundDataContexts.Contains(violation),
                $"the findings list must realize a bound row for the finding on '{violation.SubjectName}'");
        }
    }

    /// <summary>
    /// Fixture-soundness pin for the AP 4.2.3 kind-selector legibility fix (the row the ui-verifier
    /// FAILed): with the kind selector set to <paramref name="selected"/>, the realized add-object
    /// kind ComboBox renders the FRIENDLY spaced label (e.g. "Domain-local group"), never the raw
    /// enum name ("DomainLocalGroup"). Located STRUCTURALLY exactly as <see cref="AssertEditorPanelRealized"/>
    /// finds it (the unique effectively-visible ComboBox over the four <see cref="PlanCreatableKind"/>
    /// values), then: (a) the production mapping itself yields the friendly label, and (b) a
    /// realized, effectively-visible <see cref="TextBlock"/> in the combo's subtree carries that
    /// exact text — proof the closed combo's selection renders through the <c>ItemTemplate</c>.
    /// </summary>
    private static void AssertKindComboShowsFriendlyLabel(
        PlanView planView, PlanCreatableKind selected)
    {
        var friendly = PlanCreatableKindConverters.FriendlyLabel(selected);
        Assert.Contains(' ', friendly); // a spaced GROUP label, not the raw camel-case enum name

        var kindCombo = Assert.Single(
            planView.GetVisualDescendants().OfType<ComboBox>(),
            cb => cb.IsEffectivelyVisible && cb.Items.OfType<PlanCreatableKind>().Count() == 4);
        Assert.Equal(selected, Assert.IsType<PlanCreatableKind>(kindCombo.SelectedItem));

        // The closed combo renders its selection through the same ItemTemplate → a realized,
        // effectively-visible TextBlock carrying the friendly label text (never the enum name).
        Assert.Contains(
            kindCombo.GetVisualDescendants().OfType<TextBlock>(),
            t => t.IsEffectivelyVisible && t.Text == friendly);
        Assert.DoesNotContain(
            planView.GetVisualDescendants().OfType<TextBlock>(),
            t => t.IsEffectivelyVisible && t.Text == selected.ToString());
    }

    // --- plan seeding (through the PUBLIC editor command/model surface) ----------------------------

    /// <summary>
    /// Connect (demo) → pick the first OU → load → switch into Plan Mode via the workspace's
    /// <c>Design plan</c> command (the same path <see cref="PlanModeTests"/>'
    /// <c>WorkspaceDesignPlanCommand</c> test proves), then SEED a representative plan on the
    /// resulting <see cref="PlanViewModel"/> through its public editor commands: a DL, a GG, a
    /// user; the user authored DIRECTLY into the DL (the AGDLP nesting Error → the one finding)
    /// and the conformant GG→… wait: the GG is a member of the DL (the G→DL lane, allowed). Two
    /// memberships, three nodes, exactly one finding. Revalidates and returns the settled plan VM.
    /// </summary>
    private static async Task<PlanViewModel> DriveToSeededPlanAsync(ShellViewModel shell)
    {
        // Connect → pick → load the workspace (the demo-mode truth).
        var connect = Assert.IsType<ConnectionViewModel>(shell.CurrentStep);
        await connect.ConnectDemoCommand.ExecuteAsync(null);
        var picker = Assert.IsType<RootPickerViewModel>(shell.CurrentStep);
        await picker.LoadCandidates;
        picker.SelectedCandidate = picker.Candidates.First(PickFirstOu);
        picker.LoadRootCommand.Execute(null);
        var workspace = Assert.IsType<WorkspaceViewModel>(shell.CurrentStep);
        await workspace.Initialization;

        // Switch into Plan Mode via the workspace command (the production seam — proves the
        // callback is wired, not a direct OnDesignPlan call).
        Assert.True(workspace.DesignPlanCommand.CanExecute(null), "Design plan must be armed");
        workspace.DesignPlanCommand.Execute(null);
        var plan = Assert.IsType<PlanViewModel>(shell.CurrentStep);

        // Seed through the public editor command surface: a DL, a GG, a user.
        var dlDn = await AddNodeAsync(plan, PlanCreatableKind.DomainLocalGroup, "DL_FS_RW");
        var ggDn = await AddNodeAsync(plan, PlanCreatableKind.GlobalGroup, "GG_FS_Users");
        var userDn = await AddNodeAsync(plan, PlanCreatableKind.User, "Anna Acker", "aacker");

        // Membership 1 (conformant G→DL lane, allowed): GG is a member of the DL.
        await AddMemberAsync(plan, dlDn, ggDn);
        // Membership 2 (the AGDLP violation): the user is a DIRECT member of the DL → nesting Error.
        await AddMemberAsync(plan, dlDn, userDn);

        await plan.RevalidateAsync();

        // The findings list is non-empty: exactly the one nesting finding (verified by RuleId,
        // never a hardcoded message — the canonical "user directly in a DL" AGDLP Error).
        var finding = Assert.Single(plan.Report.Violations, v => v.RuleId == RuleIds.Nesting);
        Assert.Equal(RuleSeverity.Error, finding.Severity);
        Assert.True(plan.HasViolations);

        return plan;
    }

    /// <summary>Authors a node through the add-object COMMAND (the production seam) and returns
    /// its DN — sets the form (kind + name + optional SAM), executes, asserts no EditError.</summary>
    private static async Task<string> AddNodeAsync(
        PlanViewModel plan, PlanCreatableKind kind, string name, string? sam = null)
    {
        plan.NewObjectKind = kind;
        plan.NewObjectName = name;
        plan.NewObjectSam = sam ?? string.Empty;
        await plan.AddObjectCommand.ExecuteAsync(null);
        Assert.Null(plan.EditError);
        return plan.Plan.FormDn(name);
    }

    /// <summary>Authors a membership through the add-member COMMAND: select the parent row in
    /// GroupNodes and the child row in Nodes (the combos bind THOSE instances), then execute.</summary>
    private static async Task AddMemberAsync(PlanViewModel plan, string parentDn, string childDn)
    {
        plan.MemberParentRow = Assert.Single(
            plan.GroupNodes, r => Dn.Comparer.Equals(r.Dn, parentDn));
        plan.MemberChildRow = Assert.Single(
            plan.Nodes, r => Dn.Comparer.Equals(r.Dn, childDn));
        Assert.True(plan.AddMemberCommand.CanExecute(null));
        await plan.AddMemberCommand.ExecuteAsync(null);
    }

    // --- capture core (copied from ShellScreenshotTests — its helpers are private) ----------------

    /// <summary>Real DemoProvider behind the factory; NO renderer factory (the 3-arg ctor), so
    /// the plan step's renderer is null and GraphHost shows its placeholder — the editor panel is
    /// the judged surface. WebView2 forced PRESENT (never the live registry — that would make the
    /// banner machine-dependent).</summary>
    private static (MainWindow Window, ShellViewModel Shell) ShowShell(
        WebView2RuntimeStatus status, int width, int height)
    {
        var shell = new ShellViewModel(
            _ => new DemoProvider(), new StartupOptions(Demo: false), status);

        // Size BEFORE Show so every layout pass — including ListBox virtualization — happens
        // against the final viewport.
        var window = new MainWindow { DataContext = shell, Width = width, Height = height };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, shell);
    }

    /// <summary>
    /// Flush pending jobs, capture the rendered frame, prove it is a real rasterization of the
    /// requested size (not a stub or a blank), and write the PNG. Same capture-and-discard +
    /// real-rasterization gate as <see cref="ShellScreenshotTests"/> (the headless compositor
    /// renders one committed batch per tick, so the first capture after a mutation returns the
    /// previous frame — discard it, then capture; deterministic, no sleeps).
    /// </summary>
    private static void CapturePng(MainWindow window, string name, int width, int height)
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
    /// (copied from <see cref="ShellScreenshotTests"/> — its helper is private). Robust by
    /// construction — the editor panel renders text on a background, while a failed capture is
    /// uniformly blank.
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

        Assert.Fail($"'{name}': every sampled pixel is identical — a blank frame, not the rendered editor");
    }
}
