using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GroupWeaver.Core.Providers;

namespace GroupWeaver.App.ViewModels;

/// <summary>
/// Connect step (AP 2.1): two explicit paths — live LDAP against the detected domain
/// (integrated Windows auth, current user context) and the embedded demo directory.
/// Error policy per ADR-003 D7: <see cref="DirectoryUnavailableException"/> surfaces
/// inline via <see cref="ErrorMessage"/>; every other exception bubbles (crash = bug).
/// On success the result is handed to the shell via callback — this step holds no state
/// the next step needs.
/// </summary>
public sealed partial class ConnectionViewModel : ObservableObject
{
    private readonly Func<bool, IDirectoryProvider> _providerFactory;
    private readonly Action<IDirectoryProvider, DirectoryConnection> _onConnected;

    /// <summary>True while a connect attempt is in flight; disables both commands.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConnectDemoCommand))]
    private bool _isConnecting;

    /// <summary>Error from the last failed attempt; <c>null</c> hides the inline error block.</summary>
    [ObservableProperty]
    private string? _errorMessage;

    public ConnectionViewModel(
        Func<bool, IDirectoryProvider> providerFactory,
        Action<IDirectoryProvider, DirectoryConnection> onConnected)
    {
        _providerFactory = providerFactory;
        _onConnected = onConnected;
    }

    /// <summary>
    /// Identity the live connect binds with — integrated Windows authentication, never a
    /// credential prompt (the provider takes no credentials by design).
    /// </summary>
    public string CurrentUserContext { get; } =
        $"{Environment.UserDomainName}\\{Environment.UserName}";

    /// <summary>Connect to the detected domain via the live LDAP provider.</summary>
    [RelayCommand(CanExecute = nameof(CanConnect))]
    private Task ConnectAsync() => ConnectCoreAsync(demo: false);

    /// <summary>Connect to the embedded demo directory.</summary>
    [RelayCommand(CanExecute = nameof(CanConnect))]
    private Task ConnectDemoAsync() => ConnectCoreAsync(demo: true);

    private bool CanConnect() => !IsConnecting;

    private async Task ConnectCoreAsync(bool demo)
    {
        IsConnecting = true;
        ErrorMessage = null;
        try
        {
            IDirectoryProvider provider = _providerFactory(demo);
            DirectoryConnection connection = await provider.ConnectAsync();
            _onConnected(provider, connection);
        }
        catch (DirectoryUnavailableException ex)
        {
            // UI counterpart of the --check stderr hint; only the live path earns it.
            ErrorMessage = demo
                ? ex.Message
                : $"{ex.Message}\nNo domain is reachable in this user context — try Demo mode for the embedded demo directory.";
        }
        finally
        {
            IsConnecting = false;
        }
    }
}
