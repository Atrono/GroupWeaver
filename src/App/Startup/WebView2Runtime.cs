using System.Diagnostics;
using Microsoft.Win32;

namespace GroupWeaver.App.Startup;

/// <summary>
/// Result of the WebView2 Runtime startup probe. <see cref="Version"/> is the
/// EdgeUpdate <c>pv</c> value when <see cref="IsInstalled"/>, otherwise <c>null</c>.
/// </summary>
public sealed record WebView2RuntimeStatus(bool IsInstalled, string? Version);

/// <summary>
/// Startup check for the Microsoft Edge WebView2 Runtime (PLANNING.md AP 2.1, ADR-001
/// guardrail 7, ADR-003 commit-body decision D3): a registry probe instead of a
/// <c>Microsoft.Web.WebView2</c> SDK dependency. A missing runtime is NOT an error —
/// the shell shows a persistent banner and everything but the graph keeps working;
/// AP 2.2 re-checks before instantiating the WebView control.
/// </summary>
public static class WebView2Runtime
{
    /// <summary>Microsoft's product GUID for the WebView2 Runtime under EdgeUpdate.</summary>
    private const string ClientGuid = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

    /// <summary>Where the banner's hyperlink sends the user (Evergreen installer page).</summary>
    public const string DownloadUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";

    /// <summary>
    /// The Microsoft-documented EdgeUpdate client-state keys holding the runtime's
    /// <c>pv</c> (product version) value, in priority order: HKLM 32-bit view (where the
    /// per-machine installer registers on a 64-bit OS), HKLM native view (the 32-bit OS
    /// shape), HKCU (per-user install). Probing all three unconditionally keeps the
    /// decision pure data — a path that does not exist simply yields no value.
    /// </summary>
    public static readonly IReadOnlyList<string> ClientStateKeyPaths =
    [
        @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\" + ClientGuid,
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\EdgeUpdate\Clients\" + ClientGuid,
        @"HKEY_CURRENT_USER\Software\Microsoft\EdgeUpdate\Clients\" + ClientGuid,
    ];

    /// <summary>Probe the real registry; call ONCE at the composition root.</summary>
    public static WebView2RuntimeStatus Probe() => Probe(ReadPvFromRegistry);

    /// <summary>
    /// Decision core, separated from the registry for testability: <paramref name="readPv"/>
    /// maps one of <see cref="ClientStateKeyPaths"/> to that key's <c>pv</c> value (or
    /// <c>null</c>). Installed = the first location yielding a non-empty version that is
    /// not the uninstalled marker <c>0.0.0.0</c>.
    /// </summary>
    public static WebView2RuntimeStatus Probe(Func<string, string?> readPv)
    {
        foreach (var keyPath in ClientStateKeyPaths)
        {
            var pv = readPv(keyPath);
            if (!string.IsNullOrEmpty(pv) && pv != "0.0.0.0")
            {
                return new WebView2RuntimeStatus(IsInstalled: true, Version: pv);
            }
        }

        return new WebView2RuntimeStatus(IsInstalled: false, Version: null);
    }

    /// <summary>Open <see cref="DownloadUrl"/> in the default browser (banner hyperlink).</summary>
    public static void OpenDownloadPage() =>
        Process.Start(new ProcessStartInfo(DownloadUrl) { UseShellExecute = true });

    private static string? ReadPvFromRegistry(string keyPath) =>
        Registry.GetValue(keyPath, "pv", defaultValue: null) as string;
}
