using System;
using System.Linq;

using GroupWeaver.App.ViewModels;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.App.Tests;

/// <summary>
/// Pins the ADR-030 D2 (#188) triage-caveat sentence pluralization on
/// <see cref="AuditViewModel.TriageCaveatText"/>: "1 finding acknowledged/suppressed — excluded
/// from this score." (singular) vs "N findings …" (plural for any N != 1). The VM pluralizes in
/// code because a raw <c>StringFormat</c> would emit the ungrammatical "1 findings".
///
/// <para><see cref="AuditViewModel.TriagedCount"/> = <c>max(0, wouldBe.Count − live.Count)</c>,
/// where the would-be report is the full <see cref="RuleEngine.Evaluate"/> of the scope (no triage
/// tags on the default ruleset, so the would-be IS the full evaluation) and the LIVE report is the
/// ctor argument. So a trimmed live report (the full findings minus k) drives the count to exactly
/// k — a pure VM projection over the two reports the VM already holds, never touching the triage-tag
/// grammar (the ADR-028 boundary). Compares the text/count projection, never record identity.</para>
/// </summary>
public sealed class AuditTriageCaveatTextTests
{
    private const string RootDn = "OU=Lab,DC=stub,DC=lab";

    [Fact]
    public void TriageCaveatText_OneTriaged_ReadsSingularFinding()
    {
        // Drop exactly ONE finding from the live report relative to the would-be (full) report.
        using var audit = AuditWithTriagedCount(1);

        Assert.Equal(1, audit.TriagedCount);
        Assert.True(audit.HasTriaged);
        Assert.Equal(
            "1 finding acknowledged/suppressed — excluded from this score.",
            audit.TriageCaveatText);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void TriageCaveatText_MultipleTriaged_ReadsPluralFindings(int triaged)
    {
        using var audit = AuditWithTriagedCount(triaged);

        Assert.Equal(triaged, audit.TriagedCount);
        Assert.True(audit.HasTriaged);
        Assert.Equal(
            $"{triaged} findings acknowledged/suppressed — excluded from this score.",
            audit.TriageCaveatText);
    }

    [Fact]
    public void TriageCaveatText_NoneTriaged_HasTriagedFalse()
    {
        // Live report == would-be report => TriagedCount 0, the caveat is not shown (HasTriaged false).
        using var audit = AuditWithTriagedCount(0);

        Assert.Equal(0, audit.TriagedCount);
        Assert.False(audit.HasTriaged);
    }

    /// <summary>
    /// An <see cref="AuditViewModel"/> whose <see cref="AuditViewModel.TriagedCount"/> is exactly
    /// <paramref name="triaged"/>: build a loaded scope rich enough to trip more than
    /// <paramref name="triaged"/> default-ruleset findings, evaluate it for the would-be report, then
    /// hand the ctor a LIVE report with the first <paramref name="triaged"/> findings removed. The VM
    /// re-derives the would-be report itself (no triage tags ⇒ full evaluation), so
    /// <c>wouldBe.Count − live.Count == triaged</c>.
    /// </summary>
    private static AuditViewModel AuditWithTriagedCount(int triaged)
    {
        var snapshot = LoadedScopeWithManyFindings();
        var ruleset = RulesetLoader.LoadDefault();
        var full = RuleEngine.Evaluate(snapshot, ruleset);

        Assert.True(
            full.Violations.Count > triaged,
            $"the fixture must produce more than {triaged} findings to trim (had {full.Violations.Count})");

        var live = new RuleReport(
            full.Violations.Skip(triaged).ToArray(),
            full.UncheckedDns);

        return new AuditViewModel(snapshot, live, ruleset, RootDn, onBack: () => { });
    }

    /// <summary>A fully-loaded scope that trips several default-ruleset findings: a handful of empty
    /// GG groups (each an empty-group Info) plus one badly-named GG (a naming Warning). More than
    /// enough findings to trim 0..3 from the live report for the caveat-count theory.</summary>
    private static DirectorySnapshot LoadedScopeWithManyFindings()
    {
        var snapshot = new DirectorySnapshot();

        // Five empty GG groups => five empty-group Info findings.
        for (int i = 0; i < 5; i++)
        {
            var dn = $"CN=GG_Empty_{i:D2},OU=Lab,DC=stub,DC=lab";
            snapshot.AddObject(new AdObject { Dn = dn, Kind = AdObjectKind.GlobalGroup, Name = dn });
            snapshot.SetMembers(dn, Array.Empty<string>());
        }

        // One badly-named GG (does not match the GG_* convention) => a naming Warning. Give it a
        // member so it is not ALSO an empty-group finding (keeps the finding shape simple).
        const string member = "CN=GG_Member,OU=Lab,DC=stub,DC=lab";
        snapshot.AddObject(new AdObject { Dn = member, Kind = AdObjectKind.GlobalGroup, Name = "GG_Member" });
        snapshot.SetMembers(member, Array.Empty<string>());

        const string badName = "CN=NotAConventionName,OU=Lab,DC=stub,DC=lab";
        snapshot.AddObject(new AdObject { Dn = badName, Kind = AdObjectKind.GlobalGroup, Name = "NotAConventionName" });
        snapshot.SetMembers(badName, new[] { member });

        return snapshot;
    }
}
