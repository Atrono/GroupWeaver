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
/// Pins issue #221 (Lever 2, action hierarchy) at the VIEW layer for the Settings window's File
/// tab: the three file actions — <c>Import…</c>, <c>Export…</c>, <c>Reset to default</c> — all
/// carry the secondary <c>ghost</c> class (none of these is the window's primary — that is the
/// footer <c>Save</c>). The CRITICAL pin is the destructive-never-primary contract:
/// <c>Reset to default</c> must NOT carry <c>accent</c>, so a destructive action can never be
/// styled as the call-to-action. Only the <c>Classes=</c> attribute changed (the existing
/// ghost/accent classes are reused); these assertions stop the hierarchy from silently drifting.
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
    /// <summary>Import… is a secondary (ghost, not accent).</summary>
    [AvaloniaFact]
    public void ImportButton_IsGhostSecondary_NotAccent()
    {
        var (window, vm) = ShowFileTab();

        var import = ButtonForCommand(window, vm.ImportCommand);
        Assert.Contains("ghost", import.Classes);
        Assert.DoesNotContain("accent", import.Classes);

        window.Close();
    }

    /// <summary>Export… is a secondary (ghost, not accent).</summary>
    [AvaloniaFact]
    public void ExportButton_IsGhostSecondary_NotAccent()
    {
        var (window, vm) = ShowFileTab();

        var export = ButtonForCommand(window, vm.ExportCommand);
        Assert.Contains("ghost", export.Classes);
        Assert.DoesNotContain("accent", export.Classes);

        window.Close();
    }

    /// <summary>THE destructive-never-primary contract: Reset to default carries ghost and,
    /// critically, does NOT carry accent — a destructive action is never the call-to-action.</summary>
    [AvaloniaFact]
    public void ResetButton_IsGhost_AndNeverAccent()
    {
        var (window, vm) = ShowFileTab();

        var reset = ButtonForCommand(window, vm.ResetCommand);
        Assert.Contains("ghost", reset.Classes);
        Assert.DoesNotContain("accent", reset.Classes); // destructive is never the primary

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
