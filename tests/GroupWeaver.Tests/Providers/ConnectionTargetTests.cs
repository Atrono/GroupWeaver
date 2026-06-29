using GroupWeaver.Providers;

using Xunit;

namespace GroupWeaver.Tests.Providers;

/// <summary>
/// Pins <see cref="ConnectionTarget"/> (ADR-031 D5) — the one new attack surface the
/// connection-targeting feature introduces. The two free-text inputs (server/DC host +
/// base DN) flow into <see cref="AdsPath.For"/> → <c>LDAP://{server}/{dn}</c>, so the
/// validator is the gate that keeps a crafted host from injecting a scheme/path and a
/// garbage base DN from silently degrading the bind. The contract:
/// <list type="bullet">
/// <item>blank/whitespace = "not targeting" ⇒ <c>Ok(Value: null)</c> (the zero-config
/// serverless default — never an error).</item>
/// <item><b>server</b> = a bare host/IP/port/IPv6-literal only; ANY slash, backslash,
/// <c>LDAP://</c> scheme, LDAP-filter metacharacter (<c>( ) * \ = , ;</c>) or embedded
/// whitespace ⇒ <c>Error</c> with a non-null, inline-displayable message.</item>
/// <item><b>baseDn</b> = comma-separated <c>type=value</c> RDNs (attr type starts with an
/// ASCII letter, value non-empty); garbage ⇒ <c>Error</c>. Used ONLY as a search base,
/// never composed into a filter — so this is a well-formedness gate, not a filter-injection
/// defense (the filters stay fixed literals).</item>
/// </list>
/// On success the value is trimmed and stored verbatim (DNs are never canonicalized —
/// data-model rule). Consumers compare the projection <c>(IsValid, Value)</c> /
/// <c>(IsValid, ErrorMessage is null)</c>, never whole-record identity.
/// </summary>
public sealed class ConnectionTargetTests
{
    // --- ValidateServer: blank → Ok(null) (the zero-config default) -----------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void ValidateServer_BlankOrWhitespace_OkWithNullValue(string? server)
    {
        var result = ConnectionTarget.ValidateServer(server);

        Assert.True(result.IsValid);
        Assert.Null(result.Value);
        Assert.Null(result.ErrorMessage);
    }

    // --- ValidateServer: valid hosts → Ok with the trimmed value --------------------

    [Theory]
    [InlineData("dc01", "dc01")]
    [InlineData("dc01.corp.local", "dc01.corp.local")]
    [InlineData("10.0.0.5", "10.0.0.5")]
    [InlineData("host:636", "host:636")] // an explicit port
    [InlineData("[::1]", "[::1]")] // an IPv6 literal
    [InlineData("  dc01.corp.local  ", "dc01.corp.local")] // trimmed on success
    public void ValidateServer_BareHostOrIp_OkWithTrimmedValue(string server, string expected)
    {
        var result = ConnectionTarget.ValidateServer(server);

        Assert.True(result.IsValid, $"'{server}' should be accepted as a bare host/IP");
        Assert.Equal(expected, result.Value);
        Assert.Null(result.ErrorMessage);
    }

    // --- ValidateServer: injection / malformed → Error ------------------------------

    [Theory]
    // Slashes / scheme: must never terminate the host segment of LDAP://{server}/{dn}
    // or inject a scheme/path (the issue-#16 serverless-bind concern, now UI-reachable).
    [InlineData("a/b")]
    [InlineData("LDAP://x")]
    [InlineData("ldap://evil/DC=x")]
    [InlineData("//evil")]
    [InlineData("a\\b")] // backslash
    // LDAP-filter metacharacters ( ) * \ = , ; — rejected belt-and-braces even though
    // the server never reaches a filter (defense in depth at the host gate).
    [InlineData("a(b")]
    [InlineData("a)b")]
    [InlineData("a*b")]
    [InlineData("a=b")]
    [InlineData("a,b")]
    [InlineData("a;b")]
    // Embedded whitespace inside a non-blank value is not a valid host label.
    [InlineData("dc 01")]
    [InlineData("two hosts")]
    public void ValidateServer_InjectionOrMalformed_ErrorWithMessage(string server)
    {
        var result = ConnectionTarget.ValidateServer(server);

        Assert.False(result.IsValid, $"'{server}' must be rejected");
        Assert.Null(result.Value);
        Assert.False(
            string.IsNullOrWhiteSpace(result.ErrorMessage),
            "a rejected server must carry a non-blank, inline-displayable message (ADR-003 D7)");
    }

    // --- ValidateBaseDn: blank → Ok(null) (read defaultNamingContext) ---------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateBaseDn_BlankOrWhitespace_OkWithNullValue(string? baseDn)
    {
        var result = ConnectionTarget.ValidateBaseDn(baseDn);

        Assert.True(result.IsValid);
        Assert.Null(result.Value);
        Assert.Null(result.ErrorMessage);
    }

    // --- ValidateBaseDn: well-formed DNs → Ok with the trimmed (verbatim) value -----

    [Theory]
    [InlineData("DC=corp,DC=local", "DC=corp,DC=local")]
    [InlineData("OU=x,DC=corp,DC=local", "OU=x,DC=corp,DC=local")]
    [InlineData("CN=Users,DC=corp,DC=local", "CN=Users,DC=corp,DC=local")]
    [InlineData("  DC=corp,DC=local  ", "DC=corp,DC=local")] // trimmed on success
    public void ValidateBaseDn_WellFormed_OkWithTrimmedValue(string baseDn, string expected)
    {
        var result = ConnectionTarget.ValidateBaseDn(baseDn);

        Assert.True(result.IsValid, $"'{baseDn}' should be accepted as a well-formed DN");
        // Stored verbatim — never canonicalized (data-model rule); only the outer trim applies.
        Assert.Equal(expected, result.Value);
        Assert.Null(result.ErrorMessage);
    }

    // --- ValidateBaseDn: malformed → Error ------------------------------------------

    [Theory]
    [InlineData("notadn")] // no '=' at all
    [InlineData("=novalue")] // empty attribute type
    [InlineData("DC=")] // empty value
    [InlineData(",DC=corp")] // leading comma → empty first RDN
    [InlineData("DC=corp,")] // trailing comma → empty last RDN
    [InlineData("1DC=corp")] // attribute type must start with a letter
    public void ValidateBaseDn_Malformed_ErrorWithMessage(string baseDn)
    {
        var result = ConnectionTarget.ValidateBaseDn(baseDn);

        Assert.False(result.IsValid, $"'{baseDn}' must be rejected as not a well-formed DN");
        Assert.Null(result.Value);
        Assert.False(
            string.IsNullOrWhiteSpace(result.ErrorMessage),
            "a rejected base DN must carry a non-blank, inline-displayable message (ADR-003 D7)");
    }

    // --- ConnectionTargetResult factory projections ---------------------------------

    [Fact]
    public void Result_Factories_ProjectIsValidValueAndMessage()
    {
        var ok = ConnectionTargetResult.Ok("dc01");
        Assert.True(ok.IsValid);
        Assert.Equal("dc01", ok.Value);
        Assert.Null(ok.ErrorMessage);

        var okNull = ConnectionTargetResult.Ok(null);
        Assert.True(okNull.IsValid);
        Assert.Null(okNull.Value);
        Assert.Null(okNull.ErrorMessage);

        var error = ConnectionTargetResult.Error("bad host");
        Assert.False(error.IsValid);
        Assert.Null(error.Value);
        Assert.Equal("bad host", error.ErrorMessage);
    }
}
