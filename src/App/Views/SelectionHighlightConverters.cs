using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace GroupWeaver.App.Views;

/// <summary>
/// The AP 3.4 selection-sync HIGHLIGHT brush (ADR-010 §5): maps a row's
/// <see cref="ViewModels.ViolationRowModel.IsActive"/> flag to its
/// <c>Button.Background</c> — a subtle band behind the selected finding so the
/// graph/panel selection is visible in the sidebar too (the VM flips the flag,
/// this paints it). Active → the pinned <see cref="HighlightHex"/> band (20%-alpha
/// of the app accent #0F6CBD); inactive → a transparent brush (the row template's
/// literal default, so a cold row is indistinguishable from before).
///
/// The hex is LOAD-BEARING and pinned by the headless
/// <c>ViolationsSidebarViewTests</c> — change it only by editing this constant AND
/// that test together in one reviewed PR. Compiled-bindings-safe; mirrors the
/// <see cref="DetailPanelStateConverters"/> shape (one converter the XAML seam and
/// the test share).
/// </summary>
public static class SelectionHighlightConverters
{
    /// <summary>The pinned selection-highlight color: 20%-alpha of the app accent
    /// #0F6CBD. Must equal the <c>ViolationsSidebarViewTests</c> constant.</summary>
    private const string HighlightHex = "#330F6CBD";

    /// <summary><c>IsActive</c> → the row's background brush: the highlight band when
    /// active, transparent when cold.</summary>
    public static readonly IValueConverter IsActiveToBackground =
        new FuncValueConverter<bool, IBrush>(BackgroundFor);

    private static IBrush BackgroundFor(bool active) => active ? HighlightBrush : TransparentBrush;

    private static readonly ImmutableSolidColorBrush HighlightBrush = new(Color.Parse(HighlightHex));
    private static readonly ImmutableSolidColorBrush TransparentBrush = new(Colors.Transparent);
}
