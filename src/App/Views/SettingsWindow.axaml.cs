using Avalonia.Controls;

namespace GroupWeaver.App.Views;

/// <summary>
/// The modal settings / rule editor window (AP 3.3 / ADR-011 §1): a separate
/// top-level <see cref="Window"/> — ADR-003 D5's sanctioned escape hatch for
/// genuinely modal UI, never layered over the workspace GraphHost (ADR-001 airspace
/// guardrail 5). Its DataContext is the editable <c>SettingsViewModel</c> mirror
/// tree; all gated logic lives there, so the production <c>ShowDialog</c> path and
/// the headless <c>.Show()</c> screenshot path differ only in how the window is
/// shown (ADR-011 open-risk #3).
/// </summary>
public sealed partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }
}
