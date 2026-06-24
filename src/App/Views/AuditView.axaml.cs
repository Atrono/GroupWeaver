using Avalonia.Controls;
using Avalonia.Interactivity;

using GroupWeaver.App.ViewModels;

namespace GroupWeaver.App.Views;

/// <summary>
/// Audit step view (WP5 / #152): a pure-Avalonia table view binding the live
/// <see cref="ViewModels.AuditViewModel"/> summary. It owns NO graph renderer, so — unlike
/// WorkspaceView/PlanView/GapView — there is no GraphHost mount/detach plumbing here.
///
/// <para>WP5f (#160): the findings detail pane's <b>Copy</b> button (<see cref="OnCopySnippetClick"/>)
/// is the ONLY code-behind logic — it writes the read-only remediation snippet to the CLIPBOARD and
/// nothing else. <b>The snippet is NEVER executed:</b> there is no <c>Process.Start</c>, no shell
/// launch, no AD call on this path — the app only displays the inert text and copies it.</para>
/// </summary>
public sealed partial class AuditView : UserControl
{
    public AuditView() => InitializeComponent();

    /// <summary>The detail pane's <b>Copy</b> button (WP5f / #160): writes the selected finding's
    /// read-only PowerShell remediation snippet to the system clipboard via the Avalonia
    /// <c>TopLevel.Clipboard</c> — a pure clipboard write, the snippet's ONLY side effect. It is NEVER
    /// executed: no process is started, no shell is launched, no AD cmdlet runs. After a successful
    /// write the VM flips its transient "Copied" affordance (<see cref="AuditViewModel.MarkSnippetCopied"/>).</summary>
    private async void OnCopySnippetClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AuditViewModel vm || vm.Detail is not { } detail)
        {
            return;
        }

        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(detail.Snippet);
            vm.MarkSnippetCopied();
        }
    }
}
