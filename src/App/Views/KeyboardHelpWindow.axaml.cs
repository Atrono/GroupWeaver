using Avalonia.Controls;
using Avalonia.Interactivity;

namespace GroupWeaver.App.Views;

/// <summary>
/// The keyboard/gesture cheat sheet ("?" in the top command strip): a small modal
/// <see cref="Window"/> — its own top-level window, never layered over the workspace
/// GraphHost (ADR-001 airspace guardrail 5), mirroring <c>SettingsWindow</c>. Content
/// is STATIC (no VM); every row is a shortcut verified in code, so there is no logic
/// beyond closing.
/// </summary>
public sealed partial class KeyboardHelpWindow : Window
{
    public KeyboardHelpWindow()
    {
        InitializeComponent();
    }

    /// <summary>The footer Close button: closes the sheet. <c>IsCancel</c> handles the modal
    /// <c>ShowDialog</c> path; this also closes the non-modal fallback show.</summary>
    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
