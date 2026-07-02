using System.Linq;

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;

using GroupWeaver.App.Settings;
using GroupWeaver.App.Views;
using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.App.Tests.Settings;

/// <summary>
/// Pins issue #221 (Lever 2, action hierarchy) + ADR-036 D3 (issue #236, the destructive tier)
/// at the VIEW layer for the Settings window's File tab: <c>Import…</c> and <c>Export…</c> are
/// benign secondaries (<c>ghost</c>, with the executable <c>DoesNotContain("destructive")</c>
/// dilution guard), while <c>Reset to default</c> — one click discards the WHOLE in-memory
/// ruleset mirror, the canonical whole-draft reset — carries <c>destructive</c> and neither
/// <c>accent</c> (destructive is never the primary — that stays the footer <c>Save</c>) nor
/// <c>ghost</c> (it moved out of the benign tier in #236). Only the <c>Classes=</c> attribute
/// changes; these assertions stop the hierarchy from silently drifting.
///
/// <para>The <see cref="SettingsWindow"/> is realized headless over a mirror seeded from the
/// embedded default (<see cref="SettingsViewModel.LoadFrom"/> — the demo-mode-safe default,
/// never a lab file, and it reads no user profile), then the File tab is brought to the front
/// so its three buttons realize. The buttons are not <c>x:Name</c>d, so each is located by its
/// bound <c>Command</c> instance (<c>ImportCommand</c> / <c>ExportCommand</c> / <c>ResetCommand</c>)
/// — robust against the two "Export…" senses and the ellipsis glyph in the content. The window
/// owns no rail / <c>UiStateStore</c> seam, so the #124 rail-collapse realization hazard does not
/// apply.</para>
/// </summary>
public sealed class SettingsFileTabActionHierarchyTests
{
    /// <summary>Import… is a secondary (ghost, not accent) — and, ADR-036 D3 OUT, never
    /// destructive (it REPLACES the mirror only through the loader gate on a successful pick;
    /// the dilution guard keeps the red tier rare).</summary>
    [AvaloniaFact]
    public void ImportButton_IsGhostSecondary_NotAccent()
    {
        var (window, vm) = ShowFileTab();

        var import = ButtonForCommand(window, vm.ImportCommand);
        Assert.Contains("ghost", import.Classes);
        Assert.DoesNotContain("accent", import.Classes);
        Assert.DoesNotContain("destructive", import.Classes); // ADR-036 D3 dilution guard

        window.Close();
    }

    /// <summary>Export… is a secondary (ghost, not accent) — and, ADR-036 D3 OUT, never
    /// destructive (it discards nothing; the dilution guard keeps the red tier rare).</summary>
    [AvaloniaFact]
    public void ExportButton_IsGhostSecondary_NotAccent()
    {
        var (window, vm) = ShowFileTab();

        var export = ButtonForCommand(window, vm.ExportCommand);
        Assert.Contains("ghost", export.Classes);
        Assert.DoesNotContain("accent", export.Classes);
        Assert.DoesNotContain("destructive", export.Classes); // ADR-036 D3 dilution guard

        window.Close();
    }

    /// <summary>ADR-036 D3 IN: Reset to default discards the WHOLE in-memory ruleset mirror in
    /// one click, so it carries <c>destructive</c> — and, critically, still NOT <c>accent</c>
    /// (the #221 destructive-never-primary contract this test has always pinned) and no longer
    /// the benign <c>ghost</c> it shared with Import/Export before #236.</summary>
    [AvaloniaFact]
    public void ResetButton_IsDestructive_AndNeverAccent()
    {
        var (window, vm) = ShowFileTab();

        var reset = ButtonForCommand(window, vm.ResetCommand);
        Assert.Contains("destructive", reset.Classes);
        Assert.DoesNotContain("accent", reset.Classes); // destructive is never the primary
        Assert.DoesNotContain("ghost", reset.Classes); // moved OUT of the benign ghost tier

        window.Close();
    }

    // --- helpers -------------------------------------------------------------------

    /// <summary>The single realized, visible <see cref="Button"/> bound to <paramref name="command"/>
    /// — located by command instance (the three File-tab buttons are unnamed and their content
    /// carries an ellipsis glyph, so the bound command is the robust, template-independent handle).
    /// <see cref="Assert.Single{T}(System.Collections.Generic.IEnumerable{T}, System.Func{T, bool})"/>
    /// makes it non-vacuous — a rewire fails here.</summary>
    private static Button ButtonForCommand(SettingsWindow window, System.Windows.Input.ICommand command) =>
        Assert.Single(
            window.GetVisualDescendants().OfType<Button>(),
            b => b.IsEffectivelyVisible && ReferenceEquals(b.Command, command));

    /// <summary>Realize <see cref="SettingsWindow"/> over the embedded default and bring the File
    /// tab to the front so its Import…/Export…/Reset buttons realize.</summary>
    private static (SettingsWindow Window, SettingsViewModel Vm) ShowFileTab()
    {
        var vm = SettingsViewModel.LoadFrom(RulesetLoader.LoadDefault());
        var window = new SettingsWindow { DataContext = vm, Width = 1280, Height = 720 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        SelectTab(window, "File");
        Dispatcher.UIThread.RunJobs();

        return (window, vm);
    }

    /// <summary>Bring the tab whose header contains <paramref name="header"/> to the front (the
    /// File-tab buttons realize only when their tab is selected) — mirrors the
    /// <c>ShellScreenshotTests.SelectTab</c> idiom.</summary>
    private static void SelectTab(SettingsWindow window, string header)
    {
        var tabs = Assert.Single(window.GetVisualDescendants().OfType<TabControl>());
        var item = Assert.Single(
            tabs.GetVisualDescendants().OfType<TabItem>(),
            t => (t.Header?.ToString() ?? string.Empty)
                .Contains(header, System.StringComparison.OrdinalIgnoreCase));
        tabs.SelectedItem = item;
    }
}
