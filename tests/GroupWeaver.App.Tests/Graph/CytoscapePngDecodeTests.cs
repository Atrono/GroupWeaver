using System.Text;

using GroupWeaver.App.Graph;

using Xunit;

namespace GroupWeaver.App.Tests.Graph;

/// <summary>
/// Pins the FIX C hardening of <see cref="CytoscapeGraphRenderer.ExportPngAsync"/>'s
/// base64 decode. The pre-0.2 adversarial audit found an UNGUARDED
/// <c>Convert.FromBase64String</c> on the page's <c>pngExported</c> reply: a malformed
/// base64 body throws <see cref="FormatException"/>, which on the RelayCommand async-void
/// path rethrows on the UI thread with no handler and CRASHES the app — violating the
/// <see cref="IGraphRenderer"/> contract that export returns <c>null</c> on ANY error.
/// The fix extracts an <c>internal static</c> <see cref="CytoscapeGraphRenderer.DecodePngOrNull"/>
/// that returns <c>null</c> for null/empty/garbage and the exact bytes for valid base64.
/// The renderer itself is WebView-bound (no unit-testable surface — see
/// <c>GraphRendererSeamTests</c>), so the extracted helper is tested directly through the
/// App.Tests InternalsVisibleTo seam.
/// </summary>
public sealed class CytoscapePngDecodeTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void DecodePngOrNull_NullOrEmpty_ReturnsNull(string? input)
    {
        Assert.Null(CytoscapeGraphRenderer.DecodePngOrNull(input));
    }

    [Theory]
    [InlineData("not base64!!!")] // illegal base64 chars
    [InlineData("====")] // padding-only, no data
    [InlineData("abc")] // length not a multiple of 4
    [InlineData("AB==CD")] // padding mid-string
    public void DecodePngOrNull_MalformedBase64_ReturnsNull_NeverThrows(string garbage)
    {
        // FormatException must be swallowed to null — never propagate to the UI thread
        // (the async-void crash the audit reproduced).
        Assert.Null(CytoscapeGraphRenderer.DecodePngOrNull(garbage));
    }

    [Fact]
    public void DecodePngOrNull_ValidBase64_ReturnsExactDecodedBytes()
    {
        var expected = Encoding.UTF8.GetBytes("the quick brown fox — PNG bytes stand-in");
        var encoded = Convert.ToBase64String(expected);

        var decoded = CytoscapeGraphRenderer.DecodePngOrNull(encoded);

        Assert.NotNull(decoded);
        Assert.Equal(expected, decoded);
    }
}
