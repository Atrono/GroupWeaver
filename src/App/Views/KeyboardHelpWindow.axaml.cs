using Avalonia.Controls;
using Avalonia.Interactivity;
using GroupWeaver.App.Feedback;

namespace GroupWeaver.App.Views;

/// <summary>
/// The keyboard/gesture cheat sheet ("?" in the top command strip): a small modal
/// <see cref="Window"/> — its own top-level window, never layered over the workspace
/// GraphHost (ADR-001 airspace guardrail 5), mirroring <c>SettingsWindow</c>. Content
/// is STATIC (no VM); every row is a shortcut verified in code, so there is no logic
/// beyond closing and the footer's "Report an issue…" browser link.
/// </summary>
public sealed partial class KeyboardHelpWindow : Window
{
    public KeyboardHelpWindow()
    {
        InitializeComponent();
    }

    /// <summary>The footer link's target: the opener (the shell's F1/"?" command) passes the
    /// state-prefilled <see cref="UxFeedbackLink.BuildUrl"/> URL in; a bare construction
    /// (headless tests) falls back to the un-prefilled form. The window itself never knows
    /// shell state — it just opens what it was given.</summary>
    internal string ReportIssueUrl { get; init; } = UxFeedbackLink.TemplateOnlyUrl;

    /// <summary>The footer Close button: closes the sheet. <c>IsCancel</c> handles the modal
    /// <c>ShowDialog</c> path; this also closes the non-modal fallback show.</summary>
    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>The footer "Report an issue…" link: opens the prefilled GitHub issue form in
    /// the default browser (never-throw — <see cref="UxFeedbackLink.OpenInBrowser"/>).</summary>
    private void OnReportIssueClick(object? sender, RoutedEventArgs e) =>
        UxFeedbackLink.OpenInBrowser(ReportIssueUrl);
}
