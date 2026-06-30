using Avalonia;
using Avalonia.Platform;
using Avalonia.Styling;

namespace GroupWeaver.App.Settings;

/// <summary>
/// The OS light/dark preference seam (ADR-026 extension — the System/auto theme state). The
/// production <see cref="DefaultPlatformThemeProvider"/> reads
/// <c>Application.Current.PlatformSettings.GetColorValues().ThemeVariant</c> and re-raises the
/// platform's <c>ColorValuesChanged</c> event; headless tests inject a fake. Threaded into
/// <see cref="ViewModels.ShellViewModel"/> as an optional ctor param (defaulting to the production
/// impl) exactly as <see cref="UiStateStore"/> is — so existing call sites/tests keep compiling.
///
/// <para>NEVER-THROW by contract (the ADR-026 D4 degradation discipline):
/// <see cref="GetOsPreference"/> always returns a concrete <see cref="ThemeVariant.Light"/> or
/// <see cref="ThemeVariant.Dark"/> (never <see cref="ThemeVariant.Default"/>), falling back to
/// <see cref="ThemeVariant.Dark"/> (dark-first) on a null <c>PlatformSettings</c> or any read
/// failure — the shell can apply the result without a guard.</para>
/// </summary>
public interface IPlatformThemeProvider
{
    /// <summary>The OS-resolved theme preference — a concrete <see cref="ThemeVariant.Light"/> or
    /// <see cref="ThemeVariant.Dark"/>, never <see cref="ThemeVariant.Default"/>. Falls back to
    /// <see cref="ThemeVariant.Dark"/> (dark-first) when the preference cannot be read.</summary>
    ThemeVariant GetOsPreference();

    /// <summary>Raised when the OS light/dark preference changes at runtime (a live OS theme
    /// switch) — the shell re-applies the resolved variant while the choice is System.</summary>
    event EventHandler? OsPreferenceChanged;
}

/// <summary>
/// Production <see cref="IPlatformThemeProvider"/>: wraps
/// <c>Application.Current.PlatformSettings</c>, mapping its
/// <see cref="PlatformColorValues.ThemeVariant"/> to an Avalonia <see cref="ThemeVariant"/> and
/// re-raising <c>ColorValuesChanged</c> as <see cref="OsPreferenceChanged"/>. Never-throw: a null
/// app / null <c>PlatformSettings</c> / any read failure yields <see cref="ThemeVariant.Dark"/>,
/// and the underlying event is subscribed lazily only when a listener attaches (so a shell that
/// never selects System never touches the platform event).
/// </summary>
public sealed class DefaultPlatformThemeProvider : IPlatformThemeProvider
{
    private EventHandler? _osPreferenceChanged;

    /// <inheritdoc />
    public ThemeVariant GetOsPreference()
    {
        try
        {
            if (Application.Current?.PlatformSettings is { } settings
                && settings.GetColorValues().ThemeVariant == PlatformThemeVariant.Light)
            {
                return ThemeVariant.Light;
            }
        }
        catch
        {
            // Never-throw (ADR-026 D4): any platform read failure falls through to dark-first.
        }

        return ThemeVariant.Dark;
    }

    /// <inheritdoc />
    public event EventHandler? OsPreferenceChanged
    {
        add
        {
            // Lazily subscribe to the platform event on the FIRST listener (a shell only listens
            // while the choice is System) — so a Dark/Light-only session never hooks the OS event.
            if (_osPreferenceChanged is null && Application.Current?.PlatformSettings is { } settings)
            {
                settings.ColorValuesChanged += OnPlatformColorValuesChanged;
            }

            _osPreferenceChanged += value;
        }

        remove
        {
            _osPreferenceChanged -= value;
            if (_osPreferenceChanged is null && Application.Current?.PlatformSettings is { } settings)
            {
                settings.ColorValuesChanged -= OnPlatformColorValuesChanged;
            }
        }
    }

    private void OnPlatformColorValuesChanged(object? sender, PlatformColorValues e) =>
        _osPreferenceChanged?.Invoke(this, EventArgs.Empty);
}
