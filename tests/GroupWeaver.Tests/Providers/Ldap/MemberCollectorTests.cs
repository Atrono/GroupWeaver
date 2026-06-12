using GroupWeaver.Providers;

using Xunit;

namespace GroupWeaver.Tests.Providers.Ldap;

/// <summary>
/// Pins <see cref="MemberCollector.CollectAllMembers"/> via delegate fakes:
/// plain <c>member</c> short-circuits without any fetch, ranged keys drive the
/// follow-up loop until the server answers a <c>-*</c> range, defensive
/// terminations never spin, and a server that never terminates hits the
/// iteration cap.
/// </summary>
public class MemberCollectorTests
{
    private const string Dn = "CN=GG_Sales,OU=AGDLP-Lab,DC=agdlp,DC=lab";

    private static Func<string, (string, IReadOnlyList<string>)> NoFetchExpected() =>
        _ => throw new InvalidOperationException("fetchRange must not be invoked.");

    [Theory]
    [InlineData("member")]
    [InlineData("MEMBER")]
    [InlineData("Member")]
    public void PlainMemberKey_AnyCasing_ReturnedAsIs_NoFetch(string key)
    {
        var properties = new Dictionary<string, IReadOnlyList<string>>
        {
            [key] = ["CN=U1", "CN=U2"],
        };
        int fetches = 0;

        var members = MemberCollector.CollectAllMembers(Dn, properties, attr =>
        {
            fetches++;
            return (attr, []);
        });

        Assert.Equal(["CN=U1", "CN=U2"], members);
        Assert.Equal(0, fetches);
    }

    [Fact]
    public void RangedInitialKey_FetchesFollowUpRanges_UntilStarTerminator()
    {
        // Page 1 arrives inline as member;range=0-2; pages 2 and 3 via fetches.
        var properties = new Dictionary<string, IReadOnlyList<string>>
        {
            ["member;range=0-2"] = ["CN=A", "CN=B", "CN=C"],
        };
        var requested = new List<string>();
        var pages = new Queue<(string, IReadOnlyList<string>)>(
        [
            ("member;range=3-5", new[] { "CN=D", "CN=E", "CN=F" }),
            ("member;range=6-*", new[] { "CN=G" }),
        ]);

        var members = MemberCollector.CollectAllMembers(Dn, properties, attr =>
        {
            requested.Add(attr);
            return pages.Dequeue();
        });

        Assert.Equal(["member;range=3-*", "member;range=6-*"], requested);
        Assert.Equal(["CN=A", "CN=B", "CN=C", "CN=D", "CN=E", "CN=F", "CN=G"], members);
    }

    [Fact]
    public void InitialRangeAlreadyStarTerminated_Complete_NoFetch()
    {
        var properties = new Dictionary<string, IReadOnlyList<string>>
        {
            ["member;range=0-*"] = ["CN=A", "CN=B"],
        };

        var members = MemberCollector.CollectAllMembers(Dn, properties, NoFetchExpected());

        Assert.Equal(["CN=A", "CN=B"], members);
    }

    [Fact]
    public void NeitherMemberNorRangedKey_ReturnsEmptyList_NoFetch()
    {
        var properties = new Dictionary<string, IReadOnlyList<string>>
        {
            ["description"] = ["a group"],
        };

        var members = MemberCollector.CollectAllMembers(Dn, properties, NoFetchExpected());

        Assert.Empty(members);
    }

    [Fact]
    public void Duplicates_ArePreserved_CollectorDoesNotDeDup()
    {
        var properties = new Dictionary<string, IReadOnlyList<string>>
        {
            ["member;range=0-1"] = ["CN=A", "CN=A"],
        };

        var members = MemberCollector.CollectAllMembers(
            Dn, properties, _ => ("member;range=2-*", ["CN=A", "CN=B"]));

        Assert.Equal(["CN=A", "CN=A", "CN=A", "CN=B"], members);
    }

    [Fact]
    public void EmptyFetchValues_TerminateDefensively()
    {
        var properties = new Dictionary<string, IReadOnlyList<string>>
        {
            ["member;range=0-1"] = ["CN=A", "CN=B"],
        };
        int fetches = 0;

        var members = MemberCollector.CollectAllMembers(Dn, properties, _ =>
        {
            fetches++;
            return ("member;range=2-4", []); // bounded range, but no values: must stop
        });

        Assert.Equal(1, fetches);
        Assert.Equal(["CN=A", "CN=B"], members);
    }

    [Fact]
    public void FetchThatNeverReturnsTerminalRange_ThrowsInvalidDataException()
    {
        var properties = new Dictionary<string, IReadOnlyList<string>>
        {
            ["member;range=0-0"] = ["CN=A"],
        };

        // Always answers a bounded (non-*) range with values: a hostile/broken
        // server. The collector must hit its iteration cap, never loop forever.
        var ex = Assert.Throws<InvalidDataException>(() =>
            MemberCollector.CollectAllMembers(
                Dn, properties, _ => ("member;range=1-1", new[] { "CN=Again" })));

        Assert.Contains(Dn, ex.Message);
    }
}
