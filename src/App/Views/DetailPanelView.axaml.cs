using Avalonia.Controls;
using Avalonia.Interactivity;

using GroupWeaver.App.ViewModels;

namespace GroupWeaver.App.Views;

public sealed partial class DetailPanelView : UserControl
{
    public DetailPanelView()
    {
        InitializeComponent();
    }

    /// <summary>The DN's one-click <b>Copy</b> button (Slice B / UX polish): writes the verbatim,
    /// never-canonicalized <see cref="DetailPanelModel.Dn"/> to the system clipboard via the Avalonia
    /// <c>TopLevel.Clipboard</c> — a pure clipboard write, the ONLY side effect. It is NEVER an AD
    /// write: no provider call, no directory mutation. After a successful write the model flips its
    /// transient "Copied" affordance (<see cref="DetailPanelModel.MarkDnCopied"/>). Mirrors
    /// <see cref="AuditView.OnCopySnippetClick"/>.</summary>
    private async void OnCopyDnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DetailPanelModel model)
        {
            return;
        }

        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(model.Dn);
            model.MarkDnCopied();
        }
    }
}
