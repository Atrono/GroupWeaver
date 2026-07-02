using System.Collections.Generic;
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
/// Pins ADR-036 D3 (issue #236) — the destructive-tier MEMBERSHIP rule — for the Settings
/// window's rule-editing tabs: an action carries <c>destructive</c> iff one click discards
/// user-authored state beyond the single row it sits beside.
///
/// <list type="bullet">
///   <item><b>IN — the Naming-tab per-card <c>Remove</c>:</b> deletes a whole configured
///   naming rule including its pattern and exceptions list (a compound deletion), so every
///   card's Remove carries <c>destructive</c> and never <c>accent</c> (destructive is never
///   the primary).</item>
///   <item><b>OUT — the Ignore-tab row <c>Remove</c>s (and the exception-row <c>Remove</c>):</b>
///   each deletes exactly the row it sits beside — red on every list row is an alarm wall that
///   dilutes the signal, so they stay unclassed. The <c>DoesNotContain("destructive")</c> arm
///   is the executable dilution guard.</item>
/// </list>
///
/// <para>The <see cref="SettingsWindow"/> is realized headless over the embedded default
/// (<see cref="SettingsViewModel.LoadFrom"/> — demo-mode-safe, reads no user profile), then the
/// tab under test is brought to the front (the <c>SelectTab</c> idiom from
/// <see cref="SettingsFileTabActionHierarchyTests"/>). The Remove buttons are unnamed template
/// buttons, so each set is located by its bound command INSTANCE: the axaml binds them via
/// reflection (<c>$parent[ItemsControl].DataContext.RemoveNamingCommand</c> /
/// <c>…RemoveIgnoreCommand</c> with <c>x:CompileBindings="False"</c>), which still materializes
/// the ONE generated VM command object on <c>Button.Command</c> at runtime (spot-checked; the
/// <c>Assert.Equal(count)</c> arms below keep the locator honest — zero matches can never pass).
/// The default ruleset seeds MULTIPLE naming cards (naming-gg/dl/ug) and many ignore rows all
/// sharing one command instance apiece (rows are distinguished by <c>CommandParameter</c>), so
/// the locator is NotEmpty+All, deliberately NOT <c>Assert.Single</c>.</para>
/// </summary>
public sealed class SettingsDestructiveClassTests
{
    /// <summary>ADR-036 D3 IN: every naming-rule card's <c>Remove</c> (a compound deletion —
    /// the rule, its pattern, and its exceptions list go in one click) carries
    /// <c>destructive</c> and never <c>accent</c>. Before #236 these buttons carried no class
    /// at all, so there is no <c>ghost</c> arm to move — the Contains is the whole red.</summary>
    [AvaloniaFact]
    public void NamingRuleRemoveButtons_AreDestructive_NeverAccent()
    {
        var (window, vm) = ShowTab("Naming");

        var removes = ButtonsForCommand(window, vm.RemoveNamingCommand);
        Assert.NotEmpty(removes);
        Assert.Equal(vm.Naming.Count, removes.Count); // one Remove per seeded card (3 in the default)
        Assert.All(removes, b =>
        {
            Assert.Contains("destructive", b.Classes);
            Assert.DoesNotContain("accent", b.Classes); // destructive is never the primary
        });

        window.Close();
    }

    /// <summary>ADR-036 D3 OUT (the executable dilution guard): every global-ignore row's
    /// <c>Remove</c> deletes exactly the row it sits beside, so none of them may carry
    /// <c>destructive</c> — and neither may the per-rule exception-row <c>Remove</c> (realized
    /// here by seeding one nesting exception through the VM's own Add command; the default
    /// ruleset ships zero exceptions, so the row would otherwise never realize).</summary>
    [AvaloniaFact]
    public void IgnoreAndExceptionRowRemoveButtons_AreNotDestructive()
    {
        var (window, vm) = ShowTab("Ignore");

        var ignoreRemoves = ButtonsForCommand(window, vm.RemoveIgnoreCommand);
        Assert.NotEmpty(ignoreRemoves);
        Assert.Equal(vm.Ignore.Count, ignoreRemoves.Count); // one Remove per seeded ignore row
        Assert.All(ignoreRemoves, b => Assert.DoesNotContain("destructive", b.Classes));

        // Exception rows: realize ONE nesting exception (empty dn-mode entry — presentation is
        // class-only, validity is a Save-gate concern) so its templated Remove exists to pin.
        vm.Nesting.AddExceptionCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var exceptionRemoves = ButtonsForCommand(window, vm.Nesting.RemoveExceptionCommand);
        Assert.NotEmpty(exceptionRemoves);
        Assert.All(exceptionRemoves, b => Assert.DoesNotContain("destructive", b.Classes));

        window.Close();
    }

    // --- helpers -------------------------------------------------------------------

    /// <summary>ALL realized, visible <see cref="Button"/>s bound to <paramref name="command"/> —
    /// the multi-row counterpart of the File-tab test's <c>ButtonForCommand</c> (these rows share
    /// ONE command instance and differ only by <c>CommandParameter</c>).</summary>
    private static List<Button> ButtonsForCommand(
        SettingsWindow window, System.Windows.Input.ICommand command) =>
        [.. window.GetVisualDescendants()
            .OfType<Button>()
            .Where(b => b.IsEffectivelyVisible && ReferenceEquals(b.Command, command))];

    /// <summary>Realize <see cref="SettingsWindow"/> over the embedded default and bring the
    /// tab whose header contains <paramref name="header"/> to the front — mirrors
    /// <see cref="SettingsFileTabActionHierarchyTests"/>' ShowFileTab/SelectTab idiom.</summary>
    private static (SettingsWindow Window, SettingsViewModel Vm) ShowTab(string header)
    {
        var vm = SettingsViewModel.LoadFrom(RulesetLoader.LoadDefault());
        var window = new SettingsWindow { DataContext = vm, Width = 1280, Height = 720 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var tabs = Assert.Single(window.GetVisualDescendants().OfType<TabControl>());
        var item = Assert.Single(
            tabs.GetVisualDescendants().OfType<TabItem>(),
            t => (t.Header?.ToString() ?? string.Empty)
                .Contains(header, System.StringComparison.OrdinalIgnoreCase));
        tabs.SelectedItem = item;
        Dispatcher.UIThread.RunJobs();

        return (window, vm);
    }
}
