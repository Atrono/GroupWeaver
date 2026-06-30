using System.ComponentModel;

using GroupWeaver.App.ViewModels;
using GroupWeaver.Core.Model;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// Pins the Slice B (UX polish) detail-panel DN-copy affordance at the MODEL level
/// (the <see cref="DetailPanelViewTests"/> companion pins the rendered button + caption).
/// <see cref="DetailPanelModel.MarkDnCopied"/> flips the transient
/// <see cref="DetailPanelModel.CopiedDn"/> false→true and raises
/// <see cref="INotifyPropertyChanged"/> for that one property — the SAME transient-affordance
/// pattern as <c>AuditViewModel.MarkSnippetCopied</c>/<c>SnippetCopied</c>
/// (<see cref="AuditFindingDetailTests"/>).
///
/// <para><b>Clipboard handling.</b> The model never touches the clipboard — copying is a
/// pure clipboard write the VIEW owns (<c>DetailPanelView.OnCopyDnClick</c> →
/// <c>TopLevel.Clipboard.SetTextAsync(model.Dn)</c>), mirroring
/// <c>AuditView.OnCopySnippetClick</c>. That existing precedent is likewise tested at the
/// model level only (no real headless clipboard dependency); this fixture follows it, so the
/// flip contract is pinned deterministically without a flaky <c>TopLevel.Clipboard</c> seam.
/// The DN is a typed member (<see cref="DetailPanelModel.Dn"/>), so copying it introduces no
/// non-whitelisted binding — the ADR-007 D2 baseline is untouched.</para>
/// </summary>
public sealed class DetailPanelCopyDnTests
{
    private const string SalesDn = "CN=GG_Sales,OU=Lab,DC=stub,DC=lab";

    /// <summary>A fresh projection starts with the affordance OFF — every selection rebuilds
    /// the model (Build is the choke point), so the default <c>false</c> resets it per DN.</summary>
    [Fact]
    public void FreshlyBuiltModel_HasCopiedDnFalse()
    {
        var model = BuildSalesModel();

        Assert.False(model.CopiedDn);
    }

    /// <summary>
    /// <see cref="DetailPanelModel.MarkDnCopied"/> flips <see cref="DetailPanelModel.CopiedDn"/>
    /// false→true and raises <see cref="INotifyPropertyChanged.PropertyChanged"/> for exactly
    /// <c>CopiedDn</c> — the binding the view's transient "Copied" TextBlock observes.
    /// </summary>
    [Fact]
    public void MarkDnCopied_FlipsCopiedDnFalseToTrue_AndRaisesPropertyChanged()
    {
        var model = BuildSalesModel();
        Assert.False(model.CopiedDn);

        var changed = new List<string?>();
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        model.MarkDnCopied();

        Assert.True(model.CopiedDn);
        Assert.Contains(nameof(DetailPanelModel.CopiedDn), changed);
    }

    /// <summary>
    /// The notification only fires on a real transition: a second
    /// <see cref="DetailPanelModel.MarkDnCopied"/> while already copied is a no-op (the
    /// setter's equality guard) — the affordance never re-flickers and no spurious
    /// PropertyChanged is raised.
    /// </summary>
    [Fact]
    public void MarkDnCopied_WhenAlreadyCopied_DoesNotRaiseAgain()
    {
        var model = BuildSalesModel();
        model.MarkDnCopied();
        Assert.True(model.CopiedDn);

        var changed = new List<string?>();
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        model.MarkDnCopied();

        Assert.True(model.CopiedDn);
        Assert.DoesNotContain(nameof(DetailPanelModel.CopiedDn), changed);
    }

    /// <summary>The model that the view copies from — a loaded GG with the typed Dn the
    /// clipboard write reads. Built through the single choke point (no fabrication).</summary>
    private static DetailPanelModel BuildSalesModel()
    {
        var snapshot = new DirectorySnapshot();
        snapshot.AddObject(new AdObject
        {
            Dn = SalesDn,
            Kind = AdObjectKind.GlobalGroup,
            Name = "GG_Sales",
            SamAccountName = "GG_Sales",
        });

        var model = DetailPanelModel.Build(snapshot, SalesDn);
        Assert.NotNull(model);
        Assert.Equal(SalesDn, model.Dn); // the verbatim DN the Copy button writes to the clipboard
        return model;
    }
}
