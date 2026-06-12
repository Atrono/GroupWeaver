using System.Reflection;

using GroupWeaver.Core.Model;
using GroupWeaver.Providers;

using Xunit;

namespace GroupWeaver.Tests.Providers;

/// <summary>
/// Pins <see cref="DemoProvider"/>'s scope predicate to the escape-aware
/// <c>DnPath</c> ancestry semantics (#29, ADR-004): a DN is in scope iff it IS
/// the base DN or a component-wise descendant of it — an escaped comma
/// (<c>\,</c>) inside an RDN value never separates, so a raw string that merely
/// ENDS WITH <c>",{baseDn}"</c> is not necessarily in scope.
/// </summary>
/// <remarks>
/// The divergent (escaped-comma) cases are pinned at the predicate level via
/// reflection, deliberately: the embedded demo dataset is itself a pinned public
/// artifact that contains no backslash-escaped DNs, and over backslash-free data
/// a textual <c>",{baseDn}"</c> suffix can only start at an UNESCAPED comma —
/// naive and escape-aware matching then agree on every object, so no
/// <c>LoadScopeAsync</c> black-box test can tell the two implementations apart.
/// The seam is pinned BY NAME: <c>bool IsInScope(string dn, string baseDn)</c>
/// must stay a static method on <see cref="DemoProvider"/> (any visibility;
/// <c>InternalsVisibleTo("GroupWeaver.Tests")</c> already exists) — rename it
/// only together with this file.
/// </remarks>
public class DemoProviderScopeTests : IClassFixture<DemoProviderFixture>
{
    private const string RootDn = DemoProviderFixture.RootDn;
    private const string UsersOuDn = "OU=Users,OU=AGDLP-Demo,DC=weavedemo,DC=example";
    private const string User001Dn = "CN=Anna Acker (u001),OU=Users,OU=AGDLP-Demo,DC=weavedemo,DC=example";
    private const string CircleADn = "CN=GG_Circle_A,OU=Groups,OU=AGDLP-Demo,DC=weavedemo,DC=example";

    private readonly DemoProviderFixture _fixture;

    public DemoProviderScopeTests(DemoProviderFixture fixture) => _fixture = fixture;

    // --- IsInScope: escaped-comma divergence (the #29 bug) ---------------------
    // A naive EndsWith(","+baseDn) match says IN for both rows; component-wise
    // DnPath semantics say OUT. RED until DemoProvider is aligned on DnPath.

    [Theory]
    // Escaped comma in the leading RDN: the object is "CN=Acker\,OU=Users",
    // a DIRECT child of OU=AGDLP-Demo — the issue's example shape.
    [InlineData(@"CN=Acker\,OU=Users,OU=AGDLP-Demo,DC=weavedemo,DC=example")]
    // Escaped comma in a middle RDN: the object sits under an OU literally
    // named "Dev,OU=Users", not under OU=Users.
    [InlineData(@"CN=Printer-07,OU=Dev\,OU=Users,OU=AGDLP-Demo,DC=weavedemo,DC=example")]
    public void IsInScope_EscapedCommaSuffixLookalike_IsOutOfScope(string dn)
    {
        Assert.False(IsInScope(dn, UsersOuDn));
    }

    [Fact]
    public void IsInScope_EscapedCommaInRdnOfGenuineDescendant_IsInScope()
    {
        // The escaped comma stays inside the CN value; the remaining components
        // really are OU=Users,… — escape-awareness must not over-exclude.
        Assert.True(IsInScope(
            @"CN=Acker\, Anna (u900),OU=Users,OU=AGDLP-Demo,DC=weavedemo,DC=example",
            UsersOuDn));
    }

    // --- IsInScope: agreeing cases (green today; regression guards for the fix) ---

    [Theory]
    [InlineData(UsersOuDn, UsersOuDn)] // the scope root itself is in scope
    [InlineData("ou=users,ou=agdlp-demo,dc=weavedemo,dc=example", UsersOuDn)] // … case-insensitively (Dn.Comparer)
    [InlineData(User001Dn, UsersOuDn)] // direct child
    [InlineData("CN=Print-01,OU=Sub,OU=Users,OU=AGDLP-Demo,DC=weavedemo,DC=example", UsersOuDn)] // nested descendant
    [InlineData(User001Dn, "ou=USERS,ou=agdlp-demo,DC=WEAVEDEMO,dc=example")] // case-twisted base
    public void IsInScope_SelfAndDescendants_AreInScope(string dn, string baseDn)
    {
        Assert.True(IsInScope(dn, baseDn));
    }

    [Theory]
    [InlineData(CircleADn)] // sibling subtree (OU=Groups)
    [InlineData(RootDn)] // ancestor of the scope is not IN the scope
    // The whole scope DN as an ESCAPED value substring of a single RDN —
    // a value lookalike, not a descendant.
    [InlineData(@"CN=OU=Users\,OU=AGDLP-Demo\,DC=weavedemo\,DC=example,OU=Notes,OU=AGDLP-Demo,DC=weavedemo,DC=example")]
    public void IsInScope_NonDescendants_AreOutOfScope(string dn)
    {
        Assert.False(IsInScope(dn, UsersOuDn));
    }

    // --- LoadScopeAsync: end-to-end scope behavior --------------------------------

    [Fact]
    public async Task LoadScopeAsync_IncludesTheScopeRootItself()
    {
        var snapshot = await _fixture.Provider.LoadScopeAsync(UsersOuDn);

        Assert.True(snapshot.TryGetObject(UsersOuDn, out var root));
        Assert.Equal(AdObjectKind.OrganizationalUnit, root.Kind);
    }

    [Fact]
    public async Task LoadScopeAsync_CaseTwistedBaseDn_LoadsTheSameSubtree()
    {
        var snapshot = await _fixture.Provider.LoadScopeAsync(UsersOuDn.ToUpperInvariant());

        Assert.Equal(141, snapshot.Objects.Count); // the OU itself + 140 users
    }

    /// <summary>Bridges to the (currently private) static scope predicate —
    /// see the class remarks for why reflection is the right seam here.</summary>
    private static bool IsInScope(string dn, string baseDn)
    {
        var method = typeof(DemoProvider).GetMethod(
            "IsInScope",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
            [typeof(string), typeof(string)]);
        Assert.True(
            method is not null && method.ReturnType == typeof(bool),
            "DemoProvider must keep a static scope predicate 'bool IsInScope(string dn, string baseDn)' — "
            + "it is pinned by name in DemoProviderScopeTests (#29); rename only together with this file.");
        return (bool)method!.Invoke(null, [dn, baseDn])!;
    }
}
