namespace GroupWeaver.App.Settings;

/// <summary>
/// The five <see cref="CellChoice"/> verdicts in display order
/// (Allow / Deny / Error / Warning / Info) — the shared ItemsSource the
/// matrix-tab cell ComboBoxes and the Unlisted-fallback ComboBox bind (AP 3.3 /
/// S6). Exposed as a static array so XAML can bind it via <c>x:Static</c> without
/// per-cell enumeration; the order matches the loader's allow/deny/severity token
/// vocabulary so the chip palette reads top-to-bottom as allow → deny → overrides.
/// </summary>
public static class CellChoiceItems
{
    /// <summary>The five choices in display order.</summary>
    public static readonly CellChoice[] All =
    [
        CellChoice.Allow,
        CellChoice.Deny,
        CellChoice.Error,
        CellChoice.Warning,
        CellChoice.Info,
    ];
}
