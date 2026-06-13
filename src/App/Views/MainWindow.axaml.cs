using System;
using System.ComponentModel;

using Avalonia.Controls;

using GroupWeaver.App.Export;
using GroupWeaver.App.ViewModels;

namespace GroupWeaver.App.Views;

public sealed partial class MainWindow : Window
{
    /// <summary>The ONE production export save-picker seam (AP 4.2.4 / ADR-013 §5, fixes #63):
    /// built once in <see cref="OnOpened"/> from this window's <c>TopLevel</c> and pushed into
    /// whatever exportable <c>CurrentStep</c> is (or becomes) current via <see cref="WireExport"/>.
    /// Without this the AP 4.1 export commands ship permanently disabled (the seam was never
    /// wired in production).</summary>
    private IExportFileDialogs? _exportDialogs;

    /// <summary>The shell whose <c>CurrentStep</c> changes are watched, kept so the subscription
    /// is torn down in <see cref="OnClosed"/> (no leak).</summary>
    private ShellViewModel? _wiredShell;

    /// <summary>The <c>CurrentStep</c>-change handler, kept so the exact delegate can be
    /// unsubscribed in <see cref="OnClosed"/>.</summary>
    private PropertyChangedEventHandler? _currentStepChanged;

    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Wires the production export seam (AP 4.2.4 / ADR-014; mirrors
    /// <c>SettingsWindow.OnOpened</c>, fixes #63): once the window is open it owns a
    /// <c>TopLevel</c>, so it builds ONE <see cref="StorageProviderExportFileDialogs"/> from it
    /// and pushes it into the current exportable step. It also subscribes to the shell's
    /// <c>CurrentStep</c> changes so a workspace or a Plan step created LATER is wired too. The
    /// headless screenshot/test path also opens the window but never invokes a picker, so this
    /// thin <c>[I]</c> wiring only ARMS the export commands there.
    /// </summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (GetTopLevel(this) is not { } topLevel || DataContext is not ShellViewModel shell)
        {
            return;
        }

        _exportDialogs = new StorageProviderExportFileDialogs(topLevel);
        _wiredShell = shell;

        // Arm whatever step is current right now…
        WireExport(shell.CurrentStep);

        // …and re-arm each new exportable step (a workspace, or a Plan step entered later).
        _currentStepChanged = (_, args) =>
        {
            if (args.PropertyName == nameof(ShellViewModel.CurrentStep))
            {
                WireExport(shell.CurrentStep);
            }
        };
        shell.PropertyChanged += _currentStepChanged;
    }

    /// <summary>Pushes the one export seam into the current step if it is exportable (a
    /// workspace or a Plan step). A no-op before the seam exists or for a non-exportable step.</summary>
    private void WireExport(object? step)
    {
        if (_exportDialogs is null)
        {
            return;
        }

        switch (step)
        {
            case WorkspaceViewModel workspace:
                workspace.UseExportFileDialogs(_exportDialogs);
                break;
            case PlanViewModel plan:
                plan.UseExportFileDialogs(_exportDialogs);
                break;
        }
    }

    /// <summary>
    /// Window teardown: unsubscribes the <c>CurrentStep</c> watcher (no leak — the shell
    /// outlives the window only in theory, but the handler closes over it) and disposes the
    /// shell, the one signal that cancels the workspace's in-flight scope load (AP 2.2 S6).
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        if (_wiredShell is not null && _currentStepChanged is not null)
        {
            _wiredShell.PropertyChanged -= _currentStepChanged;
        }

        _wiredShell = null;
        _currentStepChanged = null;

        (DataContext as ShellViewModel)?.Dispose();

        base.OnClosed(e);
    }
}
