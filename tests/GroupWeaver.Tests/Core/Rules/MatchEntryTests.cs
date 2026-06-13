using GroupWeaver.Core.Model;
using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.Tests.Core.Rules;

/// <summary>
/// Pins <see cref="MatchEntry"/> matching semantics (ADR-008): a dn entry
/// globs against <see cref="AdObject.Dn"/> only; a name entry globs against
/// <see cref="AdObject.Name"/> OR <see cref="AdObject.SamAccountName"/> only;
/// <see cref="MatchEntry.MatchesDn"/> serves raw member DNs absent from the
/// snapshot and name entries NEVER match raw DNs (no name to compare —
/// a name glob must not accidentally swallow DN strings).
/// </summary>
public class MatchEntryTests
{
    // --- dn entries: glob vs Dn only ------------------------------------------

    [Fact]
    public void Matches_DnEntry_GlobsAgainstObjectDn()
    {
        var entry = new MatchEntry { Dn = "*,CN=Builtin,*" };

        Assert.True(entry.Matches(
            Obj("CN=Administrators,CN=Builtin,DC=weavedemo,DC=example", "Administrators")));
    }

    [Fact]
    public void Matches_DnEntry_NonMatchingDn_IsFalse()
    {
        var entry = new MatchEntry { Dn = "*,CN=Builtin,*" };

        Assert.False(entry.Matches(
            Obj("CN=GG_Sales_Staff,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab", "GG_Sales_Staff")));
    }

    [Fact]
    public void Matches_DnEntry_DoesNotConsultNameOrSam()
    {
        // The glob hits the Name and the SamAccountName but not the Dn:
        // a dn entry must stay a dn entry.
        var entry = new MatchEntry { Dn = "GG_Sales_Staff" };

        Assert.False(entry.Matches(Obj(
            "CN=GG_Sales_Staff,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab",
            "GG_Sales_Staff",
            sam: "GG_Sales_Staff")));
    }

    [Fact]
    public void Matches_DnEntry_IsCaseInsensitive()
    {
        var entry = new MatchEntry { Dn = "cn=domain admins,cn=users,*" };

        Assert.True(entry.Matches(
            Obj("CN=Domain Admins,CN=Users,DC=weavedemo,DC=example", "Domain Admins")));
    }

    // --- name entries: glob vs Name OR SamAccountName --------------------------

    [Fact]
    public void Matches_NameEntry_HitsName()
    {
        var entry = new MatchEntry { Name = "GG_Circle_?" };

        Assert.True(entry.Matches(Obj(
            "CN=GG_Circle_A,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab", "GG_Circle_A")));
    }

    [Fact]
    public void Matches_NameEntry_HitsSamAccountNameWhenNameDiffers()
    {
        var entry = new MatchEntry { Name = "u001" };

        Assert.True(entry.Matches(Obj(
            "CN=Anna Acker (u001),OU=Users,OU=AGDLP-Lab,DC=agdlp,DC=lab",
            "Anna Acker (u001)",
            sam: "u001")));
    }

    [Fact]
    public void Matches_NameEntry_NeitherNameNorSam_IsFalse()
    {
        var entry = new MatchEntry { Name = "SalesTeamGlobal" };

        Assert.False(entry.Matches(Obj(
            "CN=GG_Sales_Staff,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab",
            "GG_Sales_Staff",
            sam: "GG_Sales_Staff")));
    }

    [Fact]
    public void Matches_NameEntry_NullSam_NoMatchOnName_IsFalseNotThrow()
    {
        var entry = new MatchEntry { Name = "u???" };

        Assert.False(entry.Matches(Obj(
            "CN=GG_X,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab", "GG_X", sam: null)));
    }

    [Fact]
    public void Matches_NameEntry_DoesNotConsultDn()
    {
        // The glob would swallow any DN ("CN=*") — but a name entry compares
        // names, and this object's Name/Sam do not start with "CN=".
        var entry = new MatchEntry { Name = "CN=*" };

        Assert.False(entry.Matches(Obj(
            "CN=GG_Sales_Staff,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab",
            "GG_Sales_Staff",
            sam: "GG_Sales_Staff")));
    }

    [Fact]
    public void Matches_NameEntry_IsCaseInsensitive()
    {
        var entry = new MatchEntry { Name = "gg_sales_staff" };

        Assert.True(entry.Matches(Obj(
            "CN=GG_Sales_Staff,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab", "GG_Sales_Staff")));
    }

    // --- MatchesDn: raw member DNs absent from the snapshot ---------------------

    [Fact]
    public void MatchesDn_DnEntry_GlobsAgainstRawDn()
    {
        var entry = new MatchEntry { Dn = "*,CN=ForeignSecurityPrincipals,*" };

        Assert.True(entry.MatchesDn(
            "CN=S-1-5-21-1100000001-2200000002-3300000003-1106,CN=ForeignSecurityPrincipals,DC=agdlp,DC=lab"));
    }

    [Fact]
    public void MatchesDn_DnEntry_AnchoringHolds()
    {
        var entry = new MatchEntry { Dn = "CN=Users,*" };

        Assert.False(entry.MatchesDn("CN=Domain Admins,CN=Users,DC=weavedemo,DC=example"));
    }

    [Fact]
    public void MatchesDn_DnEntry_IsCaseInsensitive()
    {
        var entry = new MatchEntry { Dn = "cn=krbtgt,cn=users,*" };

        Assert.True(entry.MatchesDn("CN=krbtgt,CN=Users,DC=agdlp,DC=lab"));
    }

    [Fact]
    public void MatchesDn_NameEntry_IsAlwaysFalse()
    {
        // A raw DN has no Name/SamAccountName to compare — name entries never
        // match raw DNs, even with a match-everything glob.
        var entry = new MatchEntry { Name = "*" };

        Assert.False(entry.MatchesDn("CN=GG_Sales_Staff,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab"));
    }

    [Fact]
    public void MatchesDn_NameEntry_GlobThatWouldHitTheDnText_StillFalse()
    {
        var entry = new MatchEntry { Name = "CN=GG_X,*" };

        Assert.False(entry.MatchesDn("CN=GG_X,OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab"));
    }

    // --- semantics fields ---------------------------------------------------------

    [Fact]
    public void Endpoint_DefaultsToAny()
    {
        Assert.Equal(MatchEndpoint.Any, new MatchEntry { Name = "x" }.Endpoint);
    }

    [Fact]
    public void MatchEndpoint_ValuesArePinned()
    {
        // Any must stay the default (0): every entry outside nesting.exceptions
        // is forced to Any, and absent JSON "endpoint" must mean Any.
        Assert.Equal(0, (int)MatchEndpoint.Any);
        Assert.Equal(1, (int)MatchEndpoint.Parent);
        Assert.Equal(2, (int)MatchEndpoint.Member);
        Assert.Equal(
            new[] { MatchEndpoint.Any, MatchEndpoint.Parent, MatchEndpoint.Member },
            Enum.GetValues<MatchEndpoint>());
    }

    [Fact]
    public void SemanticsFields_RoundTripThroughInit()
    {
        var entry = new MatchEntry
        {
            Dn = "CN=UG_Managers,OU=Groups,*",
            Note = "Migration grace; remove once OPS-1234 lands.",
            Endpoint = MatchEndpoint.Parent,
        };

        Assert.Equal("CN=UG_Managers,OU=Groups,*", entry.Dn);
        Assert.Null(entry.Name);
        Assert.Equal("Migration grace; remove once OPS-1234 lands.", entry.Note);
        Assert.Equal(MatchEndpoint.Parent, entry.Endpoint);
    }

    // --- helpers ---------------------------------------------------------------------

    private static AdObject Obj(
        string dn, string name, string? sam = null, AdObjectKind kind = AdObjectKind.GlobalGroup) =>
        new() { Dn = dn, Kind = kind, Name = name, SamAccountName = sam };
}
