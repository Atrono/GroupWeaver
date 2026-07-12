using System;
using System.Reflection;

using GroupWeaver.App.Diagnostics;

using Xunit;

namespace GroupWeaver.App.Tests.Diagnostics;

/// <summary>
/// Pins the ADR-037 D9 <see cref="Redactor"/> contract (WP10 #249 — redaction GATES the next
/// tagged release). REPLACES <c>RedactorStubTests</c>, whose WP1 identity pins documented
/// themselves as "flipping these assertions is that PR's deliberate, reviewed test update" —
/// this is that update, written RED-first against the identity stub.
///
/// <para><b>The pinned token formula</b> (see <see cref="RedactionTokens"/>):
/// <c>dn#</c>/<c>host#</c>/<c>path#</c>/<c>run#</c> + the first 8 lowercase hex of
/// SHA-256(<see cref="Redactor.SessionSalt"/> ‖ UTF-8(value)) — stable within a session so
/// events join, salt-dependent so tokens are unlinkable across sessions.</para>
///
/// <para><b>The pinned null/empty shape:</b> <c>null</c> stays <c>null</c> (the existing
/// <c>[return: NotNullIfNotNull]</c> contract — E2eChannel's nullable <c>selectedDn</c> keeps
/// its JSON-null "no value" semantics, and null carries no data to protect); empty/whitespace
/// becomes the harmless fixed token <c>dn#empty</c>/<c>host#empty</c>/<c>path#empty</c>/
/// <c>run#empty</c> (never hashed — a hash of "" would masquerade as a real subject).</para>
/// </summary>
public sealed class RedactorContractTests
{
    private const string LabDn = "CN=GG_Circle_A,OU=AGDLP-Lab,DC=agdlp,DC=lab";

    // === Mode: the AppStarted banner truth (ADR-037 D6) =======================================

    /// <summary>The DEFAULT is the salted redactor — <c>Mode == "redacted"</c>; only
    /// <c>--log-plain</c> swaps in the identity instance (<c>Mode == "identity"</c>, pinned in
    /// <see cref="RedactionInstanceTests"/>).</summary>
    [Fact]
    public void Mode_DefaultsToRedacted_TheD9SafeDefault()
        => Assert.Equal("redacted", Redactor.Mode);

    // === Dn / Host: the salted-hash tokens =====================================================

    [Fact]
    public void Dn_IsTheSaltedSha256Token_AndStableWithinTheSession()
    {
        Assert.Equal(RedactionTokens.Dn(Redactor.SessionSalt, LabDn), Redactor.Dn(LabDn));
        Assert.Matches("^dn#[0-9a-f]{8}$", Redactor.Dn(LabDn));
        Assert.Equal(Redactor.Dn(LabDn), Redactor.Dn(LabDn)); // stable: events join within a session
    }

    /// <summary>The value is hashed as its raw UTF-8 bytes, AS-GIVEN (umlauts, escaped commas
    /// — DNs are never canonicalized, [[data-model]]); no fragment of it survives.</summary>
    [Fact]
    public void Dn_HashesTheUtf8BytesAsGiven_UmlautAndEscapedCommaIncluded()
    {
        const string dn = "CN=GG_Übung\\, Kernteam,OU=AGDLP-Lab,DC=agdlp,DC=lab";
        Assert.Equal(RedactionTokens.Dn(Redactor.SessionSalt, dn), Redactor.Dn(dn));
        Assert.DoesNotContain("Übung", Redactor.Dn(dn), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Unlinkability is structural in the pinned formula: the token depends on the
    /// session salt, so a new salt (a new session) yields a different token for the same value.</summary>
    [Fact]
    public void Dn_TokenIsSaltDependent_UnlinkableAcrossSessions()
    {
        Assert.Equal(RedactionTokens.Dn(Redactor.SessionSalt, LabDn), Redactor.Dn(LabDn));
        Assert.NotEqual(RedactionTokens.Dn(new byte[16], LabDn), Redactor.Dn(LabDn));
    }

    [Fact]
    public void Host_IsTheSaltedToken()
    {
        const string host = "dc01.agdlp.lab";
        Assert.Equal(RedactionTokens.Host(Redactor.SessionSalt, host), Redactor.Host(host));
        Assert.Matches("^host#[0-9a-f]{8}$", Redactor.Host(host));
    }

    // === Path / RunFile: the shapes Scrub's (CN|OU|DC)= pattern never matches ==================

    /// <summary>A full user path hashes WHOLLY to <c>path#&lt;hash8&gt;</c> — the user name in
    /// <c>C:\Users\&lt;name&gt;\…</c> is D9-sensitive and must not survive.</summary>
    [Fact]
    public void Path_IsTheSaltedToken_TheUserNameNeverSurvives()
    {
        const string userPath = @"C:\Users\alice\AppData\Roaming\GroupWeaver\runs";
        Assert.Equal(RedactionTokens.Path(Redactor.SessionSalt, userPath), Redactor.Path(userPath));
        Assert.DoesNotContain("alice", Redactor.Path(userPath), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A canonical run-file name (<c>&lt;utcstamp&gt;-&lt;rootDn-slug&gt;.json</c>,
    /// the AuditRunStore shape) keeps its timestamp JOINABLE and hashes only the slug:
    /// <c>&lt;utcstamp&gt;-run#&lt;hash8&gt;.json</c>.</summary>
    [Fact]
    public void RunFile_HashesTheSlug_AndKeepsTheTimestampJoinable()
    {
        const string name = "20260101T000000Z-OU_AGDLP-Lab_DC_agdlp_DC_lab.json";
        var expected =
            "20260101T000000Z-"
            + RedactionTokens.Run(Redactor.SessionSalt, "OU_AGDLP-Lab_DC_agdlp_DC_lab")
            + ".json";
        Assert.Equal(expected, Redactor.RunFile(name));
    }

    /// <summary>A name NOT matching the canonical shape fails safe: the WHOLE input hashes to
    /// <c>run#&lt;hash8&gt;</c> (nothing of an unrecognized name is trusted to be harmless).</summary>
    [Fact]
    public void RunFile_NonCanonicalName_HashesWholly()
        => Assert.Equal(
            RedactionTokens.Run(Redactor.SessionSalt, "weird-name.txt"),
            Redactor.RunFile("weird-name.txt"));

    // === null / empty ==========================================================================

    [Fact]
    public void Null_PassesThrough_EmptyGetsTheFixedToken()
    {
        // null stays null — [return: NotNullIfNotNull] call sites (E2eChannel's nullable
        // selectedDn JSON null) keep their "no value" semantics; null carries no data.
        Assert.Null(Redactor.Dn(null));
        Assert.Null(Redactor.Host(null));
        Assert.Null(Redactor.Path(null));
        Assert.Null(Redactor.RunFile(null));
        Assert.Null(Redactor.Scrub(null));

        // empty/whitespace never reaches the hash: the harmless fixed token.
        Assert.Equal("dn#empty", Redactor.Dn(string.Empty));
        Assert.Equal("dn#empty", Redactor.Dn("   "));
        Assert.Equal("host#empty", Redactor.Host(string.Empty));
        Assert.Equal("path#empty", Redactor.Path(string.Empty));
        Assert.Equal("run#empty", Redactor.RunFile(string.Empty));

        // Scrub is a TEXT filter, not a value redactor: empty text is simply empty.
        Assert.Equal(string.Empty, Redactor.Scrub(string.Empty));
    }

    // === Session salt + the facade's new surface ===============================================

    /// <summary>Carried over from <c>RedactorStubTests</c>: the per-process salt is minted
    /// eagerly, 16 bytes — and it is what the static facade's tokens hash with (pinned by the
    /// expected-token equalities above).</summary>
    [Fact]
    public void SessionSalt_IsMinted_SixteenBytes()
        => Assert.Equal(16, Redactor.SessionSalt.Length);

    /// <summary>The facade grows exactly two members for WP10: <c>Current</c> (the ambient
    /// <c>Redaction</c> instance — salted by default, swapped to <c>Redaction.Identity</c> by
    /// Program's <c>--log-plain</c> path) and <c>Learn(string?)</c> (the connect-time
    /// server/baseDn registration LdapProvider calls). Reflection-pinned so this file compiles
    /// against the WP1 stub; <see cref="RedactionInstanceTests"/> pins the instance behavior.</summary>
    [Fact]
    public void Facade_ExposesTheCurrentInstance_AndTheLearnRegistration()
    {
        var current = typeof(Redactor).GetProperty("Current", BindingFlags.Public | BindingFlags.Static);
        Assert.True(
            current is not null && current.PropertyType.FullName == RedactionSurface.TypeName,
            $"ADR-037 D9 pins Redactor.Current : {RedactionSurface.TypeName} — the ambient instance "
            + "Program swaps to Redaction.Identity under --log-plain.");

        var learn = typeof(Redactor).GetMethod(
            "Learn", BindingFlags.Public | BindingFlags.Static, new[] { typeof(string) });
        Assert.True(
            learn is not null,
            "ADR-037 D9 pins Redactor.Learn(string?) — the connect-time server/baseDn "
            + "registration feeding Scrub's learned-string replacement.");
    }
}
