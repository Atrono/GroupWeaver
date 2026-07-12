using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Input;

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
/// Pins the ADR-037 D10 "Open logs folder" affordance (WP10 #249): <see cref="SettingsViewModel"/>
/// exposes an <c>OpenLogsFolderCommand</c> (<c>[RelayCommand]</c>-generated <c>ICommand</c>,
/// opening <c>FileLogSink.ResolveLogDirectory()</c> in Explorer via a NEVER-THROW shell-out —
/// the <c>UxFeedbackLink.OpenInBrowser</c> precedent; the shell-out itself deliberately stays
/// untested), and the <see cref="SettingsWindow"/> realizes exactly one visible button bound to
/// it (the house realized-view assert, the <c>SettingsFileTabActionHierarchyTests</c> idiom:
/// located by bound command instance, never by content text). The command is reflection-pinned
/// so this file compiles while the member does not exist — its absence is the red assertion.
/// The window reads no user profile (<see cref="SettingsViewModel.LoadFrom(Ruleset)"/> over the
/// embedded default), so the #124 rail-state realization hazard does not apply.
/// </summary>
public sealed class SettingsOpenLogsFolderTests
{
    [Fact]
    public void SettingsViewModel_ExposesTheOpenLogsFolderCommand()
    {
        var property = OpenLogsFolderCommandProperty();
        Assert.True(
            typeof(ICommand).IsAssignableFrom(property.PropertyType),
            "OpenLogsFolderCommand must be bindable as an ICommand ([RelayCommand] OpenLogsFolder).");
    }

    [AvaloniaFact]
    public void SettingsWindow_RealizesExactlyOneVisibleButton_BoundToTheCommand()
    {
        var vm = SettingsViewModel.LoadFrom(RulesetLoader.LoadDefault());
        var command = Assert.IsAssignableFrom<ICommand>(OpenLogsFolderCommandProperty().GetValue(vm));

        var window = new SettingsWindow { DataContext = vm, Width = 1280, Height = 720 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // The placement (which tab, or the always-visible chrome) is the implementer's choice:
        // bring every tab to the front in turn and collect the realized, visible buttons bound
        // to the command — an always-visible placement is re-found under every tab and the
        // reference-keyed set dedups it back to the single control.
        var matches = new HashSet<Button>();
        CollectMatches(window, command, matches);
        var tabs = Assert.Single(window.GetVisualDescendants().OfType<TabControl>());
        foreach (var item in tabs.GetVisualDescendants().OfType<TabItem>().ToList())
        {
            tabs.SelectedItem = item;
            Dispatcher.UIThread.RunJobs();
            CollectMatches(window, command, matches);
        }

        Assert.True(
            matches.Count == 1,
            $"expected exactly ONE realized, visible button bound to OpenLogsFolderCommand "
            + $"anywhere in the Settings window; found {matches.Count}.");

        window.Close();
    }

    private static PropertyInfo OpenLogsFolderCommandProperty()
    {
        var property = typeof(SettingsViewModel).GetProperty(
            "OpenLogsFolderCommand", BindingFlags.Public | BindingFlags.Instance);
        Assert.True(
            property is not null,
            "ADR-037 D10 pins SettingsViewModel.OpenLogsFolderCommand — the 'Open logs folder' "
            + "affordance opening FileLogSink.ResolveLogDirectory() via a never-throw shell-out "
            + "(the UxFeedbackLink.OpenInBrowser precedent).");
        return property!;
    }

    private static void CollectMatches(SettingsWindow window, ICommand command, HashSet<Button> matches)
    {
        foreach (var button in window.GetVisualDescendants().OfType<Button>())
        {
            if (button.IsEffectivelyVisible && ReferenceEquals(button.Command, command))
            {
                matches.Add(button);
            }
        }
    }
}
