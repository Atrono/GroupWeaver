using Avalonia.Data.Converters;
using GroupWeaver.App.ViewModels;

namespace GroupWeaver.App.Views;

/// <summary>
/// Compiled-bindings-safe converters switching <see cref="DetailPanelView"/>'s
/// state sections from the <see cref="DetailPanelModel"/> projection (ADR-007 D3)
/// — each takes the model itself (null-safe: no DataContext, nothing visible).
/// </summary>
public static class DetailPanelStateConverters
{
    /// <summary>Never fetched — the "not loaded, expand/Refresh to resolve" hint.</summary>
    public static readonly IValueConverter IsNotLoaded =
        new FuncValueConverter<DetailPanelModel?, bool>(
            model => model?.State == DetailPanelState.NotLoaded);

    /// <summary>Fetched and genuinely unresolvable (FSP, AP 1.5).</summary>
    public static readonly IValueConverter IsUnresolvable =
        new FuncValueConverter<DetailPanelModel?, bool>(
            model => model?.State == DetailPanelState.Unresolvable);

    /// <summary>Loaded but the whitelist matched nothing — an honest empty note
    /// instead of a blank panel (never a fabricated row).</summary>
    public static readonly IValueConverter IsLoadedWithoutAttributes =
        new FuncValueConverter<DetailPanelModel?, bool>(
            model => model is { State: DetailPanelState.Loaded, Rows.Count: 0 });
}
