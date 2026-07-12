using Xunit;

namespace GroupWeaver.App.Tests.Diagnostics;

/// <summary>
/// Pins the ADR-037 D9 instance redaction core (WP10 #249) via <see cref="RedactionSurface"/>
/// (reflection — the type does not exist on the WP1 stub; its absence is the red assertion):
/// <c>Redaction.CreateSalted()</c> mints a fresh session salt, so two instances are two
/// "sessions" — tokens are stable WITHIN an instance and different ACROSS instances (the
/// unlinkability contract, testable in-process only through constructible instances); and
/// <c>Redaction.Identity</c> is the <c>--log-plain</c> twin: <c>Mode == "identity"</c>, every
/// value passes through verbatim, <c>Learn</c> registers nothing.
/// </summary>
public sealed class RedactionInstanceTests
{
    private const string LabDn = "CN=GG_Circle_A,OU=AGDLP-Lab,DC=agdlp,DC=lab";

    [Fact]
    public void SaltedInstances_AreStableWithin_AndUnlinkableAcrossSessions()
    {
        var type = RedactionSurface.Require();
        var sessionA = RedactionSurface.CreateSalted(type);
        var sessionB = RedactionSurface.CreateSalted(type);

        Assert.Equal("redacted", RedactionSurface.Mode(sessionA));

        var tokenA = RedactionSurface.Invoke(sessionA, "Dn", LabDn);
        Assert.Matches("^dn#[0-9a-f]{8}$", tokenA);
        Assert.Equal(tokenA, RedactionSurface.Invoke(sessionA, "Dn", LabDn)); // same session: joins
        Assert.NotEqual(tokenA, RedactionSurface.Invoke(sessionB, "Dn", LabDn)); // new salt: unlinkable
    }

    [Fact]
    public void IdentityInstance_PassesThroughVerbatim_AndReportsIdentity()
    {
        var type = RedactionSurface.Require();
        var identity = RedactionSurface.Identity(type);

        Assert.Equal("identity", RedactionSurface.Mode(identity));

        // Verbatim pass-through: the SAME string instances come back (never re-built, never
        // hashed) — the --log-plain contract for every helper, the scrubber included.
        Assert.Same(LabDn, RedactionSurface.Invoke(identity, "Dn", LabDn));
        Assert.Same("dc01.agdlp.lab", RedactionSurface.Invoke(identity, "Host", "dc01.agdlp.lab"));

        const string text = "failed on CN=X,DC=lab";
        Assert.Same(text, RedactionSurface.Invoke(identity, "Scrub", text));

        // Learn on the identity instance is inert: even text CONTAINING the learned string
        // still passes through verbatim — plain mode never replaces anything.
        RedactionSurface.Learn(identity, "dc01.agdlp.lab");
        const string hostText = "bind to dc01.agdlp.lab failed";
        Assert.Same(hostText, RedactionSurface.Invoke(identity, "Scrub", hostText));
    }
}
