using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

using GroupWeaver.App.Settings;
using GroupWeaver.App.Startup;
using GroupWeaver.App.Tests.Fakes;
using GroupWeaver.App.ViewModels;
using GroupWeaver.App.Views;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Rules;
using GroupWeaver.Providers;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// The systematic native text-clipping sweep (ADR-041 D2.1; ui-checklist "Global" clipped-
/// controls items) — the structural-invariant replacement for a pixel-diff on the #286 bug
/// class: a <see cref="TextBlock"/> whose container gives it less room than its text needs
/// renders CUT OFF, silently, on every capture until a judge happens to look at that corner.
/// Walks every realized <see cref="TextBlock"/> (which covers <c>SelectableTextBlock</c> by
/// inheritance) of every drivable shell surface — the same drive harness idiom as
/// <see cref="AccessibleNameSweepTests"/> (temp-dir <see cref="UiStateStore"/> seam per
/// lab-environment.md #124, anti-vacuity floors, per-surface tests, allowlist shrink ratchet).
///
/// <para><b>The clip verdict.</b> For each visible, non-empty TextBlock the sweep compares the
/// space the text NEEDS against the space the layout GAVE it (<c>Bounds</c>), where "needs" is
/// the max of two signals — both are required: (a) <c>DesiredSize</c> minus <c>Margin</c> (what
/// measure asked for; Avalonia clamps this to the measure constraint, so it goes blind exactly
/// when a fixed-size container squeezes the measure — the #286 shape), and (b) the realized
/// <c>TextLayout</c> ink extents plus <c>Padding</c> (the actual laid-out text, immune to the
/// constraint clamp). The <c>DetectorCanary_*</c> facts at the bottom prove both directions
/// against real layouts — they are the PERMANENT fail-path proof that the detector detects.</para>
///
/// <para><b>Exemption rules</b> (each deliberate, each still judged on the axis that stays
/// honest — designed so the #286 watermark-clip class is caught while deliberate ellipsis
/// passes):
/// E1 — <c>TextTrimming</c> set (not None): ellipsis is an intentional design decision (picker
///      DN rows), so WIDTH overflow is exempt; HEIGHT is still judged (a trimmed line cut in
///      half vertically is never intentional).
/// E2 — <c>TextWrapping</c> Wrap/WrapWithOverflow: the block absorbs width pressure by
///      reflowing, so WIDTH is exempt; HEIGHT is judged — precisely when the container
///      actually constrains it (arranged height below the wrapped text's laid-out height;
///      an unconstrained wrap block always arranges at or above its ink height and passes).
/// E3 — everything else: width AND height must fit.
/// A TextBlock inside a ScrollViewer needs no exemption: the scroll EXTENT arranges it at
/// full desired size (scrolling reveals the rest), so it only flags when genuinely cut.</para>
///
/// <para><b>Font determinism (ADR-041 D3).</b> Every measurement above depends on the resolved
/// font. The app ships NO embedded font — Tokens.axaml's <c>FontFamilyMono</c> is a SYSTEM
/// stack ("Cascadia Mono, Consolas, …": first hit varies by machine — Cascadia Mono is
/// installed on neither this box nor the windows-2022 runner today, so both silently fall back
/// to Consolas until some image update flips one of them) and the default UI face is ambient
/// Segoe UI. <see cref="TestAppBuilder"/> therefore pins BOTH channels to OFL fonts embedded
/// in THIS test assembly (Selawik = Microsoft's OFL metric companion to Segoe UI; Cascadia
/// Mono = the app's own first choice), via <c>FontManagerOptions.DefaultFamilyName</c> and an
/// <c>Application.Resources</c> own-entry override of <c>FontFamilyMono</c> (own entries
/// resolve before merged dictionaries). The <c>FontCanary_*</c> facts pin both pins — if an
/// Avalonia bump changes either resolution order, they fail before any sweep verdict lies.</para>
///
/// <para><b>The allowlist contract</b> (same as the name sweep): <see cref="Allowlist"/> holds
/// KNOWN, tracked violations only, each with a <c>// TODO(#issue)</c>; the shrink ratchet
/// fails any entry no observed violation matches, so entries can only shrink with reality.
/// Fix direction is always the VIEW (more room, deliberate trimming, or wrapping), never this
/// filter.</para>
/// </summary>
public sealed class TextClippingSweepTests
{
    private const string DemoRootDn = "OU=AGDLP-Demo,DC=weavedemo,DC=example";

    /// <summary>WebView2 forced present (never the live registry probe) — the sibling
    /// shell-fixture pin.</summary>
    private static readonly WebView2RuntimeStatus Present = new(IsInstalled: true, Version: "test");

    /// <summary>Sub-pixel slack: TextLayout ink extents are fractional while arranged bounds
    /// are layout-rounded, so a genuine fit can read up to ~1px short. The #286 bug class cuts
    /// whole ascenders/descenders (several px), so 1px slack costs no detection power.</summary>
    private const double Epsilon = 1.0;

    /// <summary>
    /// KNOWN violations, tracked as issues — see the class doc. Keys are
    /// <c>{surface} :: {x:Name | .style-classes | tpl:OwnerType[watermark] | text:prefix}</c>
    /// as produced by <see cref="AllowKey"/>. EMPTY means the swept surfaces are clean.
    /// </summary>
    private static readonly HashSet<string> Allowlist = new(StringComparer.Ordinal)
    {
    };

    // === per-surface sweeps ==================================================================

    /// <summary>The Connect step (Advanced targeting expanded so its caption/field labels are
    /// realized and judged too).</summary>
    [AvaloniaFact(Timeout = 60_000)]
    public async Task ConnectStep_NoRealizedTextBlockIsClipped()
    {
        await using var ctx = ShellContext.Show();

        var connect = Assert.IsType<ConnectionViewModel>(ctx.Shell.CurrentStep);
        connect.ToggleAdvancedCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        AssertNoClippedText("Connect", ctx.Window, minSwept: 13); // observed today: 20
        ctx.Window.Close();
    }

    /// <summary>The RootPicker step with its candidate list loaded (DN rows carry the
    /// deliberate-ellipsis trimming the E1 exemption exists for).</summary>
    [AvaloniaFact(Timeout = 60_000)]
    public async Task RootPickerStep_NoRealizedTextBlockIsClipped()
    {
        await using var ctx = ShellContext.Show();
        await ctx.DriveToRootPickerAsync();

        AssertNoClippedText("RootPicker", ctx.Window, minSwept: 27); // observed today: 41
        ctx.Window.Close();
    }

    /// <summary>The loaded Workspace step with the rail EXPANDED (temp-dir UiStateStore seam
    /// guarantees the default state, #124) — findings rows, detail panel, status strip.</summary>
    [AvaloniaFact(Timeout = 60_000)]
    public async Task WorkspaceStep_RailExpanded_NoRealizedTextBlockIsClipped()
    {
        await using var ctx = ShellContext.Show();
        var workspace = await ctx.DriveToWorkspaceAsync();
        Assert.False(workspace.IsRailCollapsed, "the rail must be expanded for a full-surface sweep");

        AssertNoClippedText("Workspace", ctx.Window, minSwept: 28); // observed today: 42
        ctx.Window.Close();
    }

    /// <summary>The Audit step over the loaded demo Ist (19-finding baseline: table rows,
    /// chips, tiles and the band header all realized).</summary>
    [AvaloniaFact(Timeout = 60_000)]
    public async Task AuditStep_NoRealizedTextBlockIsClipped()
    {
        await using var ctx = ShellContext.Show();
        var workspace = await ctx.DriveToWorkspaceAsync();

        ctx.Shell.OnAudit(workspace);
        var audit = Assert.IsType<AuditViewModel>(ctx.Shell.CurrentStep);
        Assert.True(audit.HasFindings, "the demo baseline must yield findings or the table sweep is hollow");
        Dispatcher.UIThread.RunJobs();

        AssertNoClippedText("Audit", ctx.Window, minSwept: 48); // observed today: 73
        ctx.Window.Close();
    }

    /// <summary>The Plan step (fresh empty plan over the demo root).</summary>
    [AvaloniaFact(Timeout = 60_000)]
    public async Task PlanStep_NoRealizedTextBlockIsClipped()
    {
        await using var ctx = ShellContext.Show();
        var workspace = await ctx.DriveToWorkspaceAsync();

        ctx.Shell.OnDesignPlan(workspace);
        Assert.IsType<PlanViewModel>(ctx.Shell.CurrentStep);
        Dispatcher.UIThread.RunJobs();

        AssertNoClippedText("Plan", ctx.Window, minSwept: 18); // observed today: 27
        ctx.Window.Close();
    }

    /// <summary>The Gap step with its diff computed.</summary>
    [AvaloniaFact(Timeout = 60_000)]
    public async Task GapStep_NoRealizedTextBlockIsClipped()
    {
        await using var ctx = ShellContext.Show();
        var workspace = await ctx.DriveToWorkspaceAsync();
        Assert.NotNull(workspace.Snapshot);

        ctx.Shell.OnDesignPlan(workspace);
        var plan = Assert.IsType<PlanViewModel>(ctx.Shell.CurrentStep);
        ctx.Shell.OnGapAnalysis(plan, workspace);
        var gap = Assert.IsType<GapViewModel>(ctx.Shell.CurrentStep);
        await gap.RefreshAsync();
        Dispatcher.UIThread.RunJobs();

        AssertNoClippedText("Gap", ctx.Window, minSwept: 24); // observed today: 36
        ctx.Window.Close();
    }

    /// <summary>The <see cref="SettingsWindow"/> over the default-ruleset mirror, every tab
    /// brought to the front in turn; violations aggregated and deduped by allowlist key.</summary>
    [AvaloniaFact] // no Timeout: AvaloniaFact supports Timeout only on async tests
    public void SettingsWindow_EveryTab_NoRealizedTextBlockIsClipped()
    {
        var vm = SettingsViewModel.LoadFrom(RulesetLoader.LoadDefault());
        var window = new SettingsWindow { DataContext = vm, Width = 1100, Height = 760 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var tabs = Assert.Single(window.GetVisualDescendants().OfType<TabControl>());
        Assert.True(tabs.ItemCount >= 5, "the settings editor lost tabs — the sweep drive regressed");

        var violations = new Dictionary<string, string>(StringComparer.Ordinal);
        var maxSwept = 0;
        for (var i = 0; i < tabs.ItemCount; i++)
        {
            tabs.SelectedIndex = i;
            Dispatcher.UIThread.RunJobs();

            var (swept, tabViolations) = Sweep("Settings", window);
            maxSwept = Math.Max(maxSwept, swept);
            foreach (var v in tabViolations)
            {
                violations.TryAdd(v.Key, v.Detail);
            }
        }

        Assert.True(
            maxSwept >= 65, // observed today: 98 on the densest tab
            $"Settings: sweep looks hollow — only {maxSwept} TextBlock(s) realized on the densest tab.");
        var real = violations.Where(v => !Allowlist.Contains(v.Key)).ToList();
        Assert.True(real.Count == 0, FailureMessage("Settings", maxSwept, real.Select(v => v.Value)));

        AssertAllowlistEntriesStillLive("Settings", violations.Keys.ToHashSet(StringComparer.Ordinal));

        window.Close();
    }

    /// <summary>The keyboard/gesture cheat sheet window at its default size.</summary>
    [AvaloniaFact] // no Timeout: AvaloniaFact supports Timeout only on async tests
    public void KeyboardHelpWindow_NoRealizedTextBlockIsClipped()
    {
        var window = new KeyboardHelpWindow();
        window.Show();
        Dispatcher.UIThread.RunJobs();

        AssertNoClippedText("KeyboardHelp", window, minSwept: 26); // observed today: 39
        window.Close();
    }

    // === font-determinism canaries (ADR-041 D3) ==============================================

    /// <summary>Pin (1): the default UI face resolves to the EMBEDDED Selawik — never the
    /// ambient system font. If this fails after an Avalonia bump, every measurement verdict in
    /// this class is suspect until re-audited.</summary>
    [AvaloniaFact]
    public void FontCanary_DefaultFamily_ResolvesToEmbeddedSelawik()
    {
        Assert.True(
            FontManager.Current.TryGetGlyphTypeface(new Typeface(FontFamily.Default), out var glyphTypeface),
            "the default typeface must resolve a glyph typeface");
        Assert.Equal("Selawik", glyphTypeface.FamilyName);
    }

    /// <summary>Pin (2): the <c>FontFamilyMono</c> resource a view's StaticResource lookup gets
    /// is the test host's own-entry override (embedded Cascadia Mono), NOT Tokens.axaml's system
    /// stack merged beneath it — pinning the own-entries-before-merged resolution order the
    /// override depends on, and that the embedded family actually yields glyphs.</summary>
    [AvaloniaFact]
    public void FontCanary_FontFamilyMonoResource_ResolvesToEmbeddedCascadiaMono()
    {
        var window = new Window();
        Assert.True(
            window.TryFindResource("FontFamilyMono", out var resource),
            "FontFamilyMono must be resolvable from any window (Tokens.axaml is app-merged)");
        var family = Assert.IsType<FontFamily>(resource);
        Assert.Equal(TestAppBuilder.EmbeddedMonoFamily, family.ToString());

        Assert.True(
            FontManager.Current.TryGetGlyphTypeface(new Typeface(family), out var glyphTypeface),
            "the embedded mono family must resolve a glyph typeface");
        Assert.Equal("Cascadia Mono", glyphTypeface.FamilyName);
    }

    // === detector-behavior canaries (the PERMANENT fail-path proof) ==========================
    // Each builds a minimal real layout and pins that the detector (a) fires on a genuine clip
    // with an actionable message and (b) stays quiet on the two deliberate styles. If Avalonia's
    // measure/TextLayout behavior shifts, these fail before any surface verdict lies.

    /// <summary>A plain (E3) TextBlock in a too-narrow fixed container MUST be flagged, and the
    /// failure detail must carry the measured-vs-arranged numbers (the actionable part).</summary>
    [AvaloniaFact]
    public void DetectorCanary_PlainTextInTooNarrowContainer_IsFlagged()
    {
        var text = new TextBlock { Text = "This prose is far wider than sixty pixels" };
        var window = ShowProbeWindow(new Border { Width = 60, Height = 40, Child = text });

        var violation = Judge("Canary", text);
        Assert.NotNull(violation);
        Assert.Contains("needs", violation.Detail, StringComparison.Ordinal);
        Assert.Contains("got", violation.Detail, StringComparison.Ordinal);

        window.Close();
    }

    /// <summary>The #286 watermark-clip SHAPE: a fixed-HEIGHT container shorter than one line.
    /// This is exactly where DesiredSize alone goes blind (the measure constraint clamps it) —
    /// the TextLayout ink signal must still flag it.</summary>
    [AvaloniaFact]
    public void DetectorCanary_TextInTooShortContainer_IsFlagged_TheWatermarkClipClass()
    {
        var text = new TextBlock { Text = "Clipped ascenders" };
        var window = ShowProbeWindow(new Border { Width = 400, Height = 6, Child = text });

        Assert.NotNull(Judge("Canary", text));

        window.Close();
    }

    /// <summary>E1: deliberate ellipsis (TextTrimming) in the same too-narrow container is NOT
    /// flagged — the picker-DN-row style must keep passing.</summary>
    [AvaloniaFact]
    public void DetectorCanary_TrimmedTextInTooNarrowContainer_IsExempt()
    {
        var text = new TextBlock
        {
            Text = "OU=AGDLP-Demo,DC=weavedemo,DC=example — far wider than sixty pixels",
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var window = ShowProbeWindow(new Border { Width = 60, Height = 40, Child = text });

        Assert.Null(Judge("Canary", text));

        window.Close();
    }

    /// <summary>E2 quiet half: a wrapping TextBlock that is narrow but free to grow vertically
    /// reflows instead of clipping — NOT flagged.</summary>
    [AvaloniaFact]
    public void DetectorCanary_WrappingTextWithRoomToGrow_IsExempt()
    {
        var text = new TextBlock
        {
            Text = "This prose is far wider than eighty pixels but wraps into many lines",
            TextWrapping = TextWrapping.Wrap,
        };
        var window = ShowProbeWindow(new Border { Width = 80, Child = text });

        Assert.Null(Judge("Canary", text));

        window.Close();
    }

    /// <summary>E2 loud half: the same wrapping TextBlock inside a container that CONSTRAINS its
    /// height below the wrapped line count IS flagged — wrap exempts width, never height.</summary>
    [AvaloniaFact]
    public void DetectorCanary_WrappingTextInHeightConstrainedContainer_IsFlagged()
    {
        var text = new TextBlock
        {
            Text = "This prose is far wider than eighty pixels but wraps into many lines",
            TextWrapping = TextWrapping.Wrap,
        };
        var window = ShowProbeWindow(new Border { Width = 80, Height = 20, Child = text });

        Assert.NotNull(Judge("Canary", text));

        window.Close();
    }

    private static Window ShowProbeWindow(Control content)
    {
        var window = new Window
        {
            Width = 500,
            Height = 300,
            Content = new StackPanel { Children = { content } },
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    // === the sweep ===========================================================================

    /// <summary>One violation: the allowlist key plus a human-readable, actionable detail.</summary>
    private sealed record Violation(string Key, string Detail);

    private static void AssertNoClippedText(string surface, Visual root, int minSwept)
    {
        var (swept, violations) = Sweep(surface, root);
        Assert.True(
            swept >= minSwept,
            $"{surface}: sweep looks hollow — only {swept} TextBlock(s) realized (expected >= {minSwept}); the drive or the visibility filter regressed.");

        var real = violations.Where(v => !Allowlist.Contains(v.Key)).ToList();
        Assert.True(real.Count == 0, FailureMessage(surface, swept, real.Select(v => v.Detail)));

        AssertAllowlistEntriesStillLive(surface, violations.Select(v => v.Key).ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>The allowlist SHRINK RATCHET (same contract as the name sweep): every entry
    /// belonging to <paramref name="surface"/> must be matched by an observed violation this
    /// run — an unmatched entry means the clip was FIXED (remove the entry) or the drive
    /// regressed (fix the drive); both fail loudly.</summary>
    private static void AssertAllowlistEntriesStillLive(string surface, IReadOnlySet<string> observedViolationKeys)
    {
        var stale = Allowlist
            .Where(e => e.StartsWith(surface + " :: ", StringComparison.Ordinal))
            .Where(e => !observedViolationKeys.Contains(e))
            .OrderBy(e => e, StringComparer.Ordinal)
            .ToList();
        Assert.True(
            stale.Count == 0,
            $"{surface}: {stale.Count} allowlist entr(y/ies) matched NO observed violation this run:\n  - "
            + string.Join("\n  - ", stale)
            + "\nEither the clip was fixed (remove this entry + its TODO and update the tracking issue) "
            + "or the drive regressed and the TextBlock no longer realizes (fix the drive) — an allowlist "
            + "entry may never outlive the violation it tracks.");
    }

    /// <summary>Walks every realized TextBlock under <paramref name="root"/>, filters to
    /// effectively visible blocks that render text, and judges each (see class doc).</summary>
    private static (int Swept, List<Violation> Violations) Sweep(string surface, Visual root)
    {
        var swept = 0;
        var violations = new List<Violation>();
        foreach (var text in root.GetVisualDescendants().OfType<TextBlock>())
        {
            if (!text.IsEffectivelyVisible)
            {
                continue;
            }

            // Blocks that render no text at all (empty bindings, spacer runs) have nothing to
            // clip. Inlines-composed blocks (<Run>) carry null Text but real Inlines — judge them.
            if (string.IsNullOrEmpty(text.Text) && (text.Inlines is null || text.Inlines.Count == 0))
            {
                continue;
            }

            swept++;
            if (Judge(surface, text) is { } violation)
            {
                violations.Add(violation);
            }
        }

        return (swept, violations);
    }

    /// <summary>The clip verdict for one realized TextBlock — see the class doc for the two
    /// measurement signals and the E1/E2/E3 exemption rules. Returns null when the text fits
    /// (or the overflow axis is deliberately exempt).</summary>
    private static Violation? Judge(string surface, TextBlock text)
    {
        var bounds = text.Bounds;
        var margin = text.Margin;
        var padding = text.Padding;

        // Signal (a): what measure asked for, net of margin (DesiredSize includes Margin,
        // Bounds does not). Clamped by the measure constraint, so never sufficient alone.
        var desiredWidth = text.DesiredSize.Width - margin.Left - margin.Right;
        var desiredHeight = text.DesiredSize.Height - margin.Top - margin.Bottom;

        // Signal (b): the realized ink extents plus Padding — the truth the constraint clamp
        // cannot hide.
        var layout = text.TextLayout;
        var inkWidth = layout.WidthIncludingTrailingWhitespace + padding.Left + padding.Right;
        var inkHeight = layout.Height + padding.Top + padding.Bottom;

        var neededWidth = Math.Max(desiredWidth, inkWidth);
        var neededHeight = Math.Max(desiredHeight, inkHeight);

        var trimmed = text.TextTrimming != TextTrimming.None;
        var wraps = text.TextWrapping is TextWrapping.Wrap or TextWrapping.WrapWithOverflow;

        // E1 (trimming) and E2 (wrapping) exempt WIDTH; E3 judges it.
        var widthClipped = !trimmed && !wraps && neededWidth > bounds.Width + Epsilon;

        // HEIGHT is judged under every rule. Two sub-signals, both needed: the ink-vs-bounds
        // comparison catches a partially cut line, but a height-constrained TextLayout DROPS
        // whole overflow lines from its own extent (so ink == bounds even though text
        // vanished) — the source-coverage check catches exactly that. Deliberate truncation
        // (TextTrimming, an explicit MaxLines) is exempt from the coverage check.
        var dropsText = !trimmed && text.MaxLines == 0 && LayoutDropsSourceText(text, layout);
        var heightClipped = neededHeight > bounds.Height + Epsilon || dropsText;

        if (!widthClipped && !heightClipped)
        {
            return null;
        }

        return new Violation(AllowKey(surface, text), Describe(surface, text, neededWidth, neededHeight, widthClipped, heightClipped, dropsText));
    }

    /// <summary>True when the realized layout laid out FEWER source characters than the block
    /// carries — a height-constrained TextLayout silently drops whole overflow lines, which is
    /// invisible to every extent comparison (the dropped lines are not in the extent).</summary>
    private static bool LayoutDropsSourceText(TextBlock text, Avalonia.Media.TextFormatting.TextLayout layout)
    {
        var sourceLength = (text.Text ?? text.Inlines?.Text ?? string.Empty).Length;
        if (sourceLength == 0)
        {
            return false;
        }

        var lines = layout.TextLines;
        if (lines.Count == 0)
        {
            return true;
        }

        var last = lines[^1];
        return last.FirstTextSourceIndex + last.Length < sourceLength;
    }

    /// <summary>The stable allowlist identifier — STRUCTURAL only (x:Name, style classes, the
    /// templated owner + its watermark, then a short text prefix as the last resort; static
    /// label text is authored in XAML, so it is structural for this sweep's purposes), so
    /// repeated template rows collapse to one archetype key.</summary>
    private static string AllowKey(string surface, TextBlock text)
    {
        var classes = string.Join(
            " ",
            text.Classes.Where(c => !c.StartsWith(':')).Select(c => "." + c));
        var identity = text.Name
            ?? (classes.Length > 0 ? classes : null)
            ?? TemplateIdentity(text)
            ?? "text:" + TextPrefix(text);
        return $"{surface} :: {identity}";
    }

    /// <summary>For a template-realized TextBlock (a TextBox's watermark presenter, a header
    /// chrome block…): the owning templated control's type, plus the watermark when the owner
    /// is a TextBox — the #286 class's natural archetype key.</summary>
    private static string? TemplateIdentity(TextBlock text)
    {
        if (text.TemplatedParent is not Control owner)
        {
            return null;
        }

        var watermark = (owner as TextBox)?.Watermark;
        return string.IsNullOrEmpty(watermark)
            ? $"tpl:{owner.GetType().Name}"
            : $"tpl:{owner.GetType().Name}[{watermark}]";
    }

    private static string TextPrefix(TextBlock text)
    {
        var raw = text.Text ?? text.Inlines?.Text ?? string.Empty;
        var flat = raw.Replace('\r', ' ').Replace('\n', ' ');
        return flat.Length <= 40 ? flat : flat[..40];
    }

    /// <summary>The actionable failure line: where it is, what it renders, and measured-vs-
    /// arranged per axis.</summary>
    private static string Describe(
        string surface,
        TextBlock text,
        double neededWidth,
        double neededHeight,
        bool widthClipped,
        bool heightClipped,
        bool dropsText)
    {
        var sb = new StringBuilder();
        sb.Append("  - ").Append(AllowKey(surface, text));
        sb.Append("\n      text='").Append(TextPrefix(text)).Append('\'');
        if (dropsText)
        {
            sb.Append(" [whole lines dropped by the height constraint]");
        }
        if (widthClipped)
        {
            sb.Append(
                System.Globalization.CultureInfo.InvariantCulture,
                $" WIDTH needs {neededWidth:0.##} got {text.Bounds.Width:0.##}");
        }

        if (heightClipped)
        {
            sb.Append(
                System.Globalization.CultureInfo.InvariantCulture,
                $" HEIGHT needs {neededHeight:0.##} got {text.Bounds.Height:0.##}");
        }

        sb.Append(" trimming=").Append(text.TextTrimming)
            .Append(" wrapping=").Append(text.TextWrapping);
        if (NearestNamedAncestor(text) is { } ancestor)
        {
            sb.Append(" under=").Append(ancestor);
        }

        return sb.ToString();
    }

    private static string FailureMessage(string surface, int swept, IEnumerable<string> details)
    {
        var list = details.ToList();
        return
            $"{surface}: {list.Count} of {swept} swept TextBlock(s) render CLIPPED text "
            + "(measured text extent exceeds the arranged bounds — the #286 bug class):\n"
            + string.Join("\n", list)
            + "\nFix in the VIEW (give the container room, or make truncation deliberate via "
            + "TextTrimming/TextWrapping) — never by weakening this sweep. A block may enter the "
            + "allowlist only with a tracking // TODO(#issue).";
    }

    /// <summary>The nearest ancestor carrying an x:Name — the "where do I look in the XAML"
    /// hint (template part names are theme plumbing, not a location hint).</summary>
    private static string? NearestNamedAncestor(TextBlock text) =>
        text.GetVisualAncestors()
            .OfType<Control>()
            .Select(c => c.Name)
            .FirstOrDefault(n =>
                !string.IsNullOrEmpty(n)
                && !n.StartsWith("PART_", StringComparison.Ordinal));

    // === harness (the sibling shell-fixture idiom: real MainWindow + demo shell + temp-dir
    // UiStateStore seam per lab-environment.md #124 — mirrors AccessibleNameSweepTests'
    // private harness verbatim; that class's tests are pinned, so the record is duplicated
    // rather than extracted) =================================================================

    private sealed record ShellContext(MainWindow Window, ShellViewModel Shell, string StateDir)
        : IAsyncDisposable
    {
        public static ShellContext Show()
        {
            var stateDir = Directory.CreateTempSubdirectory("groupweaver-clip-sweep-").FullName;
            var shell = new ShellViewModel(
                _ => new DemoProvider(),
                new StartupOptions(Demo: false),
                Present,
                graphRendererFactory: () => new FakeGraphRenderer { View = new Border() },
                ruleset: null,
                locator: null,
                uiStateStore: new UiStateStore(stateDir));

            var window = new MainWindow { DataContext = shell, Width = 1280, Height = 720 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            return new ShellContext(window, shell, stateDir);
        }

        public async Task<RootPickerViewModel> DriveToRootPickerAsync()
        {
            var connect = Assert.IsType<ConnectionViewModel>(Shell.CurrentStep);
            await connect.ConnectDemoCommand.ExecuteAsync(null);
            var picker = Assert.IsType<RootPickerViewModel>(Shell.CurrentStep);
            await picker.LoadCandidates;
            Dispatcher.UIThread.RunJobs();
            return picker;
        }

        public async Task<WorkspaceViewModel> DriveToWorkspaceAsync()
        {
            var picker = await DriveToRootPickerAsync();
            picker.SelectedCandidate = picker.Candidates.First(c => Dn.Comparer.Equals(c.Dn, DemoRootDn));
            picker.LoadRootCommand.Execute(null);
            var workspace = Assert.IsType<WorkspaceViewModel>(Shell.CurrentStep);
            await workspace.Initialization;
            Dispatcher.UIThread.RunJobs();
            return workspace;
        }

        public ValueTask DisposeAsync()
        {
            Shell.Dispose();
            try
            {
                Directory.Delete(StateDir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; never fail a sweep over temp-dir teardown.
            }
            catch (UnauthorizedAccessException)
            {
            }

            return ValueTask.CompletedTask;
        }
    }
}
