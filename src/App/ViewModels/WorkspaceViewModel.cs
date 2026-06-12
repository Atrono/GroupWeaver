using CommunityToolkit.Mvvm.Input;
using GroupWeaver.App.Startup;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Providers;

namespace GroupWeaver.App.ViewModels;

/// <summary>
/// Workspace step (AP 2.1): records the chosen root and carries the connected provider
/// forward — deliberately the smallest possible seam for AP 2.2 (graph + scope load)
/// and AP 2.5 (detail panel). Nothing is loaded here; loading the scope is AP 2.2.
/// All state is immutable, so this is a plain class — observable members arrive with
/// the features that need them.
/// </summary>
public sealed class WorkspaceViewModel
{
    public WorkspaceViewModel(
        IDirectoryProvider provider,
        AdObject root,
        DirectoryConnection connection,
        bool webView2Missing = false)
    {
        Provider = provider;
        Root = root;
        Connection = connection;
        WebView2Missing = webView2Missing;
    }

    /// <summary>Provider behind the active connection; AP 2.2 loads the scope from it.</summary>
    public IDirectoryProvider Provider { get; }

    /// <summary>The root the user picked in the PickRoot step (mandatory entry filter).</summary>
    public AdObject Root { get; }

    /// <summary>Connection summary handed over from the Connect step.</summary>
    public DirectoryConnection Connection { get; }

    /// <summary>DN of the chosen root.</summary>
    public string RootDn => Root.Dn;

    /// <summary>Display name of the chosen root.</summary>
    public string RootName => Root.Name;

    /// <summary>Status-bar line; same shape as the M1 DoD console line.</summary>
    public string ConnectionSummary =>
        $"connected, {Connection.GroupCount} groups loaded — {Connection.Description}";

    /// <summary>
    /// Handed through from <see cref="ShellViewModel"/> at construction (ADR-003 D3):
    /// switches the GraphHost placeholder to its missing-runtime variant. AP 2.2
    /// re-checks before mounting the real WebView.
    /// </summary>
    public bool WebView2Missing { get; }

    /// <summary>Placeholder hyperlink: open the runtime's download page in the browser.</summary>
    public IRelayCommand OpenWebView2DownloadPageCommand { get; } =
        new RelayCommand(WebView2Runtime.OpenDownloadPage);
}
