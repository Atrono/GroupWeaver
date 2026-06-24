using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

using GroupWeaver.App.Graph;
using GroupWeaver.App.Tests.Fakes;
using GroupWeaver.App.ViewModels;
using GroupWeaver.App.Views;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Providers;

using Xunit;

namespace GroupWeaver.App.Tests.Views;

/// <summary>
/// SOUNDNESS PINS for the #91 type scale &amp; voice (branch <c>feat/91-type-scale</c>).
/// The production type treatment lands entirely in XAML — a removed
/// <c>&lt;StyleInclude Source="/Styles/Typography.axaml"/&gt;</c> (or a dropped
/// <c>Classes</c>) would silently revert every type decision with no test catching it.
/// These facts pin the two INTENTIONAL moves of #91 by reading EFFECTIVE applied
/// property values (FontWeight enum, FontSize double, FontFamily name string) — never
/// the raw XAML, never control identity — through the SAME real headless render path the
/// screenshot/detail-panel view tests use (<c>TestAppBuilder</c> boots the real
/// <see cref="App"/>, so FluentTheme + the #91 <c>Tokens.axaml</c> resources + the
/// <c>Typography.axaml</c> StyleInclude are all applied before any value is read).
///
/// <para>Pinned: (1) the wordmark's two-weight Light/SemiBold split — the ONE voice move;
/// (2) the <c>display</c> class is actually APPLIED (effective FontSize 34 + SemiBold) —
/// so a dropped Typography include, leaving the class to resolve to nothing, FAILS here;
/// (3) DN honesty — the detail-panel DN (<c>dn-strong</c>) and sAMAccountName (<c>dn</c>)
/// resolve their effective <see cref="FontFamily"/> to the mono stack (first family
/// <c>Cascadia Mono</c>), proving the <c>dn</c>/<c>dn-strong</c> class applied AND the
/// <c>FontFamilyMono</c> StaticResource resolved. Deliberately NOT pinned: letter-spacing
/// pixels, opacity — the brittle, non-load-bearing settings.</para>
/// </summary>
public sealed class TypographyTests
{
    /// <summary>The primary family of the <c>FontFamilyMono</c> token
    /// (<c>"Cascadia Mono, Consolas, Courier New, monospace"</c>, Tokens.axaml) — the first
    /// name Avalonia parses out of the comma stack and exposes via <see cref="FontFamily.Name"/>.</summary>
    private const string MonoPrimaryFamily = "Cascadia Mono";

    // --- (1) + (2) the wordmark: two-weight voice + the display class is applied ----------

    /// <summary>
    /// PIN 1 (voice): the <c>ConnectionView</c> wordmark is ONE <c>display</c>-classed
    /// <see cref="TextBlock"/> carrying exactly two <see cref="Run"/> inlines whose
    /// <see cref="Run.FontWeight"/>s are <see cref="FontWeight.Light"/> then
    /// <see cref="FontWeight.SemiBold"/>, in order — the single intentional voice move
    /// ("Group" light, "Weaver" semibold). These weights are LOCAL on the runs, so this
    /// pins the split itself, independent of the class.
    ///
    /// <para>PIN 2 (include applied): the wordmark TextBlock's EFFECTIVE
    /// <see cref="TextBlock.FontSize"/> is 34 and <see cref="TextBlock.FontWeight"/> is
    /// <see cref="FontWeight.SemiBold"/> — both come ONLY from the <c>display</c> class in
    /// <c>Typography.axaml</c>. Remove the Typography StyleInclude and the class resolves
    /// to nothing: the FontSize falls back to the Fluent default and this assertion FAILS.
    /// That is the load-bearing "the include is wired" proof the raw XAML can't give.</para>
    /// </summary>
    [AvaloniaFact]
    public void Wordmark_HasTwoWeightSplit_AndAppliesTheDisplayClass()
    {
        var (window, view) = ShowConnectionView();

        // The wordmark is the one TextBlock carrying the `display` class.
        var wordmark = Assert.Single(
            view.GetVisualDescendants().OfType<TextBlock>(),
            t => t.Classes.Contains("display"));

        // PIN 1 — the two-weight voice split, in order: "Group" Light + "Weaver" SemiBold.
        Assert.NotNull(wordmark.Inlines);
        var runs = wordmark.Inlines!.OfType<Run>().ToList();
        Assert.Equal(2, runs.Count);
        Assert.Equal(
            new[] { FontWeight.Light, FontWeight.SemiBold },
            runs.Select(r => r.FontWeight));

        // PIN 2 — the `display` class is actually APPLIED (effective values). A removed
        // Typography StyleInclude makes the class resolve to nothing and breaks these.
        Assert.Equal(34d, wordmark.FontSize);
        Assert.Equal(FontWeight.SemiBold, wordmark.FontWeight);

        window.Close();
    }

    // --- (3) DN honesty: the detail-panel DN + SAM resolve to the mono stack --------------

    /// <summary>
    /// PIN 3 (mono DN honesty): clicking a group renders the detail panel's DN as a
    /// <c>dn-strong</c> <see cref="SelectableTextBlock"/> and its sAMAccountName as a
    /// <c>dn</c> <see cref="TextBlock"/>; each must resolve its EFFECTIVE
    /// <see cref="FontFamily"/> to the mono stack — primary family <c>Cascadia Mono</c> —
    /// proving BOTH that the <c>dn</c>/<c>dn-strong</c> class applied AND that the
    /// <c>FontFamilyMono</c> StaticResource (Tokens.axaml) resolved. Drop the Typography
    /// include and these controls fall back to the default proportional UI font: FAIL.
    /// Reads the rendered control's effective <see cref="FontFamily"/> (the preferred path
    /// the task asks for), not the raw setter.
    /// </summary>
    [AvaloniaFact]
    public async Task DetailPanel_DnAndSamAccountName_RenderInTheMonoStack()
    {
        var fake = new FakeGraphRenderer();
        var vm = Workspace(Provider(PanelScope()), () => fake);
        var (window, view) = ShowWorkspace(vm);
        await vm.Initialization;

        // GG_Sales carries BOTH a Name and a SamAccountName, so the click renders the
        // dn-strong DN AND the dn SAM row (the SAM block hides when SamAccountName is null).
        fake.RaiseNodeClicked(SalesDn, "GlobalGroup");
        Dispatcher.UIThread.RunJobs();

        var region = Region(view, "DetailPanelRegion");

        // The DN: the `dn-strong` SelectableTextBlock bound to the verbatim DN.
        var dnStrong = Assert.Single(
            region.GetVisualDescendants().OfType<SelectableTextBlock>(),
            t => t.IsEffectivelyVisible && t.Classes.Contains("dn-strong"));
        Assert.Equal(SalesDn, dnStrong.Text);
        AssertMono(dnStrong.FontFamily, "the detail-panel DN (dn-strong)");

        // The sAMAccountName: the `dn` TextBlock (a plain TextBlock, not SelectableTextBlock).
        var samRow = Assert.Single(
            region.GetVisualDescendants().OfType<TextBlock>(),
            t => t.IsEffectivelyVisible
                && t.Classes.Contains("dn")
                && t is not SelectableTextBlock);
        Assert.Equal("GG_Sales", samRow.Text); // the fixture's SamAccountName
        AssertMono(samRow.FontFamily, "the detail-panel sAMAccountName (dn)");

        window.Close();
    }

    /// <summary>Asserts <paramref name="family"/> is the mono stack — its primary family is
    /// <c>Cascadia Mono</c> — so the class applied and the <c>FontFamilyMono</c> StaticResource
    /// resolved (a fallback to the default proportional UI font has a different primary name).
    /// Checks both <see cref="FontFamily.Name"/> (the first family) and
    /// <see cref="FontFamily.FamilyNames"/> for robustness across the comma-stack parse.</summary>
    private static void AssertMono(FontFamily family, string what)
    {
        Assert.True(
            family.Name == MonoPrimaryFamily || family.FamilyNames.Contains(MonoPrimaryFamily),
            $"{what} must render in the mono stack (primary family '{MonoPrimaryFamily}'); "
            + $"was '{family.Name}' [{string.Join(", ", family.FamilyNames)}] — the dn/dn-strong "
            + "class or the FontFamilyMono StaticResource did not apply");
    }

    // --- harness (mirrors DetailPanelViewTests; the real App-styled headless render path) ---

    private const string RootDn = "OU=Lab,DC=stub,DC=lab";
    private const string SalesDn = "CN=GG_Sales,OU=Lab,DC=stub,DC=lab";

    /// <summary>The wordmark fixture: a <c>ConnectionView</c> in a sized, shown headless
    /// window (so the App styles apply and a layout pass runs). The VM's commands never fire
    /// here — the wordmark is static text — so the provider factory is a never-called stub
    /// and <c>onConnected</c> is a no-op.</summary>
    private static (Window Window, ConnectionView View) ShowConnectionView()
    {
        var vm = new ConnectionViewModel(
            _ => new StubDirectoryProvider(Task.FromResult(new DirectoryConnection("stub", 0))),
            (_, _, _) => { });

        var view = new ConnectionView { DataContext = vm };
        var window = new Window { Content = view, Width = 1280, Height = 720 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view);
    }

    private static AdObject Obj(string name, string dn, AdObjectKind kind) =>
        new() { Dn = dn, Kind = kind, Name = name };

    /// <summary>Minimal detail-panel scope: a root OU and GG_Sales carrying BOTH a Name and a
    /// SamAccountName so the click renders the dn-strong DN AND the dn SAM block.</summary>
    private static DirectorySnapshot PanelScope()
    {
        var snapshot = new DirectorySnapshot();
        snapshot.AddObject(Obj("Lab", RootDn, AdObjectKind.OrganizationalUnit));
        snapshot.AddObject(new AdObject
        {
            Dn = SalesDn,
            Kind = AdObjectKind.GlobalGroup,
            Name = "GG_Sales",
            SamAccountName = "GG_Sales",
        });
        return snapshot;
    }

    private static StubDirectoryProvider Provider(DirectorySnapshot snapshot) =>
        new(Task.FromResult(new DirectoryConnection("stub directory", 5)))
        {
            LoadScopeResult = Task.FromResult(snapshot),
        };

    // Fresh temp-dir UiStateStore (#124 / ADR-022 D4): never touches the real %APPDATA%
    // ui-state.json, so a persisted RailCollapsed:true cannot collapse the right rail and
    // starve the detail-panel realization this file asserts over.
    private static WorkspaceViewModel Workspace(
        StubDirectoryProvider provider, Func<IGraphRenderer> rendererFactory) =>
        new(
            provider,
            Obj("Lab", RootDn, AdObjectKind.OrganizationalUnit),
            new DirectoryConnection("stub directory", 5),
            webView2Missing: false,
            rendererFactory,
            uiStateStore: new GroupWeaver.App.Settings.UiStateStore(
                System.IO.Directory.CreateTempSubdirectory("groupweaver-typography-uistate-").FullName));

    private static (Window Window, WorkspaceView View) ShowWorkspace(WorkspaceViewModel vm)
    {
        var view = new WorkspaceView { DataContext = vm };
        var window = new Window { Content = view, Width = 1280, Height = 720 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view);
    }

    private static ContentControl Region(WorkspaceView view, string name) =>
        Assert.Single(
            view.GetVisualDescendants().OfType<ContentControl>(), c => c.Name == name);
}
