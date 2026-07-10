using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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
/// The systematic UIA accessible-name sweep (ui-checklist "Keyboard traversal &amp; accessible
/// names", <c>[T:AccessibleNameSweepTests]</c>) — the generalization of the four hand-picked
/// glyph-button pins in <see cref="GlyphControlAccessibilityTests"/> to EVERY realizable shell
/// surface. Issue class: WCAG 2.2 SC 4.1.2 (name/role/value) — a focusable interactive control
/// with an empty UIA Name is announced by a screen reader as a bare role ("button", "edit"),
/// which is the #219 bug class when its visible content is a glyph.
///
/// <para><b>What "effective accessible name" means here.</b> Each swept control's name is read
/// through <see cref="ControlAutomationPeer.CreatePeerForElement"/> +
/// <see cref="AutomationPeer.GetName"/> — the EXACT derivation Avalonia's UIA bridge uses, so the
/// sweep can never disagree with what a screen reader is given. That derivation is: an explicit
/// <c>AutomationProperties.Name</c> wins; else <c>AutomationProperties.LabeledBy</c>; else (for
/// <c>ContentControl</c>s) the text of a realized <c>TextBlock</c>/string content child; else the
/// content's <c>ToString()</c>. Three canary facts at the bottom pin the load-bearing corners of
/// that behavior against an Avalonia bump: a plain text child IS a name source; a
/// <see cref="TextBox"/> <c>Watermark</c> is NOT (a watermark-only TextBox has an EMPTY UIA name —
/// a FINDING, not a pass); and non-text content yields the JUNK type-name fallback
/// (<c>"Avalonia.Controls.PathIcon"</c>), which <see cref="EffectiveName"/> normalizes back to
/// empty — a type name announced by a screen reader is no name at all. CAVEAT: that
/// normalization recognizes only the DEFAULT <c>object.ToString()</c> (the bare type name) — a
/// future <c>ToString()</c> override on a row VM would flip its junk-name violation into a
/// SILENT PASS whose announced name is that override's output; if a content-bound VM gains a
/// <c>ToString()</c>, re-audit its surfaces.</para>
///
/// <para><b>Swept surfaces</b> (each drives the real shell exactly as the sibling
/// integration tests do): Connect, RootPicker, Workspace (rail expanded), Audit, Plan, Gap,
/// the <see cref="SettingsWindow"/> (all tabs brought to front), and
/// <see cref="KeyboardHelpWindow"/>. Swept control classes: <see cref="Button"/> (which in
/// Avalonia covers ToggleButton / RepeatButton / CheckBox / RadioButton by inheritance),
/// <see cref="MenuItem"/>, <see cref="ComboBox"/>, <see cref="TextBox"/>, <see cref="Slider"/>,
/// and <see cref="GridSplitter"/> — filtered to controls that are effectively visible and
/// focusable (the keyboard-reachable surface), and to app-authored controls
/// (<c>TemplatedParent == null</c>: a framework template's internals — a ScrollBar's repeat
/// buttons, a TextBox's inner clear button — are the theme's contract, not this app's XAML).</para>
///
/// <para><b>Known non-realizable interactive controls (documented, never a silent skip):</b>
/// (a) the AuditView triage <c>ContextMenu</c>'s three <see cref="MenuItem"/>s only enter the
/// visual tree when the flyout opens, which headless would require popup-overlay driving — they
/// carry plain-text <c>Header</c>s ("Acknowledge"/"Suppress"/"Un-triage"), the exact text-child
/// name source the canary pins, so the risk class is covered; (b) PlanView's rename
/// <see cref="TextBox"/> realizes only once a plan NODE is selected, and a fresh plan is empty —
/// it is a watermark-only TextBox, so it is covered by the same issue as its realized siblings on
/// that surface. Also EXCLUDED from the swept classes (documented, not silent): the
/// container-generated item chrome <c>ListBoxItem</c>/<c>TabItem</c> — keyboard-focusable, but
/// their accessible names come from non-string DataTemplate content (badge + text rows), so
/// judging them needs its own name-derivation rules; sweeping item containers is a follow-up
/// extension (flagship case: the RootPicker candidate rows). Every OTHER surface in the shell's
/// step machine is swept.</para>
///
/// <para><b>The allowlist contract:</b> <see cref="Allowlist"/> holds identifiers of KNOWN,
/// tracked violations only — each entry must carry a <c>// TODO(#issue)</c>. The sweep itself is
/// never weakened: a new nameless control fails the build until it either gets a name or an
/// issue. Fix direction is always the view (an <c>AutomationProperties.Name</c>, a
/// <c>LabeledBy</c>, or a real text child), never this filter.</para>
/// </summary>
public sealed class AccessibleNameSweepTests
{
    private const string DemoRootDn = "OU=AGDLP-Demo,DC=weavedemo,DC=example";

    /// <summary>WebView2 forced present (never the live registry probe) so the shell behaves
    /// machine-independently — the same pin every sibling shell fixture uses.</summary>
    private static readonly WebView2RuntimeStatus Present = new(IsInstalled: true, Version: "test");

    /// <summary>
    /// KNOWN violations, tracked as issues — see the class doc for the contract. Keys are
    /// <c>{surface} :: {control type} :: {x:Name | watermark | .style-classes | dc:DataContextType | (anonymous)}</c>
    /// as produced by <see cref="AllowKey"/>. EMPTY means the swept surface is clean.
    /// </summary>
    private static readonly HashSet<string> Allowlist = new(StringComparer.Ordinal)
    {
        // TODO(#317): Connect — the two Advanced targeting fields are watermark-only
        // TextBoxes: the caption TextBlocks above them are not linked via LabeledBy, so the UIA
        // name is empty (the watermark canary pins that a watermark is NOT a name source).
        "Connect :: TextBox :: Server / DC host (optional, e.g. dc1.corp.example)",
        "Connect :: TextBox :: Base DN (optional, e.g. OU=Groups,DC=corp,DC=example)",

        // TODO(#317): RootPicker — the mandatory entry filter is a watermark-only TextBox.
        "RootPicker :: TextBox :: Filter by name, SAM, or DN",

        // TODO(#321): Workspace — both GridSplitters are focusable but nameless: the
        // ADR-022 rail column splitter (anonymous) and the findings/detail row splitter (tooltip
        // only — a ToolTip.Tip is not the UIA name, the #219 lesson).
        "Workspace :: GridSplitter :: dc:WorkspaceViewModel",
        "Workspace :: GridSplitter :: FindingsDetailSplitter",

        // TODO(#319): Workspace — panel-content buttons whose UIA name is the junk
        // type-name fallback: the three severity filter chips (StackPanel content), every
        // violations-sidebar row's jump button (its whole row is the content), and the Audit
        // button (icon + text panel).
        "Workspace :: Button :: .chip",
        "Workspace :: Button :: dc:ViolationRowModel",
        "Workspace :: Button :: AuditButton",

        // TODO(#318): Audit — the selection CheckBoxes render NO text at all (tooltip
        // only): the header select-all box and the per-row select box. GLYPH-ONLY, the exact
        // #219 bug class: a screen reader announces a bare "checkbox".
        "Audit :: CheckBox :: dc:AuditViewModel",
        "Audit :: CheckBox :: dc:AuditFindingRowModel",

        // TODO(#319): Audit — panel-content buttons on the junk type-name fallback:
        // the severity/status filter chips, the rule-class chips, and the sortable column
        // headers (SEV/OBJECT/RULE).
        "Audit :: Button :: .chip",
        "Audit :: Button :: .chip .chip-row",
        "Audit :: Button :: .colhead",

        // TODO(#320): Plan — the authoring panel's pickers: the new-object kind + edge
        // Parent/Member ComboBoxes (a ComboBox derives NO name from its selection).
        "Plan :: ComboBox :: dc:PlanViewModel",

        // TODO(#317): Plan — the watermark-only Name TextBox. (The SAM TextBox is
        // visibility-gated with the same watermark-only shape and is covered by the same fix.)
        "Plan :: TextBox :: Name",

        // TODO(#319): Gap — every diff row's jump button (same row-as-content shape as
        // the workspace violations sidebar) has only the junk type-name fallback.
        "Gap :: Button :: .active-band-host",

        // TODO(#317): Settings/File — metadata fields (watermark-only TextBoxes).
        "Settings :: TextBox :: ruleset name (required)",
        "Settings :: TextBox :: author (optional)",
        "Settings :: TextBox :: description (optional)",

        // TODO(#318): Settings/Rules+Naming — the per-rule enable CheckBoxes have no
        // content at all (GLYPH-ONLY, #219 class).
        "Settings :: CheckBox :: dc:RuleRowEditor",
        "Settings :: CheckBox :: dc:NamingRuleEditor",

        // TODO(#320): Settings/Rules+Naming — the severity/subject-kind ComboBoxes derive
        // no name from their selection.
        "Settings :: ComboBox :: dc:RuleRowEditor",
        "Settings :: ComboBox :: dc:NamingRuleEditor",

        // TODO(#317): Settings/Rules+Naming — the naming-rule card editors are
        // watermark-only TextBoxes.
        "Settings :: TextBox :: rule id",
        "Settings :: TextBox :: ^GG_..._..._$ (regex)",
        "Settings :: TextBox :: description",
        "Settings :: TextBox :: GG_Vertrieb_Lesen",

        // TODO(#320): Settings/Matrix — every cell verdict ComboBox (and the VM-level
        // unlisted-fallback ComboBox) reads as nameless; nothing names the row/column pair
        // a cell judges.
        "Settings :: ComboBox :: dc:NestingCellEditor",
        "Settings :: ComboBox :: dc:SettingsViewModel",

        // TODO(#318): Settings/Matrix — the VM-level matrix CheckBox renders no text at all
        // (GLYPH-ONLY, #219 class).
        "Settings :: CheckBox :: dc:SettingsViewModel",

        // TODO(#320): Settings/Ignore & Exceptions — the entry-mode ComboBox ("Dn"/"Name")
        // derives no name from its selection.
        "Settings :: ComboBox :: dc:MatchEntryEditor",

        // TODO(#317): Settings/Ignore & Exceptions — the watermark-only glob / note /
        // glob-tester TextBoxes.
        "Settings :: TextBox :: glob (e.g. *,CN=Builtin,*)",
        "Settings :: TextBox :: note (optional)",
        "Settings :: TextBox :: test a DN/name against the glob",

        // TODO(#317): Settings/Advanced (JSONC) — the raw editor TextBox has an x:Name
        // (RawEditor) but no accessible name (nothing labels it).
        "Settings :: TextBox :: RawEditor",
    };

    // === per-surface sweeps ==================================================================

    /// <summary>The Connect step (plus the ever-present top command strip): every visible,
    /// focusable interactive control exposes a non-empty UIA name. The Advanced disclosure is
    /// expanded first so its server/base-DN TextBoxes are realized and judged too.</summary>
    [AvaloniaFact(Timeout = 60_000)]
    public async Task ConnectStep_EveryInteractiveControl_ExposesAnAccessibleName()
    {
        await using var ctx = ShellContext.Show();

        var connect = Assert.IsType<ConnectionViewModel>(ctx.Shell.CurrentStep);
        // Realize the collapsed-by-default Advanced targeting fields (ADR-031 D1) — a sweep of
        // the collapsed card would silently miss its two TextBoxes.
        connect.ToggleAdvancedCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        // Floors are ~2/3 of the observed realized counts (Connect realizes 8 today) — hollow-drive
        // tripwires, not exact pins.
        AssertAccessibleNames("Connect", ctx.Window, minSwept: 6);
        ctx.Window.Close();
    }

    /// <summary>The RootPicker step with its candidate list loaded: filter box, list rows, and
    /// the Back/Load actions all realized and judged.</summary>
    [AvaloniaFact(Timeout = 60_000)]
    public async Task RootPickerStep_EveryInteractiveControl_ExposesAnAccessibleName()
    {
        await using var ctx = ShellContext.Show();
        await ctx.DriveToRootPickerAsync();

        AssertAccessibleNames("RootPicker", ctx.Window, minSwept: 5); // observed today: 6
        ctx.Window.Close();
    }

    /// <summary>The loaded Workspace step with the rail EXPANDED (the temp-dir
    /// <see cref="UiStateStore"/> seam guarantees the default state — lab-environment.md #124):
    /// graph-side chrome, the seam toggle + splitters, and the full right rail are judged.</summary>
    [AvaloniaFact(Timeout = 60_000)]
    public async Task WorkspaceStep_RailExpanded_EveryInteractiveControl_ExposesAnAccessibleName()
    {
        await using var ctx = ShellContext.Show();
        var workspace = await ctx.DriveToWorkspaceAsync();
        Assert.False(workspace.IsRailCollapsed, "the rail must be expanded for a full-surface sweep");

        AssertAccessibleNames("Workspace", ctx.Window, minSwept: 14); // observed today: 21
        ctx.Window.Close();
    }

    /// <summary>The Audit step over the loaded demo Ist (19-finding baseline, so the findings
    /// table, its filter chips, sort headers, and the selection checkboxes are all realized).</summary>
    [AvaloniaFact(Timeout = 60_000)]
    public async Task AuditStep_EveryInteractiveControl_ExposesAnAccessibleName()
    {
        await using var ctx = ShellContext.Show();
        var workspace = await ctx.DriveToWorkspaceAsync();

        ctx.Shell.OnAudit(workspace);
        var audit = Assert.IsType<AuditViewModel>(ctx.Shell.CurrentStep);
        Assert.True(audit.HasFindings, "the demo baseline must yield findings or the table sweep is hollow");
        Dispatcher.UIThread.RunJobs();

        AssertAccessibleNames("Audit", ctx.Window, minSwept: 18); // observed today: 27
        ctx.Window.Close();
    }

    /// <summary>The Plan step (fresh empty plan over the demo root): the authoring panel's
    /// kind/parent ComboBoxes, name/SAM TextBoxes, and action buttons are judged.</summary>
    [AvaloniaFact(Timeout = 60_000)]
    public async Task PlanStep_EveryInteractiveControl_ExposesAnAccessibleName()
    {
        await using var ctx = ShellContext.Show();
        var workspace = await ctx.DriveToWorkspaceAsync();

        ctx.Shell.OnDesignPlan(workspace);
        Assert.IsType<PlanViewModel>(ctx.Shell.CurrentStep);
        Dispatcher.UIThread.RunJobs();

        AssertAccessibleNames("Plan", ctx.Window, minSwept: 9); // observed today: 13
        ctx.Window.Close();
    }

    /// <summary>The Gap step with its diff computed (Workspace → Plan → Gap, awaiting
    /// <see cref="GapViewModel.RefreshAsync"/> so the diff summary chrome is realized).</summary>
    [AvaloniaFact(Timeout = 60_000)]
    public async Task GapStep_EveryInteractiveControl_ExposesAnAccessibleName()
    {
        await using var ctx = ShellContext.Show();
        var workspace = await ctx.DriveToWorkspaceAsync();
        Assert.NotNull(workspace.Snapshot); // OnGapAnalysis gates on a loaded Ist

        ctx.Shell.OnDesignPlan(workspace);
        var plan = Assert.IsType<PlanViewModel>(ctx.Shell.CurrentStep);
        ctx.Shell.OnGapAnalysis(plan, workspace);
        var gap = Assert.IsType<GapViewModel>(ctx.Shell.CurrentStep);
        await gap.RefreshAsync();
        Dispatcher.UIThread.RunJobs();

        AssertAccessibleNames("Gap", ctx.Window, minSwept: 9); // observed today: 14
        ctx.Window.Close();
    }

    /// <summary>
    /// The <see cref="SettingsWindow"/> over the default-ruleset mirror, EVERY tab brought to the
    /// front in turn (Rules / Naming / Matrix / Ignore &amp; Exceptions / File / Advanced) — the
    /// same standalone-Show seam the sibling settings view tests use (<c>ShowDialog</c> is
    /// headless-hostile, ADR-011 open-risk #3). Violations are aggregated across tabs and deduped
    /// by allowlist key, so the shared footer/validation chrome is judged once.
    /// </summary>
    [AvaloniaFact] // no Timeout: AvaloniaFact supports Timeout only on async tests
    public void SettingsWindow_EveryTab_EveryInteractiveControl_ExposesAnAccessibleName()
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

        // Whole-window anti-vacuity only (the Matrix tab alone realizes ~100 cell ComboBoxes
        // today; 133 observed): a PER-TAB floor is subsumed by the shrink ratchet below — every
        // tab owns allowlist entries, so a tab that realizes nothing surfaces as its entries
        // going unmatched.
        Assert.True(
            maxSwept >= 60,
            $"Settings: sweep looks hollow — only {maxSwept} interactive control(s) realized on the densest tab.");
        var real = violations.Where(v => !Allowlist.Contains(v.Key)).ToList();
        Assert.True(real.Count == 0, FailureMessage("Settings", maxSwept, real.Select(v => v.Value)));

        AssertAllowlistEntriesStillLive(
            "Settings", violations.Keys.ToHashSet(StringComparer.Ordinal));

        window.Close();
    }

    /// <summary>The keyboard/gesture cheat sheet window (static content; its one interactive
    /// control is the Close button, which must be named).</summary>
    [AvaloniaFact] // no Timeout: AvaloniaFact supports Timeout only on async tests
    public void KeyboardHelpWindow_EveryInteractiveControl_ExposesAnAccessibleName()
    {
        var window = new KeyboardHelpWindow();
        window.Show();
        Dispatcher.UIThread.RunJobs();

        AssertAccessibleNames("KeyboardHelp", window, minSwept: 1);
        window.Close();
    }

    // === peer-behavior canaries ==============================================================
    // The sweep's pass criterion is Avalonia's OWN name derivation (the peer), so these three
    // facts pin the corners the sweep's honesty depends on. If an Avalonia bump flips any of
    // them, the sweep's semantics changed and every surface verdict must be re-audited.

    /// <summary>A plain text content child IS a UIA name source: a Button with a string Content
    /// derives that text as its peer name (why "⚙ Settings"-style buttons pass without an
    /// explicit AutomationProperties.Name).</summary>
    [AvaloniaFact]
    public void PeerBehavior_ButtonWithATextChild_DerivesItsNameFromTheText()
    {
        var button = new Button { Content = "Sample action" };
        var window = new Window { Content = button, Width = 200, Height = 100 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(
            "Sample action",
            ControlAutomationPeer.CreatePeerForElement(button).GetName());

        window.Close();
    }

    /// <summary>A <see cref="TextBox"/> <c>Watermark</c> is NOT a UIA name source: a watermark-only
    /// TextBox has an EMPTY peer name (it reaches UIA as placeholder text, not the Name), so every
    /// watermark-only TextBox the sweep flags is a REAL finding — a screen reader announces it as a
    /// bare "edit".</summary>
    [AvaloniaFact]
    public void PeerBehavior_TextBoxWithOnlyAWatermark_HasAnEmptyPeerName()
    {
        var textBox = new TextBox { Watermark = "Server / DC host" };
        var window = new Window { Content = textBox, Width = 200, Height = 100 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var name = ControlAutomationPeer.CreatePeerForElement(textBox).GetName();
        Assert.True(
            string.IsNullOrEmpty(name),
            $"Avalonia's TextBox peer now derives '{name}' — the watermark-only findings below must be re-audited");

        window.Close();
    }

    /// <summary>For NON-text content the ContentControl peer falls back to
    /// <c>Content?.ToString()</c> — for a PathIcon-only button (the #219 shape) that is the junk
    /// type name <c>"Avalonia.Controls.PathIcon"</c>, which is exactly what a screen reader would
    /// announce. The sweep therefore treats a name equal to the content's default
    /// <c>object.ToString()</c> (its type name) as EMPTY (<see cref="EffectiveName"/>); this canary
    /// pins the behavior so an Avalonia bump that changes the fallback re-opens the audit.</summary>
    [AvaloniaFact]
    public void PeerBehavior_ButtonWithNonTextContent_FallsBackToTheJunkContentTypeName()
    {
        var button = new Button { Content = new PathIcon() };
        var window = new Window { Content = button, Width = 200, Height = 100 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(
            typeof(PathIcon).ToString(),
            ControlAutomationPeer.CreatePeerForElement(button).GetName());

        window.Close();
    }

    // === the sweep ===========================================================================

    /// <summary>One violation: the allowlist key plus a human-readable, actionable detail line.</summary>
    private sealed record Violation(string Key, string Detail);

    /// <summary>Sweeps a surface and asserts every swept control carries a non-empty effective
    /// accessible name (modulo the tracked <see cref="Allowlist"/>). <paramref name="minSwept"/>
    /// is the anti-vacuity floor: a drive or filter regression that realizes fewer interactive
    /// controls than the surface is known to hold fails loudly instead of passing hollow. Also
    /// enforces the shrink ratchet (<see cref="AssertAllowlistEntriesStillLive"/>).</summary>
    private static void AssertAccessibleNames(string surface, Visual root, int minSwept)
    {
        var (swept, violations) = Sweep(surface, root);
        Assert.True(
            swept >= minSwept,
            $"{surface}: sweep looks hollow — only {swept} interactive control(s) realized (expected >= {minSwept}); the drive or the type/visibility filter regressed.");

        var real = violations.Where(v => !Allowlist.Contains(v.Key)).ToList();
        Assert.True(real.Count == 0, FailureMessage(surface, swept, real.Select(v => v.Detail)));

        AssertAllowlistEntriesStillLive(surface, violations.Select(v => v.Key).ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>The allowlist SHRINK RATCHET: every <see cref="Allowlist"/> entry belonging to
    /// <paramref name="surface"/> (prefix-partitioned — exactly one test owns each surface) must
    /// be MATCHED by an observed violation key from this run. An unmatched entry means either the
    /// control was FIXED (celebrate: remove the entry and its TODO, and close/trim the issue) or
    /// the drive/realization regressed so the control no longer renders — both must fail loudly,
    /// so entries can only ever shrink with reality and a dead archetype cannot ride along under
    /// a broad key.</summary>
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
            + "\nEither the control was fixed (remove this entry + its TODO and update the tracking issue) "
            + "or the drive regressed and the control no longer realizes (fix the drive) — an allowlist "
            + "entry may never outlive the violation it tracks.");
    }

    /// <summary>Walks <see cref="VisualExtensions.GetVisualDescendants"/> for the swept control
    /// classes, filters to visible + focusable + app-authored (see class doc), and reads each
    /// effective name through the control's own automation peer.</summary>
    private static (int Swept, List<Violation> Violations) Sweep(string surface, Visual root)
    {
        var swept = 0;
        var violations = new List<Violation>();
        foreach (var control in root.GetVisualDescendants().OfType<Control>())
        {
            // The interactive classes under the WCAG 4.1.2 sweep. `Button` covers ToggleButton,
            // RepeatButton, CheckBox and RadioButton (Avalonia inheritance).
            if (control is not (Button or MenuItem or ComboBox or TextBox or Slider or GridSplitter))
            {
                continue;
            }

            // Framework template internals (ScrollBar repeat buttons, the TextBox clear button…)
            // belong to the theme, not this app's XAML — the sweep judges app-authored controls.
            if (control.TemplatedParent is not null)
            {
                continue;
            }

            // The keyboard-reachable surface: effectively visible AND focusable.
            if (!control.IsEffectivelyVisible || !control.Focusable)
            {
                continue;
            }

            swept++;
            var name = EffectiveName(control);
            if (!string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            violations.Add(new Violation(AllowKey(surface, control), Describe(surface, control)));
        }

        return (swept, violations);
    }

    /// <summary>The effective accessible name: what Avalonia's OWN peer derivation yields for the
    /// control (see the canaries), with ONE normalization — a name equal to the content's default
    /// <c>object.ToString()</c> (i.e. its bare type name, e.g. <c>"Avalonia.Controls.StackPanel"</c>,
    /// the ContentControl peer's last-resort fallback for non-text content) counts as EMPTY: a
    /// screen reader announcing a type name has no name in any meaningful sense.</summary>
    private static string? EffectiveName(Control control)
    {
        var name = ControlAutomationPeer.CreatePeerForElement(control).GetName();
        if (name is not null
            && control is ContentControl { Content: { } content }
            && content is not string
            && string.Equals(name, content.GetType().ToString(), StringComparison.Ordinal))
        {
            return null;
        }

        return name;
    }

    /// <summary>The stable allowlist identifier for a control on a surface — STRUCTURAL only
    /// (x:Name, watermark, style classes, then the DataContext's type name), never row DATA
    /// (tooltip/visible text may carry DNs or live counts), so repeated template rows deliberately
    /// collapse to one archetype key — one issue fixes the whole archetype.</summary>
    private static string AllowKey(string surface, Control control)
    {
        var classes = string.Join(
            " ",
            control.Classes.Where(c => !c.StartsWith(':')).Select(c => "." + c));
        var identity = control.Name
            ?? (control as TextBox)?.Watermark
            ?? (classes.Length > 0 ? classes : null)
            ?? (control.DataContext is { } dc ? $"dc:{dc.GetType().Name}" : "(anonymous)");
        return $"{surface} :: {control.GetType().Name} :: {identity}";
    }

    /// <summary>The actionable failure line: where it is, what it is, and what a sighted user
    /// sees — plus the [glyph-only] tag marking the #219 bug class (empty name AND glyph-like
    /// visible content: a screen reader gets NOTHING a sighted user could not already guess).</summary>
    private static string Describe(string surface, Control control)
    {
        var sb = new StringBuilder();
        sb.Append("  - ").Append(AllowKey(surface, control));
        if (IsGlyphLike(control))
        {
            sb.Append("  [glyph-only — #219 bug class]");
        }

        sb.Append("\n      ").Append(control.GetType().Name);
        if (control.Name is { } xname)
        {
            sb.Append(" x:Name=").Append(xname);
        }

        if (control is TextBox { Watermark: { } watermark })
        {
            sb.Append(" watermark='").Append(watermark).Append('\'');
        }

        if (ToolTip.GetTip(control) is string tooltip)
        {
            sb.Append(" tooltip='").Append(tooltip).Append('\'');
        }

        if (VisibleText(control) is { } text)
        {
            sb.Append(" text='").Append(text).Append('\'');
        }

        if (NearestNamedAncestor(control) is { } ancestor)
        {
            sb.Append(" under=").Append(ancestor);
        }

        return sb.ToString();
    }

    private static string FailureMessage(string surface, int swept, IEnumerable<string> details)
    {
        var list = details.ToList();
        return
            $"{surface}: {list.Count} of {swept} swept interactive control(s) expose an EMPTY UIA accessible name "
            + "(WCAG 2.2 SC 4.1.2 name/role/value — a screen reader announces only the bare role):\n"
            + string.Join("\n", list)
            + "\nFix in the VIEW (AutomationProperties.Name / LabeledBy / a real text child) — never by "
            + "weakening this sweep. A control may enter the allowlist only with a tracking // TODO(#issue).";
    }

    /// <summary>The control's visible text, the way the sweep reasons about "what a sighted user
    /// sees": its realized <see cref="TextBlock"/> descendants' text (covers string Content via the
    /// presenter) joined, or <c>null</c> when it renders no text at all.</summary>
    private static string? VisibleText(Control control)
    {
        var parts = control.GetVisualDescendants()
            .OfType<TextBlock>()
            .Select(t => t.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();
        return parts.Count > 0 ? string.Join(" ", parts) : null;
    }

    /// <summary>The #219 bug-class detector for an already-nameless control: its visible content
    /// is a single non-alphanumeric character (a bare glyph), or it renders no text at all but
    /// shows an icon (<see cref="Avalonia.Controls.Shapes.Path"/> / <see cref="PathIcon"/>). A
    /// watermark-only TextBox shows real prose, so it is nameless-but-not-glyph-like.</summary>
    private static bool IsGlyphLike(Control control)
    {
        if (control is TextBox { Watermark: { } w } && !string.IsNullOrWhiteSpace(w))
        {
            return false;
        }

        if (VisibleText(control) is { } text)
        {
            var trimmed = text.Trim();
            return trimmed.Length == 1 && !char.IsLetterOrDigit(trimmed[0]);
        }

        return control.GetVisualDescendants()
            .Any(v => v is Avalonia.Controls.Shapes.Path or PathIcon);
    }

    /// <summary>The nearest ancestor carrying an x:Name — the "where do I look in the XAML" hint
    /// for anonymous violations.</summary>
    private static string? NearestNamedAncestor(Control control) =>
        control.GetVisualAncestors()
            .OfType<Control>()
            .Select(c => c.Name)
            .FirstOrDefault(n =>
                !string.IsNullOrEmpty(n)
                // Template part names (PART_*) are theme plumbing, not a XAML location hint.
                && !n.StartsWith("PART_", StringComparison.Ordinal));

    // === harness (the sibling shell-fixture idiom: real MainWindow + demo shell + temp-dir
    // UiStateStore seam per lab-environment.md #124) ==========================================

    private sealed record ShellContext(MainWindow Window, ShellViewModel Shell, string StateDir)
        : IAsyncDisposable
    {
        /// <summary>A shown <see cref="MainWindow"/> over a demo-backed shell: WebView2 forced
        /// present, a null-View <see cref="FakeGraphRenderer"/> (the graph canvas is out of scope —
        /// its accessibility lives in the web bundle), and the temp-dir UiStateStore seam so a
        /// persisted RailCollapsed on this box can never zero the rail (#124).</summary>
        public static ShellContext Show()
        {
            var stateDir = Directory.CreateTempSubdirectory("groupweaver-a11y-sweep-").FullName;
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

        /// <summary>Connect (demo) and await the settled candidate list.</summary>
        public async Task<RootPickerViewModel> DriveToRootPickerAsync()
        {
            var connect = Assert.IsType<ConnectionViewModel>(Shell.CurrentStep);
            await connect.ConnectDemoCommand.ExecuteAsync(null);
            var picker = Assert.IsType<RootPickerViewModel>(Shell.CurrentStep);
            await picker.LoadCandidates;
            Dispatcher.UIThread.RunJobs();
            return picker;
        }

        /// <summary>Connect (demo) → pick the demo root OU (the scope carrying the seeded
        /// GG_Circle_A↔GG_Circle_B cycle) → load, awaiting the settled workspace.</summary>
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
