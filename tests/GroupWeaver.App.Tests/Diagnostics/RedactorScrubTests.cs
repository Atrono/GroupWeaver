using System;
using System.Reflection;

using GroupWeaver.App.Diagnostics;

using Xunit;

namespace GroupWeaver.App.Tests.Diagnostics;

/// <summary>
/// Pins the ADR-037 D9 free-text scrubber (WP10 #249) — the safety net for exception /
/// jsError text that never went through a typed helper: every <c>(CN|OU|DC)=…</c> run is
/// replaced by ITS <c>dn#</c> token (the token of the exact DN substring — so scrubbed text
/// JOINS the structured fields that redacted the same DN via <see cref="Redactor.Dn"/>),
/// and every server/baseDn string registered via <c>Learn</c> at connect time is replaced
/// by its <c>host#</c> token. Non-sensitive text (counts, HResults, step names) passes
/// through byte-identical.
///
/// <para><b>Pinned run grammar</b> (the corpus below is the contract): a run starts at
/// <c>CN=</c>/<c>OU=</c>/<c>DC=</c>; an UNESCAPED comma continues the run only into another
/// component; the escaped comma <c>\,</c> and interior spaces stay INSIDE a component value
/// (real DNs carry both — stored as-given, [[data-model]]); <c>:</c>, <c>'</c> and
/// end-of-text end the run. Anything beyond these cases is implementer freedom — when in
/// doubt, over-redact (D9 is safety-biased).</para>
/// </summary>
public sealed class RedactorScrubTests
{
    [Fact]
    public void Scrub_ReplacesADnRun_WithItsDnToken_AtEndOfText()
    {
        const string dn = "CN=GG_Circle_A,OU=AGDLP-Lab,DC=agdlp,DC=lab";
        var token = RedactionTokens.Dn(Redactor.SessionSalt, dn);

        Assert.Equal(
            $"scope load failed for {token}",
            Redactor.Scrub($"scope load failed for {dn}"));
    }

    /// <summary>A colon ends the run — the non-sensitive HResult suffix survives verbatim
    /// (the LdapProvider "message: 0x…" shape).</summary>
    [Fact]
    public void Scrub_ColonEndsTheRun_TheNonSensitiveSuffixSurvives()
    {
        const string dn = "OU=Groups,OU=AGDLP-Lab,DC=agdlp,DC=lab";
        var token = RedactionTokens.Dn(Redactor.SessionSalt, dn);

        Assert.Equal(
            $"cannot bind to {token}: 0x8007052E",
            Redactor.Scrub($"cannot bind to {dn}: 0x8007052E"));
    }

    /// <summary>The ADR's named "escaped-DN scrubber" case: <c>\,</c> inside a CN must NOT
    /// split the run (one token, no half-DN leak), interior spaces stay in the value, and the
    /// surrounding quotes end/frame it.</summary>
    [Fact]
    public void Scrub_EscapedCommaAndInteriorSpaces_StayInsideOneRun_OneToken()
    {
        const string dn = "CN=Ops\\, Team GG,OU=AGDLP-Lab,DC=agdlp,DC=lab";
        var token = RedactionTokens.Dn(Redactor.SessionSalt, dn);

        Assert.Equal(
            $"member '{token}' is unresolvable",
            Redactor.Scrub($"member '{dn}' is unresolvable"));
    }

    /// <summary>Every run gets its OWN token; the same DN scrubs to the same token wherever it
    /// appears — the joinability property the whole D9 design exists for.</summary>
    [Fact]
    public void Scrub_EveryRunGetsItsToken_AndTheSameDnJoins()
    {
        const string parent = "CN=DL_App1_Read,OU=AGDLP-Lab,DC=agdlp,DC=lab";
        const string member = "CN=GG_Sales,OU=AGDLP-Lab,DC=agdlp,DC=lab";
        var parentToken = RedactionTokens.Dn(Redactor.SessionSalt, parent);
        var memberToken = RedactionTokens.Dn(Redactor.SessionSalt, member);

        Assert.Equal(
            $"nesting {parentToken}: member {memberToken}: parent again {parentToken}",
            Redactor.Scrub($"nesting {parent}: member {member}: parent again {parent}"));
    }

    /// <summary>Non-sensitive diagnostic text passes through byte-identical (already green on
    /// the identity stub — this pin keeps the scrubber from ever over-reaching into counts,
    /// HResults, kinds and step names, the D4 "safe by construction" fields).</summary>
    [Fact]
    public void Scrub_NonSensitiveText_PassesThroughUnchanged()
    {
        const string text = "retried 3 times; hr=0x80072030, kind=timeout, step=LoadScope";
        Assert.Equal(text, Redactor.Scrub(text));
    }

    /// <summary>Strings registered via <c>Learn</c> (the connect-time server/baseDn — hostnames
    /// match no <c>(CN|OU|DC)=</c> pattern) are replaced CASE-INSENSITIVELY with the token of
    /// the LEARNED spelling, so they join <see cref="Redactor.Host"/>-redacted fields; null /
    /// whitespace registrations are safe no-ops. Reflection: <c>Learn</c> does not exist on the
    /// WP1 stub — its absence is this test's red assertion.</summary>
    [Fact]
    public void Scrub_ReplacesLearnedServerStrings_CaseInsensitively_WithTheHostToken()
    {
        var learn = typeof(Redactor).GetMethod(
            "Learn", BindingFlags.Public | BindingFlags.Static, new[] { typeof(string) });
        Assert.True(
            learn is not null,
            "ADR-037 D9 pins Redactor.Learn(string?) — the connect-time server/baseDn registration.");

        // Process-wide by design (the ambient facade; this assembly runs sequentially and the
        // value is unique to this test, so nothing else can collide with it).
        learn!.Invoke(null, new object?[] { "dc01.scrub-lab.example" });
        learn.Invoke(null, new object?[] { null });
        learn.Invoke(null, new object?[] { "   " });

        var token = RedactionTokens.Host(Redactor.SessionSalt, "dc01.scrub-lab.example");
        Assert.Equal(
            $"LDAP bind to {token}:389 timed out",
            Redactor.Scrub("LDAP bind to DC01.Scrub-Lab.example:389 timed out"));
    }
}
