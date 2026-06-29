using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;

using GroupWeaver.App.Audit;
using GroupWeaver.App.Settings;
using GroupWeaver.App.Startup;
using GroupWeaver.App.ViewModels;
using GroupWeaver.App.Views;
using GroupWeaver.Core.Audit;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Rules;
using GroupWeaver.Providers;

using Xunit;

namespace GroupWeaver.App.Tests.Screenshots;

/// <summary>
/// ADR-032 D4 (#190) ui-verifier fixture: drives the demo workspace into the Audit step, PRE-SEEDS a
/// prior saved <see cref="AuditRun"/> into a temp-dir <see cref="AuditRunStore"/> (injected at shell
/// construction — never real <c>%APPDATA%</c>), then runs the Compare-to-previous-run command so the
/// captured frame shows a POPULATED run-history compare card: the four drift bucket tiles with
/// non-zero counts, the <b>unchecked-honesty banner</b> (the prior run's hidden finding sits under a
/// live UNCHECKED parent, so it banks as Now-unchecked, not Fixed), and the <b>ruleset-mismatch
/// banner</b> (the prior run carries a deliberately different <see cref="AuditRun.RulesetHash"/>).
///
/// <para>Captures BOTH theme variants via the production ToggleThemeCommand seam, writing
/// artifacts/ui/wp-audit-compare-{dark,light}.png for the ui-verifier to judge against
/// docs/ui-checklist.md §Audit. PRODUCES the screenshots; it does NOT pixel-assert the banners.
/// Demo-mode data only; restores Dark on exit.</para>
/// </summary>
public sealed class AuditRunCompareScreenshotTests
{
    private static readonly WebView2RuntimeStatus Present = new(IsInstalled: true, Version: "x");

    private static readonly Lazy<string> ArtifactsUiDir = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GroupWeaver.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Directory.CreateDirectory(Path.Combine(dir!.FullName, "artifacts", "ui")).FullName;
    });

    [AvaloniaFact(Timeout = 60_000)]
    public async Task AuditCompare_PopulatedCard_BothVariants()
    {
        // Inject a temp-dir run store at the composition root (never real %APPDATA%, per #124).
        var runsBase = Directory.CreateTempSubdirectory("groupweaver-audit-compare-runs-").FullName;
        var runStore = new AuditRunStore(runsBase);

        // A taller-than-default viewport so the full compare card (buttons + context label + the four
        // bucket tiles + BOTH banners) fits on-screen once scrolled to the bottom of the audit page.
        var (window, shell) = ShowShell(1280, 960, runStore);
        var workspace = await DriveToWorkspaceAsync(shell);
        Dispatcher.UIThread.RunJobs();

        // Into the Audit step. The shell arms the run-store seam (audit.UseRunStore(_auditRunStore)).
        shell.OnAudit(workspace);
        var audit = Assert.IsType<AuditViewModel>(shell.CurrentStep);
        Dispatcher.UIThread.RunJobs();
        Assert.True(audit.CanCompare, "the run-store seam must be armed so the compare card renders");

        // The live demo report carries unchecked DNs (the two ignored builtin member DNs). Pick one as
        // the prior run's finding subject so that finding banks as Now-unchecked (under a live-unchecked
        // parent), NEVER Fixed — the honesty banner's reason to exist.
        var liveUnchecked = workspace.Report.UncheckedDns;
        Assert.NotEmpty(liveUnchecked);
        var uncheckedSubject = liveUnchecked[0];

        // A live finding identity to make one bucket Still-open (the same (RuleId, PrimaryDn)).
        var liveFinding = workspace.Report.Violations.First();

        // Pre-seed the PRIOR run for THIS scope, with a DIFFERENT ruleset hash (=> mismatch banner) and
        // three findings: one Still-open (matches a live finding), one Fixed (checked-area, absent live),
        // one Now-unchecked (subject under the live-unchecked parent).
        var prior = new AuditRun(
            AuditRun.CurrentSchemaVersion,
            new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero),
            audit.RootDn,
            "demo · prior run",
            "Strict AGDLP (last quarter)",
            "0000000000000000000000000000000000000000000000000000000000000000", // != the live ruleset hash
            new AuditSummary(70, "Fair", 1, 1, 1, 0, 3, 6, true, new System.Collections.Generic.Dictionary<string, int>()),
            new[]
            {
                new AuditRunFinding(
                    liveFinding.RuleId, liveFinding.Severity, liveFinding.PrimaryDn,
                    liveFinding.Dns.ToArray(), liveFinding.Message), // -> Still-open
                new AuditRunFinding(
                    "naming-gg", RuleSeverity.Warning, "CN=GG_RenamedSinceLastRun,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab",
                    new[] { "CN=GG_RenamedSinceLastRun,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab" },
                    "Off-convention name (fixed since last run)"), // -> Fixed (checked area)
                new AuditRunFinding(
                    RuleIds.EmptyGroup, RuleSeverity.Info, uncheckedSubject,
                    new[] { uncheckedSubject },
                    "Empty group under an area not expanded this run"), // -> Now-unchecked
            },
            Array.Empty<string>());
        runStore.Save(prior);

        // Run the compare: it diffs the live findings against the most recent prior run for this root.
        audit.CompareToPreviousRunCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var comparison = audit.Comparison;
        Assert.NotNull(comparison);
        Assert.True(comparison!.HasPreviousRun, "a prior run was seeded for this scope");
        // The four buckets must all be exercised so every tile shows a meaningful (and at least one
        // non-zero) count for the ui-verifier.
        Assert.True(comparison.NewCount > 0, "the live demo scope has findings absent from the small prior run");
        Assert.True(comparison.HasStillOpen, "the seeded match must bank Still-open");
        Assert.True(comparison.HasFixed, "the checked-area prior-only finding must bank Fixed");
        Assert.True(comparison.HasNowUnchecked, "the finding under a live-unchecked parent must bank Now-unchecked");
        // The two honesty banners must fire.
        Assert.True(comparison.UncheckedPresent, "the live run has unchecked areas -> the honesty banner shows");
        Assert.True(comparison.RulesetMismatch, "the prior run's differing hash -> the mismatch banner shows");

        // The compare card is the last row of the audit page (inside the page ScrollViewer), below the
        // 720px fold — scroll it into view so the captured frame actually shows the bucket tiles + banners.
        ScrollCompareCardIntoView(window);

        Assert.False(shell.IsLightTheme);
        Settle(window);
        Capture(window, "wp-audit-compare-dark", 1280, 960);

        shell.ToggleThemeCommand.Execute(null);
        Assert.True(shell.IsLightTheme);
        Settle(window);
        Capture(window, "wp-audit-compare-light", 1280, 960);

        shell.ToggleThemeCommand.Execute(null);
        Settle(window);
        shell.Dispose();
        window.Close();
    }

    /// <summary>Scrolls the audit page's outer ScrollViewer to its END so the captured viewport shows the
    /// full run-history compare card at the bottom of the page — the four bucket tiles AND the
    /// honesty/mismatch banners (which render below the tiles). The OUTER (page) ScrollViewer is the
    /// FIRST visual descendant; the findings ListBox has its own inner one further down.</summary>
    private static void ScrollCompareCardIntoView(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        var pageScroll = window.GetVisualDescendants().OfType<ScrollViewer>().First();
        pageScroll.ScrollToEnd();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Settle(Avalonia.Controls.Window window)
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame()?.Dispose();
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static (MainWindow Window, ShellViewModel Shell) ShowShell(int width, int height, AuditRunStore runStore)
    {
        var uiStateBase = Directory.CreateTempSubdirectory("groupweaver-audit-compare-uistate-").FullName;
        var shell = new ShellViewModel(
            _ => new DemoProvider(), new StartupOptions(Demo: false), Present,
            graphRendererFactory: null, ruleset: null, locator: null,
            uiStateStore: new UiStateStore(uiStateBase),
            targetedProviderFactory: null,
            auditRunStore: runStore);

        var window = new MainWindow { DataContext = shell, Width = width, Height = height };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, shell);
    }

    private static async Task<WorkspaceViewModel> DriveToWorkspaceAsync(ShellViewModel shell)
    {
        var connect = Assert.IsType<ConnectionViewModel>(shell.CurrentStep);
        await connect.ConnectDemoCommand.ExecuteAsync(null);
        var picker = Assert.IsType<RootPickerViewModel>(shell.CurrentStep);
        await picker.LoadCandidates;
        Dispatcher.UIThread.RunJobs();

        picker.SelectedCandidate = picker.Candidates
            .First(c => c.Kind == AdObjectKind.OrganizationalUnit);
        picker.LoadRootCommand.Execute(null);
        var workspace = Assert.IsType<WorkspaceViewModel>(shell.CurrentStep);
        await workspace.Initialization;
        return workspace;
    }

    private static void Capture(Avalonia.Controls.Window window, string name, int width, int height)
    {
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Dispose();

        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.Equal(new PixelSize(width, height), frame!.PixelSize);

        var path = Path.Combine(ArtifactsUiDir.Value, $"{name}.png");
        frame.Save(path);
        Assert.True(new FileInfo(path).Length > 0, $"'{path}' is empty");
    }
}
