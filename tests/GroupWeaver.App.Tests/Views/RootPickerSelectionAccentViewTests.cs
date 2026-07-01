using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
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
/// Pins issue #225 (Lever 4, the selection ACCENT half): the app-wide
/// <c>ListBoxItem:selected</c> style (App.axaml) re-fills a genuinely selected list row with the
/// brand translucent-accent band (<c>AccentSoftBrush</c>, dark #298B7BFF / light #1F6A5CFF —
/// <see cref="BrandTokens.AccentSoftHex"/>), REPLACING FluentTheme's default-BLUE
/// <c>SystemAccentColor</c> selection fill on the one genuine <c>ListBox.SelectedItem</c> list in
/// the app, the <see cref="RootPickerView"/> candidate list. The band is the shared ADR-026 D6
/// selection/focus role, the SAME token the converter-fed active-row band (pinned by
/// <see cref="ViolationsSidebarViewTests"/>) uses.
///
/// <para>This realizes the real picker over a connected stub, selects the candidate, and reads the
/// selected <see cref="ListBoxItem"/>'s templated <c>ContentPresenter#PART_ContentPresenter</c>
/// background — the target of the App.axaml <c>:selected /template/ ContentPresenter#PART_ContentPresenter</c>
/// setter — asserting it resolved to the brand <c>AccentSoftBrush</c> value (#298B7BFF, the DARK
/// theme the app ships with, <c>RequestedThemeVariant="Dark"</c>), NOT the Fluent default blue.
/// It also pins that the same App resource <c>AccentSoftBrush</c> IS the brand accent-soft token, so
/// the style-to-token wiring can't silently drift even if a template read ever went stale.</para>
///
/// <para>A fresh temp-dir <see cref="UiStateStore"/> is injected (the lab-environment rule: never
/// read the real <c>%APPDATA%\GroupWeaver\ui-state.json</c>, whose persisted <c>RailCollapsed:true</c>
/// could zero realized views locally while CI, on a fresh box, stays green).</para>
/// </summary>
public sealed class RootPickerSelectionAccentViewTests
{
    private const string CandidateDn = "CN=GG_Sales,OU=Groups,DC=stub,DC=lab";

    /// <summary>The DARK-theme brand accent-soft value the selected row must resolve to (the app
    /// ships <c>RequestedThemeVariant="Dark"</c>). Equals <see cref="BrandTokens.AccentSoftHex"/>.</summary>
    private const string AccentSoftDarkHex = "#298B7BFF";

    /// <summary>The genuinely-selected candidate row's templated content-presenter background is the
    /// brand accent-soft band (the App.axaml <c>ListBoxItem:selected</c> setter), NOT the Fluent
    /// default blue. Read from the actual realized <c>PART_ContentPresenter</c> — the setter's target.</summary>
    [AvaloniaFact]
    public async Task SelectedCandidateRow_FillsWithTheBrandAccentSoftBand_NotFluentBlue()
    {
        var (window, view, picker) = await ShowPickerAsync();
        try
        {
            var list = Assert.Single(view.GetVisualDescendants().OfType<ListBox>());

            // Select the candidate — the mandatory-entry gate + the row that must repaint.
            picker.SelectedCandidate = picker.Candidates[0];
            Dispatcher.UIThread.RunJobs();

            var container = Assert.IsType<ListBoxItem>(list.ContainerFromItem(picker.Candidates[0]));
            Assert.True(container.IsSelected, "guard: the row container is genuinely selected");

            // The App.axaml setter targets the templated ContentPresenter named PART_ContentPresenter;
            // read ITS background (the row's own Background stays unset — the :selected fill lives on
            // the presenter). Located by the template part name, the setter's exact target.
            var presenter = Assert.Single(
                container.GetVisualDescendants().OfType<ContentPresenter>(),
                p => p.Name == "PART_ContentPresenter");

            var brush = Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Background);
            Assert.Equal(Color.Parse(AccentSoftDarkHex), brush.Color);
            // Redundant to the value pin, but explicit: it is NOT the Fluent default accent blue.
            Assert.NotEqual(Color.Parse("#0078D7"), brush.Color); // Fluent SystemAccentColor
            Assert.NotEqual(Color.Parse("#0F6CBD"), brush.Color); // the legacy app blue #225 replaces

            window.Close();
        }
        catch
        {
            window.Close();
            throw;
        }
    }

    /// <summary>Belt-and-braces on the style-to-token WIRING (independent of any templated read):
    /// the App resource <c>AccentSoftBrush</c> the <c>:selected</c> setter binds resolves, under the
    /// app's shipped DARK theme, to the brand accent-soft token — so even if a future template read
    /// went stale, a wrong RESOURCE value (e.g. a revert to Fluent blue) still fails here.</summary>
    [AvaloniaFact]
    public void AccentSoftBrushResource_ResolvesToTheBrandAccentSoftToken_UnderDark()
    {
        var app = Assert.IsAssignableFrom<Application>(Application.Current);
        app.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;

        Assert.True(
            app.TryFindResource("AccentSoftBrush", Avalonia.Styling.ThemeVariant.Dark, out var resource),
            "the AccentSoftBrush App resource the :selected setter binds must resolve");
        var brush = Assert.IsAssignableFrom<ISolidColorBrush>(resource);
        Assert.Equal(Color.Parse(BrandTokens.AccentSoftHex), brush.Color);
        Assert.Equal(Color.Parse(AccentSoftDarkHex), brush.Color);
    }

    // --- helpers -------------------------------------------------------------------------

    private static async Task<(Window Window, RootPickerView View, RootPickerViewModel Picker)> ShowPickerAsync()
    {
        var candidate = new AdObject
        {
            Dn = CandidateDn,
            Kind = AdObjectKind.GlobalGroup,
            Name = "GG_Sales",
            SamAccountName = "GG_Sales",
        };
        var provider = new StubDirectoryProvider(
            Task.FromResult(new DirectoryConnection("stub directory", 5)))
        {
            RootCandidatesResult = Task.FromResult<IReadOnlyList<AdObject>>([candidate]),
        };
        var picker = new RootPickerViewModel(
            provider,
            new DirectoryConnection("stub directory", 5),
            onBack: () => { },
            onConfirmed: _ => { },
            uiStateStore: new UiStateStore(
                Directory.CreateTempSubdirectory("groupweaver-selaccent-uistate-").FullName));
        await picker.LoadCandidates;

        var view = new RootPickerView { DataContext = picker };
        var window = new Window { Content = view, Width = 800, Height = 600 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view, picker);
    }
}
