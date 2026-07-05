using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GroupWeaver.App.Graph;
using GroupWeaver.App.Rules;
using GroupWeaver.App.Settings;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Providers;

namespace GroupWeaver.App.ViewModels;

/// <summary>
/// PickRoot step (AP 2.1): the mandatory entry filter — nothing can be loaded until the
/// user picks exactly one OU or group as the root (<see cref="LoadRootCommand"/> stays
/// disabled without a selection). Candidates are a flat, client-side-filterable list:
/// real directories return thousands, so the view virtualizes instead of building a tree.
/// Error policy per ADR-003 D7: <see cref="DirectoryUnavailableException"/> surfaces
/// inline via <see cref="ErrorMessage"/> (no demo hint — a connection already succeeded);
/// every other exception bubbles through <see cref="LoadCandidates"/> (crash = bug).
/// This step only records the chosen root — loading the scope itself is AP 2.2.
/// </summary>
public sealed partial class RootPickerViewModel : ObservableObject
{
    private readonly IDirectoryProvider _provider;
    private readonly DirectoryConnection _connection;
    private readonly Action _onBack;
    private readonly Action<WorkspaceViewModel> _onConfirmed;
    private readonly bool _webView2Missing;
    private readonly Func<IGraphRenderer>? _graphRendererFactory;
    private readonly EffectiveRuleset? _ruleset;
    private readonly UiStateStore? _uiStateStore;

    /// <summary>True while the candidate enumeration is in flight.</summary>
    [ObservableProperty]
    private bool _isLoading;

    /// <summary>Error from the candidate load; <c>null</c> hides the inline error block.</summary>
    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>Every root candidate the provider offered, sorted by name for scanning.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredCandidates))]
    private IReadOnlyList<AdObject> _candidates = [];

    /// <summary>Plain-text filter narrowing <see cref="FilteredCandidates"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredCandidates))]
    private string? _filter;

    /// <summary>The candidate the user picked; <c>null</c> keeps Load disabled.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadRootCommand))]
    private AdObject? _selectedCandidate;

    public RootPickerViewModel(
        IDirectoryProvider provider,
        DirectoryConnection connection,
        Action onBack,
        Action<WorkspaceViewModel> onConfirmed,
        bool webView2Missing = false,
        Func<IGraphRenderer>? graphRendererFactory = null,
        EffectiveRuleset? ruleset = null,
        UiStateStore? uiStateStore = null)
    {
        _provider = provider;
        _connection = connection;
        _onBack = onBack;
        _onConfirmed = onConfirmed;
        _webView2Missing = webView2Missing;
        _graphRendererFactory = graphRendererFactory;
        _ruleset = ruleset;
        _uiStateStore = uiStateStore;
        LoadCandidates = LoadCandidatesAsync();
    }

    /// <summary>
    /// The candidate load kicked off on entry; storing the task keeps it observable
    /// (same pattern as <see cref="ShellViewModel.Initialization"/>, no fire-and-forget).
    /// </summary>
    public Task LoadCandidates { get; }

    /// <summary>Connection summary handed over from the Connect step (ADR-038 WP6, #245:
    /// the <c>--e2e</c> channel's <c>DemoConnected</c> event reads <c>GroupCount</c> from
    /// this the moment the picker is reached, mirroring the <c>--check --demo</c> stdout
    /// line) — mirrors <see cref="WorkspaceViewModel.Connection"/>.</summary>
    public DirectoryConnection Connection => _connection;

    /// <summary>
    /// <see cref="Candidates"/> narrowed by <see cref="Filter"/> — case-insensitive
    /// substring match on Name, SamAccountName and Dn; blank filter shows everything.
    /// </summary>
    public IReadOnlyList<AdObject> FilteredCandidates
    {
        get
        {
            var filter = Filter?.Trim();
            if (string.IsNullOrEmpty(filter))
            {
                return Candidates;
            }

            return Candidates
                .Where(c =>
                    c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || c.SamAccountName?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true
                    || c.Dn.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    /// <summary>Drop the connection and return to a fresh Connect step.</summary>
    [RelayCommand]
    private void Back() => _onBack();

    /// <summary>
    /// Confirm the selected root: hand the shell a Workspace step carrying the provider
    /// and the chosen root. CanExecute is the mandatory-entry-filter enforcement.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanLoadRoot))]
    private void LoadRoot() =>
        _onConfirmed(new WorkspaceViewModel(
            _provider, SelectedCandidate!, _connection, _webView2Missing,
            _graphRendererFactory, _ruleset, exportDialogs: null, uiStateStore: _uiStateStore));

    private bool CanLoadRoot() => SelectedCandidate is not null;

    private async Task LoadCandidatesAsync()
    {
        IsLoading = true;
        try
        {
            var candidates = await _provider.GetRootCandidatesAsync();
            Candidates = candidates
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Dn, Dn.Comparer)
                .ToList();
        }
        catch (DirectoryUnavailableException ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }
}
