using System.Linq;

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

using GroupWeaver.App.ViewModels;
using GroupWeaver.App.Views;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.App.Tests.Views;

/// <summary>
/// Pins the Slice B (UX polish) audit verdict-word PROMOTION at the VIEW layer (sister to
/// <see cref="AuditExportButtonsViewTests"/>'s AuditView hosting idiom). The band word
/// (<c>BandText</c>, <c>{Binding Band}</c>) is the qualitative health headline; Slice B promotes
/// it visually:
/// <list type="bullet">
///   <item><b><c>BandText</c> FontSize is 22</b> (was 18) — still SemiBold, still Name="BandText",
///   still bound to <see cref="AuditViewModel.Band"/>. A regression back to 18 fails here.</item>
///   <item><b>The "Directory health" label carries the <c>eyebrow</c> class</b> (demoted from
///   <c>subheading</c>) so the verdict word, not the label, leads the band card.</item>
/// </list>
///
/// <para>The <c>HealthAutomationName</c> + the Band binding itself are UNCHANGED by Slice B
/// (<see cref="AuditHealthBandTests"/> owns those); this fixture asserts ONLY the two promoted
/// presentation facts. No colour tint is asserted (the promotion adds none).</para>
///
/// <para>The audit is built over the GG_Circle_A ↔ GG_Circle_B cycle, so the report build the band
/// projects MUST terminate over it (the circular-case charter). <see cref="AuditView"/> owns no
/// graph renderer and no <c>UiStateStore</c> seam, so the #124 rail-collapse realization hazard does
/// not apply — it is a plain table UserControl.</para>
/// </summary>
public sealed class AuditBandProminenceViewTests
{
    private const string RootDn = "OU=Lab,DC=stub,DC=lab";
    private const string CircleADn = "CN=GG_Circle_A,OU=Lab,DC=stub,DC=lab";
    private const string CircleBDn = "CN=GG_Circle_B,OU=Lab,DC=stub,DC=lab";

    private const string DirectoryHealthLabel = "Directory health";

    [AvaloniaFact]
    public void BandText_IsPromotedTo22SemiBold_StillBoundToBand_AndLabelIsEyebrow()
    {
        var snapshot = CycleScope();
        var ruleset = RulesetLoader.LoadDefault();
        var report = RuleEngine.Evaluate(snapshot, ruleset); // MUST terminate over the A<->B cycle
        using var audit = new AuditViewModel(snapshot, report, ruleset, RootDn, onBack: () => { });

        var (window, view) = ShowAudit(audit);
        Dispatcher.UIThread.RunJobs();

        // The band word, located by its stable seam Name (template-independent anchor).
        var bandText = Assert.Single(
            view.GetVisualDescendants().OfType<TextBlock>(), t => t.Name == "BandText");

        // (1) Promoted to FontSize 22 (was 18) — the load-bearing Slice B change.
        Assert.Equal(22d, bandText.FontSize);

        // (2) Still SemiBold and still bound to the VM's Band (the headline content is unchanged).
        Assert.Equal(FontWeight.SemiBold, bandText.FontWeight);
        Assert.True(bandText.IsEffectivelyVisible);
        Assert.Equal(audit.Band, bandText.Text);

        // (3) The "Directory health" label is now an eyebrow (demoted from subheading) so the
        //     verdict word leads the card — caught by the rendered class set.
        var label = Assert.Single(
            view.GetVisualDescendants().OfType<TextBlock>(),
            t => t.IsEffectivelyVisible && t.Text == DirectoryHealthLabel);
        Assert.Contains("eyebrow", label.Classes);
        Assert.DoesNotContain("subheading", label.Classes);

        window.Close();
    }

    // --- helpers ------------------------------------------------------------------------

    /// <summary>Root OU + the GG_Circle_A ↔ GG_Circle_B nesting cycle (A→B→A) — the report build the
    /// band projects MUST terminate over it.</summary>
    private static DirectorySnapshot CycleScope()
    {
        var snapshot = new DirectorySnapshot();
        snapshot.AddObject(new AdObject { Dn = RootDn, Kind = AdObjectKind.OrganizationalUnit, Name = "Lab" });
        snapshot.AddObject(new AdObject { Dn = CircleADn, Kind = AdObjectKind.GlobalGroup, Name = "GG_Circle_A" });
        snapshot.AddObject(new AdObject { Dn = CircleBDn, Kind = AdObjectKind.GlobalGroup, Name = "GG_Circle_B" });
        snapshot.SetMembers(CircleADn, new[] { CircleBDn });
        snapshot.SetMembers(CircleBDn, new[] { CircleADn }); // closes the A->B->A cycle
        return snapshot;
    }

    /// <summary>The audit view in a sized, shown headless window (bindings live, the band card
    /// realized) — the AuditExportButtonsViewTests hosting idiom.</summary>
    private static (Window Window, AuditView View) ShowAudit(AuditViewModel vm)
    {
        var view = new AuditView { DataContext = vm };
        var window = new Window { Content = view, Width = 1280, Height = 720 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view);
    }
}
