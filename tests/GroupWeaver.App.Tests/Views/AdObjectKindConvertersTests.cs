using System.Globalization;

using Avalonia.Media;

using GroupWeaver.App.Views;
using GroupWeaver.Core.Model;

using Xunit;

namespace GroupWeaver.App.Tests.Views;

/// <summary>
/// Pins the AP 2.2 kind palette (ADR-021): <see cref="AdObjectKindConverters.ToBadgeBrush"/>
/// maps each <see cref="AdObjectKind"/> to its node/badge FILL, and after the #90 token
/// consolidation those fills come from <see cref="BrandTokens"/> — THE single source of
/// truth the graph bundle (graph.js PALETTE), the legend swatches, and the verify.mjs
/// PALETTE tripwire all hand-mirror. This is the C# end of that parity chain: it pins the
/// converter brushes EQUAL the BrandTokens role brushes (compared by resolved Color — a
/// projection, never brush identity), so the consolidation is verified transparent and a
/// drift surfaces here rather than only on the JS side. Calls the converter's
/// <c>Convert</c> through its binding seam exactly as XAML does (mirrors
/// <see cref="SeverityConvertersTests"/>).
/// </summary>
public sealed class AdObjectKindConvertersTests
{
    public static TheoryData<AdObjectKind, string> KindHexes() => new()
    {
        { AdObjectKind.User, "#038387" },
        { AdObjectKind.GlobalGroup, "#107C10" },
        { AdObjectKind.DomainLocalGroup, "#A14000" },
        { AdObjectKind.UniversalGroup, "#744DA9" },
        { AdObjectKind.OrganizationalUnit, "#0F6CBD" },
        { AdObjectKind.Computer, "#556070" },
        { AdObjectKind.External, "#757575" },
    };

    [Theory]
    [MemberData(nameof(KindHexes))]
    public void ToBadgeBrush_MapsEachKind_ToItsPinnedPaletteColor(AdObjectKind kind, string hex)
    {
        var brush = Assert.IsAssignableFrom<ISolidColorBrush>(Brush(kind));
        Assert.Equal(Color.Parse(hex), brush.Color);
    }

    /// <summary>The badge fill brushes ARE the consolidated <see cref="BrandTokens"/> kind
    /// brushes (ADR-021) — pinning the token wiring by resolved Color (a projection), so the
    /// consolidation can never silently re-tone a kind fill out from under the graph palette.</summary>
    [Theory]
    [MemberData(nameof(KindHexes))]
    public void ToBadgeBrush_IsWiredToBrandTokensKindFill(AdObjectKind kind, string hex)
    {
        var expected = kind switch
        {
            AdObjectKind.User => BrandTokens.User.Color,
            AdObjectKind.GlobalGroup => BrandTokens.GlobalGroup.Color,
            AdObjectKind.DomainLocalGroup => BrandTokens.DomainLocalGroup.Color,
            AdObjectKind.UniversalGroup => BrandTokens.UniversalGroup.Color,
            AdObjectKind.OrganizationalUnit => BrandTokens.OrganizationalUnit.Color,
            AdObjectKind.Computer => BrandTokens.Computer.Color,
            _ => BrandTokens.External.Color,
        };

        var brush = Assert.IsAssignableFrom<ISolidColorBrush>(Brush(kind));
        Assert.Equal(expected, brush.Color);
        // The MemberData hex is the same value, asserted independently so a wrong token
        // wiring AND a wrong hex can't cancel out.
        Assert.Equal(Color.Parse(hex), brush.Color);
    }

    /// <summary>All seven kind fills are distinct — the color channel must be unambiguous
    /// on its own (the badge letter is the colorblind-redundant channel, not a crutch).</summary>
    [Fact]
    public void ToBadgeBrush_IsInjective_AcrossAllSevenKinds()
    {
        var colors = Enum.GetValues<AdObjectKind>()
            .Select(k => Assert.IsAssignableFrom<ISolidColorBrush>(Brush(k)).Color)
            .ToArray();

        Assert.Equal(Enum.GetValues<AdObjectKind>().Length, colors.Distinct().Count());
    }

    // --- ToBadgeBorderBrush: the WCAG 1.4.11 lift-ring (#225 Lever 4) --------------------------

    /// <summary>The three kinds whose FILLS fall below the 3:1 graphical-object floor vs the dark
    /// page (DL 2.55 / UG 2.66 / Computer 2.59) — the ONLY kinds that get the #8A93A3 lift-ring
    /// (<see cref="BrandTokens.NodeLiftRing"/>), exactly mirroring graph.js's per-kind ring.</summary>
    public static TheoryData<AdObjectKind> RingKinds() => new()
    {
        AdObjectKind.DomainLocalGroup,
        AdObjectKind.UniversalGroup,
        AdObjectKind.Computer,
    };

    /// <summary>The kinds whose fills already clear 3:1 — no lift-ring, so a TRANSPARENT (invisible)
    /// border keeps the badge geometry uniform (the 2px border box exists on every badge; only its
    /// visibility differs).</summary>
    public static TheoryData<AdObjectKind> NonRingKinds() => new()
    {
        AdObjectKind.User,
        AdObjectKind.GlobalGroup,
        AdObjectKind.OrganizationalUnit,
        AdObjectKind.External,
    };

    /// <summary>The DL / UG / Computer badges carry the #8A93A3 lift-ring border brush — the exact
    /// <see cref="BrandTokens.NodeLiftRing"/> token (compared by resolved Color, a projection),
    /// pinned independently by its literal hex so a wrong token wiring AND a wrong hex can't cancel
    /// out. Invoked through the converter's binding seam exactly as the XAML BorderBrush binding does.</summary>
    [Theory]
    [MemberData(nameof(RingKinds))]
    public void ToBadgeBorderBrush_LiftsDlUgComputer_WithTheNodeLiftRing(AdObjectKind kind)
    {
        var border = Assert.IsAssignableFrom<ISolidColorBrush>(BorderBrush(kind));
        Assert.Equal(BrandTokens.NodeLiftRing.Color, border.Color);
        Assert.Equal(Color.Parse("#8A93A3"), border.Color);
    }

    /// <summary>Every OTHER kind gets a TRANSPARENT (no-lift) border — a real brush (so the 2px
    /// BorderThickness in the XAML paints an invisible box, keeping badge geometry uniform), never
    /// the lift-ring color. This is the "non-ring kind has a transparent/no-lift border" contract.</summary>
    [Theory]
    [MemberData(nameof(NonRingKinds))]
    public void ToBadgeBorderBrush_LeavesOtherKinds_Transparent(AdObjectKind kind)
    {
        var border = Assert.IsAssignableFrom<ISolidColorBrush>(BorderBrush(kind));
        Assert.Equal(Colors.Transparent, border.Color);
        Assert.NotEqual(BrandTokens.NodeLiftRing.Color, border.Color);
    }

    /// <summary>Full-partition guard: across ALL seven kinds, EXACTLY the three fill-contrast-failing
    /// kinds (DL/UG/Computer) get the lift-ring and the other four are transparent — so the ring set
    /// can never silently widen (over-decorating a fill that already passes) or narrow (dropping a
    /// lift a failing fill needs).</summary>
    [Fact]
    public void ToBadgeBorderBrush_RingsExactlyTheThreeContrastFailingKinds()
    {
        var ringed = Enum.GetValues<AdObjectKind>()
            .Where(k => Assert.IsAssignableFrom<ISolidColorBrush>(BorderBrush(k)).Color
                == BrandTokens.NodeLiftRing.Color)
            .ToHashSet();

        Assert.Equal(
            new HashSet<AdObjectKind>
            {
                AdObjectKind.DomainLocalGroup,
                AdObjectKind.UniversalGroup,
                AdObjectKind.Computer,
            },
            ringed);
    }

    /// <summary>Invoke the badge-brush converter through its binding seam exactly as XAML does.</summary>
    private static object? Brush(AdObjectKind kind) =>
        AdObjectKindConverters.ToBadgeBrush.Convert(
            kind, typeof(IBrush), null, CultureInfo.InvariantCulture);

    /// <summary>Invoke the badge-BORDER-brush converter through its binding seam exactly as the
    /// RootPickerView / DetailPanelView <c>BorderBrush</c> bindings do.</summary>
    private static object? BorderBrush(AdObjectKind kind) =>
        AdObjectKindConverters.ToBadgeBorderBrush.Convert(
            kind, typeof(IBrush), null, CultureInfo.InvariantCulture);
}
