using System.Globalization;

using Avalonia.Data.Converters;
using Avalonia.Media;

using GroupWeaver.Core.Diff;

namespace GroupWeaver.App.Views;

/// <summary>
/// THE App-side gap-diff palette (ADR-015 / #66): the Gap sidebar rows read these — in lock-step
/// with the slice-5 graph diff cues (the bundle's <c>node[diff=added|removed|unchecked]</c> overlay
/// colors), so the sidebar glyph color matches the color the graph paints. The hex values are the
/// reused diff palette: Added/EdgeAdded #2FAE4E (green — the plan introduces it),
/// Removed/EdgeRemoved #E0503A (red-orange — the plan drops it), UnverifiableArea #8A8F98 (gray —
/// the Ist side was never expanded). <see cref="ToGlyph"/> adds the redundant, colorblind-safe
/// symbol ("+" / "−" / "?") the row shows beside the colored square; <see cref="ToLabel"/> is the
/// row tooltip phrase.
///
/// Compiled-bindings-safe; mirrors the AP 3.4 <see cref="SeverityConverters"/> /
/// <see cref="AdObjectKindConverters"/> shape (one converter palette reused by the XAML binding seam
/// and the tests). The Gap view shows the Ist-vs-Plan DIFF, never rule severity, so this is a
/// separate palette from <see cref="SeverityConverters"/>.
/// </summary>
public static class GapKindConverters
{
    /// <summary>Gap kind → its diff-palette color (the glyph square fill).</summary>
    public static readonly IValueConverter ToBrush =
        new FuncValueConverter<GapKind, IBrush>(BrushFor);

    /// <summary>Gap kind → the redundant, colorblind-safe symbol ("+" / "−" / "?").</summary>
    public static readonly IValueConverter ToGlyph =
        new FuncValueConverter<GapKind, string>(GlyphFor);

    /// <summary>Gap kind → the row tooltip phrase.</summary>
    public static readonly IValueConverter ToLabel =
        new FuncValueConverter<GapKind, string>(LabelFor);

    private static IBrush BrushFor(GapKind kind) => kind switch
    {
        GapKind.NodeAdded or GapKind.EdgeAdded => BrandTokens.Added,
        GapKind.NodeRemoved or GapKind.EdgeRemoved => BrandTokens.Removed,
        _ => BrandTokens.Unchecked,
    };

    private static string GlyphFor(GapKind kind) => kind switch
    {
        GapKind.NodeAdded or GapKind.EdgeAdded => "+",
        GapKind.NodeRemoved or GapKind.EdgeRemoved => "−",
        _ => "?",
    };

    private static string LabelFor(GapKind kind) => kind switch
    {
        GapKind.NodeAdded => "Added object (in the plan, not the directory)",
        GapKind.NodeRemoved => "Removed object (in the directory, not the plan)",
        GapKind.EdgeAdded => "Added membership (in the plan, not the directory)",
        GapKind.EdgeRemoved => "Removed membership (in the directory, not the plan)",
        _ => "Unverifiable area (known in the directory but never expanded)",
    };
}

/// <summary>
/// The single-line GapView summary projector (ADR-015 / #66): renders a <see cref="GapSummary"/>
/// into the one legible line the gap header carries — node deltas, membership deltas, and the
/// unchecked-edge tally. Null-safe by contract: a <c>null</c> summary (before the first
/// <c>GapViewModel.RefreshAsync</c>) renders the empty string, and the view's <c>IsVisible</c>
/// binds the null-check so the line shows nothing until a diff is computed. Compiled-bindings-safe;
/// mirrors the <see cref="GapKindConverters"/> shape.
/// </summary>
public static class GapSummaryConverter
{
    /// <summary><see cref="GapSummary"/> → the one summary line; <c>null</c> → "".</summary>
    public static readonly IValueConverter ToLine =
        new FuncValueConverter<GapSummary?, string>(LineFor);

    private static string LineFor(GapSummary? summary) => summary is null
        ? string.Empty
        : string.Format(
            CultureInfo.InvariantCulture,
            "+{0} / −{1} objects · +{2} / −{3} memberships · {4} unchecked",
            summary.AddedNodes,
            summary.RemovedNodes,
            summary.AddedEdges,
            summary.RemovedEdges,
            summary.UncheckedEdges);
}
