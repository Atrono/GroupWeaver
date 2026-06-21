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

    /// <summary>Invoke the badge-brush converter through its binding seam exactly as XAML does.</summary>
    private static object? Brush(AdObjectKind kind) =>
        AdObjectKindConverters.ToBadgeBrush.Convert(
            kind, typeof(IBrush), null, CultureInfo.InvariantCulture);
}
