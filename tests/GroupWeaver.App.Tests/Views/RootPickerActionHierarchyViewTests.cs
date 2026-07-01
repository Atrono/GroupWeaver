using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;

using GroupWeaver.App.Settings;
using GroupWeaver.App.Tests.Fakes;
using GroupWeaver.App.ViewModels;
using GroupWeaver.App.Views;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Providers;

using Xunit;

namespace GroupWeaver.App.Tests.Views;

/// <summary>
/// Pins issue #221 (Lever 2, action hierarchy) at the VIEW layer for the RootPicker step: the
/// primary <c>Load</c> button carries the <c>accent</c> class and the secondary <c>Back</c>
/// button carries the <c>ghost</c> class, establishing the primary/secondary hierarchy the
/// implementer set (only the <c>Classes=</c> attribute changed — the existing accent/ghost
/// classes are reused). These pins stop the hierarchy from silently drifting back to all-grey:
/// a class regression on either button fails here, not in a PNG review.
///
/// <para>The <see cref="RootPickerView"/> is realized headless over a live picker VM (a
/// connected stub with one candidate so the list realizes and the footer buttons render);
/// each button is located by its <c>Content</c> text (the two footer buttons are not
/// <c>x:Name</c>d, and the text is unique in this view — robust against template duplicates),
/// then asserted against the control's <see cref="Avalonia.StyledElement.Classes"/> collection.
/// The Load button is disabled until a candidate is selected, but disabled is not invisible —
/// it is realized (and its class set is static) regardless.</para>
///
/// <para>A fresh temp-dir <see cref="UiStateStore"/> is injected into the picker (and so into
/// any <see cref="WorkspaceViewModel"/> a confirm would build): the ctor never reads the real
/// <c>%APPDATA%\GroupWeaver\ui-state.json</c>, so a persisted <c>RailCollapsed:true</c> can
/// never zero realized views locally while CI (fresh box) stays green (lab-environment rule).</para>
/// </summary>
public sealed class RootPickerActionHierarchyViewTests
{
    /// <summary>The primary action: <c>Load</c> carries <c>accent</c> and NOT <c>ghost</c>
    /// (the two hierarchy classes are mutually exclusive on a single button).</summary>
    [AvaloniaFact]
    public async Task LoadButton_CarriesTheAccentPrimaryClass_NotGhost()
    {
        var (window, view) = await ShowPickerAsync();

        var load = ButtonByContent(view, "Load");

        Assert.Contains("accent", load.Classes);
        Assert.DoesNotContain("ghost", load.Classes);

        window.Close();
    }

    /// <summary>The secondary action: <c>Back</c> carries <c>ghost</c> and NOT <c>accent</c>.</summary>
    [AvaloniaFact]
    public async Task BackButton_CarriesTheGhostSecondaryClass_NotAccent()
    {
        var (window, view) = await ShowPickerAsync();

        var back = ButtonByContent(view, "Back");

        Assert.Contains("ghost", back.Classes);
        Assert.DoesNotContain("accent", back.Classes);

        window.Close();
    }

    // --- helpers -------------------------------------------------------------------

    /// <summary>Locate the single realized, visible <see cref="Button"/> whose <c>Content</c>
    /// equals <paramref name="content"/> (the two footer buttons are unnamed; their content
    /// text is unique in this view). <see cref="Assert.Single{T}(System.Collections.Generic.IEnumerable{T}, System.Func{T, bool})"/>
    /// makes the locator non-vacuous — a duplicate or a rename fails here.</summary>
    private static Button ButtonByContent(RootPickerView view, string content) =>
        Assert.Single(
            view.GetVisualDescendants().OfType<Button>(),
            b => b.IsEffectivelyVisible && (b.Content as string) == content);

    /// <summary>Render the real <see cref="RootPickerView"/> headless in a sized window over a
    /// live picker (candidates loaded so the list + footer realize).</summary>
    private static async Task<(Window Window, RootPickerView View)> ShowPickerAsync()
    {
        var provider = ConnectedStub(
            Obj("GG_Sales", "CN=GG_Sales,OU=Groups,DC=stub,DC=lab"));
        var picker = Picker(provider);
        await picker.LoadCandidates;

        var view = new RootPickerView { DataContext = picker };
        var window = new Window { Content = view, Width = 800, Height = 600 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view);
    }

    private static AdObject Obj(string name, string dn) =>
        new() { Dn = dn, Kind = AdObjectKind.GlobalGroup, Name = name };

    private static StubDirectoryProvider ConnectedStub(params AdObject[] candidates) =>
        new(Task.FromResult(new DirectoryConnection("stub directory", 5)))
        {
            RootCandidatesResult = Task.FromResult<IReadOnlyList<AdObject>>(candidates),
        };

    /// <summary>A picker over the stub with a temp-dir <see cref="UiStateStore"/> (so the ctor —
    /// and any confirm-built workspace — never reads real <c>%APPDATA%</c>; lab-environment rule).</summary>
    private static RootPickerViewModel Picker(StubDirectoryProvider provider) =>
        new(
            provider,
            new DirectoryConnection("stub directory", 5),
            onBack: () => { },
            onConfirmed: _ => { },
            uiStateStore: new UiStateStore(
                Directory.CreateTempSubdirectory("groupweaver-rootpicker-hierarchy-uistate-").FullName));
}
