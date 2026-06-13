using System;

using Avalonia.Controls;

using GroupWeaver.App.Settings;

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

    /// <summary>The footer Cancel button: closes the editor, discarding in-memory edits
    /// (a refused or never-run gate persisted nothing). <c>IsCancel</c> handles the modal
    /// <c>ShowDialog</c> path; this also closes the non-modal fallback show.</summary>
    private void OnCancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    /// <summary>Installs the production file-picker seam (AP 3.3 / S7): once the window is
    /// open it owns a <c>TopLevel</c>, so the File-tab Import/Export commands reach the OS
    /// picker through <see cref="StorageProviderRulesetFileDialogs"/>
    /// (<c>TopLevel.GetTopLevel(window).StorageProvider</c>). The headless screenshot path
    /// also opens the window but never invokes a picker, so this thin <c>[I]</c> wiring is
    /// inert there. Gated logic stays in the VM behind <c>IRulesetFileDialogs</c>.</summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (DataContext is SettingsViewModel vm && GetTopLevel(this) is { } topLevel)
        {
            vm.UseFileDialogs(new StorageProviderRulesetFileDialogs(topLevel));
        }
    }
}
