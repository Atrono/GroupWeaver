using System.Globalization;

using GroupWeaver.App.Views;
using GroupWeaver.Core.Model;

using Xunit;

namespace GroupWeaver.App.Tests.Views;

/// <summary>
/// Pins the #196 root-picker DN-disambiguation converter
/// (<see cref="DnConverters.ToParentPath"/>): the root picker shows a candidate's leaf name on
/// the primary line and its PARENT path on the secondary line, so two same-named OUs under
/// different parents stay distinguishable even under right-side <c>CharacterEllipsis</c> trimming
/// (parents differ at the START). The parent path is everything after the first UNESCAPED comma —
/// a comma escaped as <c>\,</c> is part of an RDN value, not a separator; a single-RDN / root DN
/// (no unescaped comma) yields the literal placeholder; null/empty passes through unchanged.
///
/// Style mirrors the sibling converter oracles (<see cref="SeverityConvertersTests"/>,
/// <see cref="GapKindConvertersTests"/>): call the converter's <c>Convert</c> directly (the XAML
/// binding seam), never a private helper, so the test fails the instant the projection diverges.
/// </summary>
public sealed class DnConvertersTests
{
    /// <summary>The literal placeholder a single-RDN / root DN (no unescaped comma) projects to —
    /// the secondary line still shows SOMETHING rather than collapsing to blank.</summary>
    private const string DirectoryRoot = "(directory root)";

    /// <summary>
    /// The parent path is everything after the first UNESCAPED comma. Normal multi-RDN DNs drop
    /// their leaf RDN; an escaped-comma RDN (<c>Smith\, John</c>) is a SINGLE RDN, so the split
    /// skips its internal <c>\,</c> and lands on the real separator after it. A single-RDN / root
    /// DN has no separator at all and yields the placeholder.
    ///
    /// The #196 upgrade replaced a single-backslash lookbehind with a backslash-run-PARITY parse:
    /// the split lands on the first comma preceded by an EVEN-length run of backslashes (0,2,4,… =
    /// a real separator, because each <c>\\</c> is one escaped-backslash literal that does NOT
    /// escape the comma); an ODD run means the last backslash escapes the comma (it is part of the
    /// RDN value). The parity cases below live alongside the plain cases in one oracle. NOTE: every
    /// C# string literal here doubles each backslash, so the comment on each parity case spells out
    /// the ACTUAL (unescaped) DN character content under test.
    /// </summary>
    [Theory]
    [InlineData("OU=Groups,OU=EMEA,DC=agdlp,DC=lab", "OU=EMEA,DC=agdlp,DC=lab")]
    [InlineData("OU=Groups,OU=US,DC=agdlp,DC=lab", "OU=US,DC=agdlp,DC=lab")]
    [InlineData("CN=Smith\\, John,OU=Sales,DC=agdlp,DC=lab", "OU=Sales,DC=agdlp,DC=lab")]
    [InlineData("DC=lab", DirectoryRoot)]

    // #196 fix — EVEN run (2 backslashes) = REAL separator. Actual DN: OU=Foo\\,OU=Bar,DC=agdlp,DC=lab
    // The RDN value is `Foo\` (a trailing escaped backslash), then the comma is a genuine separator,
    // so the leaf `OU=Foo\` drops and the parent is OU=Bar,DC=agdlp,DC=lab. The C# literal "OU=Foo\\\\"
    // is four source backslashes = two real backslash characters before the comma.
    [InlineData("OU=Foo\\\\,OU=Bar,DC=agdlp,DC=lab", "OU=Bar,DC=agdlp,DC=lab")]

    // #196 — ODD run (3 backslashes) = ESCAPED comma (inside the RDN value). Actual DN: CN=a\\\,b,DC=x,DC=y
    // The value is `a\,b` (an escaped backslash followed by an escaped comma), so the leading comma is
    // NOT a separator; the split lands on the real separator after `b`, giving parent DC=x,DC=y. The C#
    // literal "CN=a\\\\\\,b,..." is six source backslashes = three real backslash characters before the comma.
    [InlineData("CN=a\\\\\\,b,DC=x,DC=y", "DC=x,DC=y")]

    // Reviewer nice-to-have — a single escaped-comma-only RDN with NO real separator. Actual DN:
    // CN=Smith\, John (one backslash escaping the comma, ODD run of 1). There is no unescaped comma
    // anywhere, so the whole DN is one RDN and the projection is the root placeholder.
    [InlineData("CN=Smith\\, John", DirectoryRoot)]
    public void ToParentPath_ProjectsEverythingAfterTheFirstUnescapedComma(string dn, string expected)
    {
        Assert.Equal(expected, ToParentPath(dn));
    }

    /// <summary>
    /// The point of #196: two candidates that share a LEAF name (<c>Groups</c>) but sit under
    /// different parents (<c>OU=EMEA</c> vs. <c>OU=US</c>) project to DIFFERENT secondary lines —
    /// and they differ at the START, so right-side <c>CharacterEllipsis</c> trimming can never
    /// erase the distinction. Without this the two rows would render identically ambiguous.
    /// </summary>
    [Fact]
    public void ToParentPath_DisambiguatesSameNamedCandidates_UnderDifferentParents()
    {
        const string emea = "OU=Groups,OU=EMEA,DC=agdlp,DC=lab";
        const string us = "OU=Groups,OU=US,DC=agdlp,DC=lab";

        var emeaParent = ToParentPath(emea);
        var usParent = ToParentPath(us);

        Assert.NotEqual(emeaParent, usParent);
        Assert.Equal("OU=EMEA,DC=agdlp,DC=lab", emeaParent);
        Assert.Equal("OU=US,DC=agdlp,DC=lab", usParent);
    }

    /// <summary>The disambiguation, driven off the SAME projection the root-picker row binds
    /// (<see cref="AdObject.Dn"/>): two same-named candidates under different parents carry
    /// distinct secondary lines. This is the closest clean seam to the rendered row without a
    /// brittle virtualized-list realization test — the converter IS the secondary-line source.</summary>
    [Fact]
    public void ToParentPath_OverCandidateDns_DistinguishesTwoSameNamedRootCandidates()
    {
        var emea = Candidate("Groups", "OU=Groups,OU=EMEA,DC=agdlp,DC=lab");
        var us = Candidate("Groups", "OU=Groups,OU=US,DC=agdlp,DC=lab");

        // Same leaf name on the primary line — indistinguishable there ...
        Assert.Equal(emea.Name, us.Name);

        // ... but the secondary line (the converter over Dn) tells them apart.
        Assert.NotEqual(ToParentPath(emea.Dn), ToParentPath(us.Dn));
    }

    /// <summary>Null and empty pass THROUGH unchanged (the binding is a no-op on absent DNs) —
    /// they must NOT become the placeholder or throw.</summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    public void ToParentPath_PassesNullAndEmptyThrough(string? dn, string? expected)
    {
        Assert.Equal(expected, ToParentPath(dn));
    }

    private static AdObject Candidate(string name, string dn) =>
        new() { Dn = dn, Kind = AdObjectKind.OrganizationalUnit, Name = name, SamAccountName = null };

    /// <summary>Invoke the parent-path converter through its binding seam exactly as XAML does.</summary>
    private static string? ToParentPath(string? dn) =>
        (string?)DnConverters.ToParentPath.Convert(
            dn, typeof(string), null, CultureInfo.InvariantCulture);
}
