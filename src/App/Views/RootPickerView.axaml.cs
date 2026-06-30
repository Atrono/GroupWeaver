using Avalonia.Controls;
using Avalonia.Input;

using GroupWeaver.App.ViewModels;

namespace GroupWeaver.App.Views;

public sealed partial class RootPickerView : UserControl
{
    public RootPickerView()
    {
        InitializeComponent();
    }

    // Double-tapping a candidate row commits the selection (Load) - keyboard/pointer
    // parity with the IsDefault Load button. No-op unless a candidate is selected and
    // LoadRootCommand can execute (the same gate the Load button honors).
    private void OnCandidateDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is RootPickerViewModel vm && vm.LoadRootCommand.CanExecute(null))
        {
            vm.LoadRootCommand.Execute(null);
        }
    }
}
