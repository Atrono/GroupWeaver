using System;

using Avalonia.Styling;

using GroupWeaver.App.Settings;

namespace GroupWeaver.App.Tests.Fakes;

/// <summary>
/// Headless fake of the ADR-026-extension OS light/dark-preference seam
/// (<see cref="IPlatformThemeProvider"/>) — the same injection idiom as the other shell-ctor
/// fakes (a temp-dir <c>UiStateStore</c>, <c>FakeGraphRenderer</c>). The production
/// <c>DefaultPlatformThemeProvider</c> reads <c>PlatformSettings</c> (headless-untestable
/// global app state); this fake makes the resolved OS variant a settable test input and exposes
/// <see cref="RaiseOsPreferenceChanged"/> so a test can simulate a live OS theme switch and
/// assert the shell re-applies (only while the choice is System) and unsubscribes on Dispose.
///
/// <para>Tracks <see cref="SubscriberCount"/> so a test can prove the shell subscribes exactly
/// while System and tears the hook down on Dispose (the subscribe-only-while-System contract).
/// <see cref="GetOsPreferenceCallCount"/> records resolution calls. Mirrors the production
/// provider's never-throw contract — <see cref="GetOsPreference"/> always returns the (settable)
/// concrete <see cref="OsPreference"/> variant; the production <c>DefaultPlatformThemeProvider</c>
/// owns the "throws =&gt; Dark" arm internally (the shell applies the result without a guard).</para>
/// </summary>
internal sealed class FakePlatformThemeProvider : IPlatformThemeProvider
{
    private EventHandler? _osPreferenceChanged;

    /// <summary>The OS-resolved variant this fake returns; defaults to Dark (the production
    /// dark-first fallback). A test sets it to Light/Dark to drive System resolution.</summary>
    public ThemeVariant OsPreference { get; set; } = ThemeVariant.Dark;

    /// <summary>How many handlers are currently attached to <see cref="OsPreferenceChanged"/> —
    /// the shell attaches exactly one while the choice is System, zero otherwise / after Dispose.</summary>
    public int SubscriberCount { get; private set; }

    /// <summary>How many times <see cref="GetOsPreference"/> was called (resolution-call counter).</summary>
    public int GetOsPreferenceCallCount { get; private set; }

    public ThemeVariant GetOsPreference()
    {
        GetOsPreferenceCallCount++;
        return OsPreference;
    }

    public event EventHandler? OsPreferenceChanged
    {
        add
        {
            _osPreferenceChanged += value;
            SubscriberCount++;
        }

        remove
        {
            _osPreferenceChanged -= value;
            SubscriberCount--;
        }
    }

    /// <summary>Simulates a live OS light/dark switch — raises <see cref="OsPreferenceChanged"/> to
    /// every current subscriber (none after the shell unsubscribes / Disposes).</summary>
    public void RaiseOsPreferenceChanged() => _osPreferenceChanged?.Invoke(this, EventArgs.Empty);
}
