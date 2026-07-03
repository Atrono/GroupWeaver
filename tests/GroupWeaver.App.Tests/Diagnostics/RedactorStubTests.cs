using GroupWeaver.App.Diagnostics;

using Xunit;

namespace GroupWeaver.App.Tests.Diagnostics;

/// <summary>
/// Pins the WP1 <see cref="Redactor"/> IDENTITY-STUB contract (ADR-037 D9): until WP10 (#249)
/// lands, <see cref="Redactor.Mode"/> reports <c>"identity"</c> (the AppStarted banner field —
/// truthful "logs are PLAIN" evidence) and every helper passes its input through unchanged,
/// null included. WP10 replaces the VALUES with session-salted hashes + the free-text scrubber;
/// flipping these assertions is that PR's deliberate, reviewed test update. The
/// <see cref="Redactor.SessionSalt"/> is already minted (16 bytes) so the WP1→WP10 swap stays
/// implementation-only.
/// </summary>
public sealed class RedactorStubTests
{
    [Fact]
    public void Mode_ReportsIdentity_TheWp1PlainTruth()
        => Assert.Equal("identity", Redactor.Mode);

    [Fact]
    public void Dn_Host_Scrub_Path_RunFile_AreIdentityPassThroughs_NullIncluded()
    {
        const string dn = "CN=GG_Circle_A,OU=AGDLP-Lab,DC=agdlp,DC=lab";
        Assert.Same(dn, Redactor.Dn(dn));
        Assert.Same("dc01.agdlp.lab", Redactor.Host("dc01.agdlp.lab"));
        Assert.Same("failed on CN=X,DC=lab", Redactor.Scrub("failed on CN=X,DC=lab"));

        // The TYPED stubs for shapes Scrub's (CN|OU|DC)= pattern would never match: a full
        // user path and a run-file name with a slugified-DN slug — the AuditRunSkipped call
        // sites route through these (Path for the runs directory, RunFile for the file name).
        const string userPath = @"C:\Users\alice\AppData\Roaming\GroupWeaver\runs";
        Assert.Same(userPath, Redactor.Path(userPath));
        const string runFile = "20260101T000000Z-OU_AGDLP-Lab_DC_agdlp_DC_lab.json";
        Assert.Same(runFile, Redactor.RunFile(runFile));

        Assert.Null(Redactor.Dn(null));
        Assert.Null(Redactor.Host(null));
        Assert.Null(Redactor.Scrub(null));
        Assert.Null(Redactor.Path(null));
        Assert.Null(Redactor.RunFile(null));
    }

    [Fact]
    public void SessionSalt_IsMinted_SixteenBytes()
        => Assert.Equal(16, Redactor.SessionSalt.Length);
}
