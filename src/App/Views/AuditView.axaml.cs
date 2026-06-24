using Avalonia.Controls;

namespace GroupWeaver.App.Views;

/// <summary>
/// Audit step view (WP5 / #152): a pure-Avalonia table view binding the live
/// <see cref="ViewModels.AuditViewModel"/> summary. It owns NO graph renderer, so — unlike
/// WorkspaceView/PlanView/GapView — there is no GraphHost mount/detach plumbing here.
/// </summary>
public sealed partial class AuditView : UserControl
{
    public AuditView() => InitializeComponent();
}
