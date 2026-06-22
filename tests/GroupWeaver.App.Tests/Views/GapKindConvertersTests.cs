using System.Globalization;

using Avalonia.Media;

using GroupWeaver.App.Views;
using GroupWeaver.Core.Diff;

using Xunit;

namespace GroupWeaver.App.Tests.Views;

/// <summary>
/// Pins the #106 gap-diff badge converters (ADR-015 / ADR-021): the ONE App-side palette the
/// Gap sidebar glyphs read, in lock-step with the slice-5 graph diff overlay colors. Mirrors
/// the AP 3.4 <see cref="SeverityConvertersTests"/> parity oracle — call each converter's
/// <c>Convert</c> directly (the XAML binding seam), never a private helper, and compare
/// resolved <see cref="Color"/> PROJECTIONS (never brush identity), so the test fails the
/// instant the badge palette diverges from the BrandTokens source of truth.
///
/// <para><see cref="GapKindConverters.ToTextBrush"/> is the on-badge diff-glyph INK (ADR-021 /
/// #106, WCAG 1.4.3): every diff kind shares ONE pure-black ink
/// (<see cref="BrandTokens.OnLightTextStrong"/> #000000). Black is the WCAG-correct ink because
/// the diff palette includes the Removed mid-tone (#E0503A) where the standard #1b1f27
/// <see cref="BrandTokens.OnLightText"/> reaches only 4.23:1 (FAIL); black clears all three diff
/// fills (Added 7.28:1 / Removed 5.38:1 / Unchecked 6.47:1). <see cref="GapKindConverters.ToBrush"/>
/// is the badge FILL — pinned here for parity so the consolidation left the fill palette
/// unmoved.</para>
/// </summary>
public sealed class GapKindConvertersTests
{
    /// <summary>The pinned on-badge ink: ONE pure-black token for every diff kind (#000000).</summary>
    private const string OnBadgeInkHex = "#000000";

    /// <summary>The five gap kinds the badge palette must cover (the <see cref="GapKind"/> domain).</summary>
    public static TheoryData<GapKind> AllGapKinds() => new()
    {
        GapKind.NodeAdded,
        GapKind.NodeRemoved,
        GapKind.EdgeAdded,
        GapKind.EdgeRemoved,
        GapKind.UnverifiableArea,
    };

    /// <summary>
    /// The on-badge text ink contract (ADR-021 / #106, WCAG 1.4.3): EVERY gap kind maps to the
    /// pure-black ink (#000000). Pins the rendered <see cref="Color"/> hex, not the brush instance,
    /// so the badge ink can never silently drift off pure black onto the too-weak #1b1f27.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllGapKinds))]
    public void ToTextBrush_MapsEveryGapKind_ToPureBlackInk(GapKind kind)
    {
        var brush = Assert.IsAssignableFrom<ISolidColorBrush>(TextBrush(kind));
        Assert.Equal(Color.Parse(OnBadgeInkHex), brush.Color);
    }

    /// <summary>
    /// The badge ink brush IS the consolidated <see cref="BrandTokens.OnLightTextStrong"/> role
    /// token (the single source of truth), not a hardcoded literal. Compares the resolved
    /// <see cref="Color"/> (a projection) against both the token brush and its hex, pinning the
    /// wiring without coupling to the brush instance.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllGapKinds))]
    public void ToTextBrush_IsWiredToBrandTokensOnLightTextStrong(GapKind kind)
    {
        var brush = Assert.IsAssignableFrom<ISolidColorBrush>(TextBrush(kind));

        Assert.Equal(BrandTokens.OnLightTextStrong.Color, brush.Color);
        Assert.Equal(Color.Parse(BrandTokens.OnLightTextStrongHex), brush.Color);
    }

    /// <summary>The five gap kinds collapse to ONE ink color — the on-badge ink is uniform
    /// across the badge strip (per-hue would mix #1b1f27 and black for no contrast benefit).</summary>
    [Fact]
    public void ToTextBrush_IsConstant_AcrossEveryGapKind()
    {
        var colors = new[]
            {
                GapKind.NodeAdded, GapKind.NodeRemoved, GapKind.EdgeAdded,
                GapKind.EdgeRemoved, GapKind.UnverifiableArea,
            }
            .Select(k => Assert.IsAssignableFrom<ISolidColorBrush>(TextBrush(k)).Color)
            .ToArray();

        Assert.Single(colors.Distinct());
    }

    /// <summary>
    /// The badge FILL palette is unmoved (parity check, ADR-015 / #66): Added/EdgeAdded →
    /// <see cref="BrandTokens.Added"/>, Removed/EdgeRemoved → <see cref="BrandTokens.Removed"/>,
    /// UnverifiableArea → <see cref="BrandTokens.Unchecked"/>. Compares resolved <see cref="Color"/>
    /// values (a projection), pinning that the #106 ink re-tone left the fill tokens transparent.
    /// </summary>
    [Theory]
    [InlineData(GapKind.NodeAdded, BrandTokens.AddedHex)]
    [InlineData(GapKind.EdgeAdded, BrandTokens.AddedHex)]
    [InlineData(GapKind.NodeRemoved, BrandTokens.RemovedHex)]
    [InlineData(GapKind.EdgeRemoved, BrandTokens.RemovedHex)]
    [InlineData(GapKind.UnverifiableArea, BrandTokens.UncheckedHex)]
    public void ToBrush_MapsEachGapKind_ToItsPinnedDiffFill(GapKind kind, string hex)
    {
        var brush = Assert.IsAssignableFrom<ISolidColorBrush>(Brush(kind));
        Assert.Equal(Color.Parse(hex), brush.Color);
    }

    /// <summary>Invoke the on-badge text-ink converter through its binding seam exactly as XAML does.</summary>
    private static object? TextBrush(GapKind kind) =>
        GapKindConverters.ToTextBrush.Convert(
            kind, typeof(IBrush), null, CultureInfo.InvariantCulture);

    /// <summary>Invoke the badge fill converter through its binding seam exactly as XAML does.</summary>
    private static object? Brush(GapKind kind) =>
        GapKindConverters.ToBrush.Convert(
            kind, typeof(IBrush), null, CultureInfo.InvariantCulture);
}
