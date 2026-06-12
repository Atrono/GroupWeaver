using System.DirectoryServices;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Principal;

using GroupWeaver.Core.Model;
using GroupWeaver.Core.Rules;
using GroupWeaver.Providers;

using Xunit;

namespace GroupWeaver.Tests.Providers.Ldap;

/// <summary>
/// AP 3.1 slice 7 (ADR-008): the default ignore list vs the LIVE, LOCALIZED
/// builtin Administrators group. The lab DC is German-localized, so the group's
/// CN/name/sAMAccountName read back as <c>Administratoren</c> — exactly the
/// case the <c>*,CN=Builtin,*</c> default ignore entry must cover WITHOUT any
/// locale knowledge: the <c>CN=Builtin</c> CONTAINER name is never localized,
/// while every <c>CN=Users</c> entry pins an English CN and must therefore NOT
/// match. No assertion here depends on the localized name itself; everything is
/// derived from the live read. Binding goes through the well-known-SID ADsPath
/// form <c>&lt;SID=S-1-5-32-544&gt;</c> (locale- and DN-independent) and is
/// strictly READ-ONLY — property-cache reads only, no CommitChanges, nothing
/// AD-mutating; reads outside OU=AGDLP-Lab are sanctioned, only writes are
/// scoped. Excluded in CI via the class-level <c>Category=RequiresAd</c> trait;
/// skipped with a loud warning off the lab DC via <see cref="AdFactAttribute"/>.
/// </summary>
[SupportedOSPlatform("windows")]
[Trait(TestCategories.Category, TestCategories.RequiresAd)]
public class DefaultIgnoreListAdTests
{
    /// <summary>Well-known SID of BUILTIN\Administrators — identical on every
    /// DC regardless of OS language; the localization-proof handle.</summary>
    private const string AdministratorsSid = "S-1-5-32-544";

    private const string BuiltinIgnoreGlob = "*,CN=Builtin,*";

    // --- the live read maps to a regular in-snapshot group object ---------------

    [AdFact]
    public void LiveBuiltinAdministrators_BySid_MapsToDomainLocalGroup_InTheBuiltinContainer()
    {
        var (adObject, objectSid) = ReadBuiltinAdministrators();

        // Proof the SID binding hit the intended object: the objectSid read
        // back from the directory IS the well-known Administrators SID.
        Assert.Equal(AdministratorsSid, objectSid);

        // The container RDN is never localized — the premise of the default
        // ignore glob — while the leaf CN is localized (Administratoren on this
        // DC) and deliberately NOT asserted.
        Assert.Contains(",CN=Builtin,", adObject.Dn, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CN=Users", adObject.Dn, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(adObject.Name));
        Assert.False(string.IsNullOrWhiteSpace(adObject.SamAccountName));

        // groupType builtin bit 0x1 forces DomainLocalGroup (kind-mapper rule):
        // a builtin group that makes it into a snapshot is a REGULAR group
        // object, not External — nothing about its kind exempts it from rules.
        Assert.Equal(AdObjectKind.DomainLocalGroup, adObject.Kind);
    }

    // --- default ignore list vs the localized live object ------------------------

    [AdFact]
    public void DefaultIgnore_BuiltinGlobMatchesLiveLocalizedAdministrators_OnBothChannels()
    {
        var (adObject, _) = ReadBuiltinAdministrators();
        var builtinEntry = RulesetLoader.LoadDefault().Ignore
            .Single(entry => entry.Dn == BuiltinIgnoreGlob);

        // Matches = the in-snapshot channel; MatchesDn = the raw frontier-DN
        // channel (on a lab-scoped load the builtin surfaces as a bare member
        // DN). AP 3.2 consults both — the entry must cover both.
        Assert.True(builtinEntry.Matches(adObject));
        Assert.True(builtinEntry.MatchesDn(adObject.Dn));
    }

    [AdFact]
    public void DefaultIgnore_NoUsersEntryMatchesLiveAdministrators_BuiltinEntryIsTheOnlyMatch()
    {
        var (adObject, _) = ReadBuiltinAdministrators();
        var ignore = RulesetLoader.LoadDefault().Ignore;

        // Non-vacuous: the default list DOES ship CN=Users entries (Domain
        // Admins, krbtgt, ...) — none of them may swallow a Builtin object.
        var usersEntries = ignore
            .Where(entry => entry.Dn?.Contains("CN=Users", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();
        Assert.NotEmpty(usersEntries);
        Assert.All(usersEntries, entry =>
        {
            Assert.False(entry.Matches(adObject));
            Assert.False(entry.MatchesDn(adObject.Dn));
        });

        // Suppression is attributable to exactly ONE visible, deletable entry:
        // delete '*,CN=Builtin,*' and this object becomes judgeable (ADR-008).
        var matching = ignore.Where(entry => entry.Matches(adObject)).ToList();
        var single = Assert.Single(matching);
        Assert.Equal(BuiltinIgnoreGlob, single.Dn);
    }

    // --- helpers -----------------------------------------------------------------

    /// <summary>
    /// READ-ONLY: binds <c>LDAP://localhost/&lt;SID=S-1-5-32-544&gt;</c>, reads
    /// the property cache, and maps the result through the provider's own
    /// <see cref="LdapEntry.Map"/> path (objectClass chain + invariant groupType
    /// → kind, name, sAMAccountName) — the same construction a live load uses.
    /// Returns the mapped object plus the string form of the objectSid read back.
    /// </summary>
    private static (AdObject AdObject, string ObjectSid) ReadBuiltinAdministrators()
    {
        using var entry = new DirectoryEntry($"LDAP://localhost/<SID={AdministratorsSid}>");

        string dn = (string)entry.Properties["distinguishedName"].Value!;
        var properties = new Dictionary<string, IReadOnlyList<string>>();
        foreach (string attribute in (string[])["name", "sAMAccountName", "objectClass", "groupType"])
        {
            var values = entry.Properties[attribute]
                .Cast<object>()
                .Select(value => Convert.ToString(value, CultureInfo.InvariantCulture))
                .OfType<string>()
                .ToList();
            if (values.Count > 0)
            {
                properties[attribute] = values;
            }
        }

        var objectSid = new SecurityIdentifier((byte[])entry.Properties["objectSid"].Value!, 0);
        return (LdapEntry.Map(new LdapEntry(dn, properties)), objectSid.Value);
    }
}
