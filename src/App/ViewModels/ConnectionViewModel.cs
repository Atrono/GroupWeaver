using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GroupWeaver.App.Diagnostics;
using GroupWeaver.Core.Providers;
using GroupWeaver.Providers;

namespace GroupWeaver.App.ViewModels;

/// <summary>
/// Connect step (AP 2.1): two explicit paths — live LDAP against the detected domain
/// (integrated Windows auth, current user context) and the embedded demo directory.
/// Error policy per ADR-003 D7: <see cref="DirectoryUnavailableException"/> surfaces
/// inline via <see cref="ErrorMessage"/>; every other exception bubbles (crash = bug).
/// On success the result is handed to the shell via callback — this step holds no state
/// the next step needs.
/// <para>ADR-031: an optional "Advanced — target a specific domain or DC" disclosure
/// (collapsed by default) feeds <see cref="TargetServer"/> / <see cref="TargetBaseDn"/>
/// into a targeted provider factory; blank fields keep the zero-config serverless
/// default. Still integrated auth, still no stored credentials — the two fields are the
/// one new untrusted input, validated via <see cref="ConnectionTarget"/> before the bind.</para>
/// </summary>
public sealed partial class ConnectionViewModel : ObservableObject
{
    private readonly Func<bool, IDirectoryProvider> _providerFactory;
    private readonly Func<string?, string?, IDirectoryProvider>? _targetedProviderFactory;
    private readonly Action<IDirectoryProvider, DirectoryConnection, bool> _onConnected;

    /// <summary>True while a connect attempt is in flight; disables both commands.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConnectDemoCommand))]
    private bool _isConnecting;

    /// <summary>Error from the last failed attempt; <c>null</c> hides the inline error block.</summary>
    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>ADR-031 D1: when true the Advanced disclosure is open; collapsed by default so the
    /// zero-config common case is visually unchanged. Toggled by <see cref="ToggleAdvancedCommand"/>.</summary>
    [ObservableProperty]
    private bool _isAdvancedExpanded;

    /// <summary>ADR-031 D1: the optional server/DC host to target. Blank = serverless bind (today's
    /// default). The one new untrusted input — validated as a bare host via <see cref="ConnectionTarget"/>
    /// before the bind. Drives the <see cref="TargetLine"/> pre-bind confirmation.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetLine))]
    [NotifyPropertyChangedFor(nameof(TargetProse))]
    private string? _targetServer;

    /// <summary>ADR-031 D1: the optional base DN to target. Blank = read <c>defaultNamingContext</c>
    /// from RootDSE (today's default). Validated as a well-formed DN; used ONLY as a search base, never
    /// composed into an LDAP filter. Drives the <see cref="TargetLine"/> pre-bind confirmation.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetLine))]
    [NotifyPropertyChangedFor(nameof(TargetProse))]
    [NotifyPropertyChangedFor(nameof(TargetDn))]
    private string? _targetBaseDn;

    public ConnectionViewModel(
        Func<bool, IDirectoryProvider> providerFactory,
        Action<IDirectoryProvider, DirectoryConnection, bool> onConnected,
        Func<string?, string?, IDirectoryProvider>? targetedProviderFactory = null)
    {
        _providerFactory = providerFactory;
        _onConnected = onConnected;
        _targetedProviderFactory = targetedProviderFactory;
    }

    /// <summary>
    /// Identity the live connect binds with — integrated Windows authentication, never a
    /// credential prompt (the provider takes no credentials by design).
    /// </summary>
    public string CurrentUserContext { get; } =
        $"{Environment.UserDomainName}\\{Environment.UserName}";

    /// <summary>
    /// ADR-031 D4: the pre-bind target confirmation surfaced in the Connect helper text, so the
    /// auditor confirms WHICH directory before binding. A cheap read of the entered/effective
    /// target only — no enumerate, no pre-bind round-trip: an entered <see cref="TargetServer"/>
    /// (the DC/domain) and/or <see cref="TargetBaseDn"/> (the scope), or "the detected domain" when
    /// both are blank (the serverless default resolves the FQDN at bind time).
    /// <para>#287: the view renders <see cref="TargetProse"/> + <see cref="TargetDn"/> as two
    /// Runs so the base-DN half gets the mono <c>dn</c> face (monospace honesty); this whole-line
    /// form is their concatenation, kept for the pinned VM tests.</para>
    /// </summary>
    public string TargetLine => TargetProse + TargetDn;

    /// <summary>
    /// #287: the proportional (prose) half of <see cref="TargetLine"/> — identity, connective
    /// text, and the server host when entered (a hostname is prose, not a DN). Ends with the
    /// separator when <see cref="TargetDn"/> follows.
    /// </summary>
    public string TargetProse
    {
        get
        {
            var server = TargetServer?.Trim();
            var baseDn = TargetBaseDn?.Trim();
            return (string.IsNullOrEmpty(server), string.IsNullOrEmpty(baseDn)) switch
            {
                (true, true) => $"as {CurrentUserContext} against the detected domain",
                (false, true) => $"as {CurrentUserContext} against {server}",
                (true, false) => $"as {CurrentUserContext} against ",
                (false, false) => $"as {CurrentUserContext} against {server} — ",
            };
        }
    }

    /// <summary>
    /// #287: the raw base-DN half of <see cref="TargetLine"/>, empty when no base DN is entered —
    /// the view styles this Run in the mono <c>dn</c> face.
    /// </summary>
    public string TargetDn => TargetBaseDn?.Trim() is { Length: > 0 } baseDn ? baseDn : string.Empty;

    /// <summary>ADR-031 D1: opens/closes the Advanced disclosure. Collapsed by default.</summary>
    [RelayCommand]
    private void ToggleAdvanced() => IsAdvancedExpanded = !IsAdvancedExpanded;

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
            // ADR-031 D5: validate the two new free-text inputs BEFORE building the provider, on the
            // live path only (demo ignores targeting). On invalid input show the inline error
            // (ADR-003 D7) and abort — never reach AdsPath with an injectable host/scheme or a garbage
            // search base. A blank field validates to null (the zero-config default).
            IDirectoryProvider provider;
            if (demo)
            {
                provider = _providerFactory(true);
            }
            else
            {
                var serverResult = ConnectionTarget.ValidateServer(TargetServer);
                if (!serverResult.IsValid)
                {
                    ErrorMessage = serverResult.ErrorMessage;
                    return;
                }

                var baseDnResult = ConnectionTarget.ValidateBaseDn(TargetBaseDn);
                if (!baseDnResult.IsValid)
                {
                    ErrorMessage = baseDnResult.ErrorMessage;
                    return;
                }

                // ADR-037 D9: register the concrete server/baseDn strings the app learns at
                // connect, so the free-text scrubber can token-replace them in exception /
                // jsError messages (a hostname matches no (CN|OU|DC)= pattern). Null — the
                // zero-config serverless default — is a safe no-op.
                Redactor.Learn(serverResult.Value);
                Redactor.Learn(baseDnResult.Value);

                bool targeting = serverResult.Value is not null || baseDnResult.Value is not null;
                provider = targeting && _targetedProviderFactory is not null
                    ? _targetedProviderFactory(serverResult.Value, baseDnResult.Value)
                    : _providerFactory(false);
            }

            DirectoryConnection connection = await provider.ConnectAsync();
            // ADR-026 D6 (WP2): carry the demo flag to the shell so the top-strip DEMO badge has an
            // honest source. Covers BOTH the Connect-screen "Demo mode" button and the CLI --demo
            // (which also routes through ConnectDemoCommand → ConnectCoreAsync(demo:true)).
            _onConnected(provider, connection, demo);
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
