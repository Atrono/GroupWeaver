using GroupWeaver.App.Startup;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// Decision table for the WebView2 Runtime startup probe (AP 2.1 S7, ADR-003 D3),
/// exercised through the injectable-reader core <see cref="WebView2Runtime.Probe(Func{string, string?})"/>
/// — no test here touches the real registry. The registry key paths themselves are
/// pinned verbatim: the EdgeUpdate product GUID and the three hive shapes are
/// Microsoft-documented contract, and a typo would silently report "missing" on every
/// machine (the probe treats an unreadable path exactly like an absent runtime).
/// </summary>
public sealed class WebView2RuntimeTests
{
    // The Microsoft-documented EdgeUpdate client-state keys, verbatim. Duplicated from
    // the implementation ON PURPOSE: these strings are the external contract.
    private const string HklmWow6432NodePath =
        @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

    private const string Hklm32BitOsShapePath =
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

    private const string HkcuPerUserPath =
        @"HKEY_CURRENT_USER\Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

    // --- the documented paths are the contract ----------------------------------------

    [Fact]
    public void ClientStateKeyPaths_PinsGuidHiveShapesAndPriorityOrder_Verbatim()
    {
        Assert.Equal(
            [HklmWow6432NodePath, Hklm32BitOsShapePath, HkcuPerUserPath],
            WebView2Runtime.ClientStateKeyPaths);
    }

    [Fact]
    public void Probe_ConsultsExactlyTheDocumentedPaths_InPriorityOrder_WhenNothingHits()
    {
        var received = new List<string>();

        var status = WebView2Runtime.Probe(path =>
        {
            received.Add(path);
            return null;
        });

        // "Missing" may only be declared after EVERY documented location was consulted,
        // each exactly once, in the documented priority order.
        Assert.Equal(WebView2Runtime.ClientStateKeyPaths, received);
        Assert.False(status.IsInstalled);
        Assert.Null(status.Version);
    }

    [Fact]
    public void Probe_NeverInventsPaths_EveryReadIsOneOfTheDocumentedKeys()
    {
        var received = new List<string>();

        WebView2Runtime.Probe(path =>
        {
            received.Add(path);
            return "142.0.3595.53";
        });

        Assert.NotEmpty(received);
        Assert.All(received, path => Assert.Contains(path, WebView2Runtime.ClientStateKeyPaths));
    }

    // --- single-location hits: each documented shape alone means installed -------------

    [Fact]
    public void Probe_Wow6432NodeHitOnly_IsInstalledWithThatVersion()
    {
        var status = WebView2Runtime.Probe(
            path => path == HklmWow6432NodePath ? "120.0.2210.61" : null);

        Assert.True(status.IsInstalled);
        Assert.Equal("120.0.2210.61", status.Version);
    }

    [Fact]
    public void Probe_Hklm32BitOsShapeHitOnly_IsInstalledWithThatVersion()
    {
        var status = WebView2Runtime.Probe(
            path => path == Hklm32BitOsShapePath ? "119.0.2151.97" : null);

        Assert.True(status.IsInstalled);
        Assert.Equal("119.0.2151.97", status.Version);
    }

    [Fact]
    public void Probe_HkcuPerUserHitOnly_IsInstalledWithThatVersion()
    {
        var status = WebView2Runtime.Probe(
            path => path == HkcuPerUserPath ? "121.0.2277.83" : null);

        Assert.True(status.IsInstalled);
        Assert.Equal("121.0.2277.83", status.Version);
    }

    // --- not-installed shapes: uninstalled marker, empty, null/absent ------------------

    [Fact]
    public void Probe_UninstalledMarkerEverywhere_IsMissing()
    {
        // Microsoft documents pv="0.0.0.0" as the leftover of an uninstalled runtime —
        // the key exists, the runtime does not.
        var status = WebView2Runtime.Probe(_ => "0.0.0.0");

        Assert.False(status.IsInstalled);
        Assert.Null(status.Version);
    }

    [Theory]
    [InlineData("")] // pv value present but empty
    [InlineData(null)] // pv value (or the whole key) absent
    public void Probe_EmptyOrAbsentPvEverywhere_IsMissing(string? pv)
    {
        var status = WebView2Runtime.Probe(_ => pv);

        Assert.False(status.IsInstalled);
        Assert.Null(status.Version);
    }

    [Fact]
    public void Probe_MixedAbsentEmptyAndMarker_IsStillMissing()
    {
        // One location per not-installed shape — none may count as a hit.
        var status = WebView2Runtime.Probe(path => path switch
        {
            HklmWow6432NodePath => "0.0.0.0",
            Hklm32BitOsShapePath => "",
            _ => null,
        });

        Assert.False(status.IsInstalled);
        Assert.Null(status.Version);
    }

    // --- priority order between locations ----------------------------------------------

    [Fact]
    public void Probe_Wow6432NodeWinsOverHkcu_WhenBothArePresent()
    {
        // Both locations hold a valid version; the reported one proves the per-machine
        // WOW6432Node location outranks the per-user HKCU one.
        var status = WebView2Runtime.Probe(path => path switch
        {
            HklmWow6432NodePath => "120.0.2210.61",
            HkcuPerUserPath => "999.9.9999.99",
            _ => null,
        });

        Assert.True(status.IsInstalled);
        Assert.Equal("120.0.2210.61", status.Version);
    }

    [Fact]
    public void Probe_UninstalledMarkerAtHigherPriority_DoesNotMaskARealLowerPriorityInstall()
    {
        // A per-machine uninstall leaves the 0.0.0.0 marker behind; a per-user install
        // under HKCU must still be found behind it.
        var status = WebView2Runtime.Probe(path => path switch
        {
            HklmWow6432NodePath => "0.0.0.0",
            HkcuPerUserPath => "121.0.2277.83",
            _ => null,
        });

        Assert.True(status.IsInstalled);
        Assert.Equal("121.0.2277.83", status.Version);
    }

    // --- the banner's hyperlink target --------------------------------------------------

    [Fact]
    public void DownloadUrl_IsTheEvergreenInstallerPage()
    {
        // The banner and the GraphHost placeholder both send the user here; the literal
        // is load-bearing for the "download link present" checklist item.
        Assert.Equal(
            "https://developer.microsoft.com/microsoft-edge/webview2/",
            WebView2Runtime.DownloadUrl);
    }
}
