using GroupWeaver.Core.Audit;
using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.Tests.Core.Audit;

/// <summary>
/// Pins the pure-Core statics of <see cref="AuditRun"/> (ADR-032 D1 / #190): the deterministic,
/// content-stable <see cref="AuditRun.ComputeRulesetHash"/> (the label that lets a comparison flag
/// drift under a CHANGED ruleset) and the <see cref="AuditRun.ToFinding"/> projection of a
/// <see cref="RuleViolation"/> into its persisted form. Both are total, pure, clock-free.
/// </summary>
public sealed class AuditRunTests
{
    // === ComputeRulesetHash: same ruleset -> same hash; any content change -> different hash =====

    [Fact]
    public void ComputeRulesetHash_SameRuleset_IsStableAndContentEqual()
    {
        // Two independent loads of the same default ruleset hash identically (content-stable, not
        // reference- or instance-keyed) — the property a comparison relies on to NOT cry "ruleset
        // changed" between two runs of the same ruleset.
        var a = RulesetLoader.LoadDefault();
        var b = RulesetLoader.LoadDefault();

        var hashA = AuditRun.ComputeRulesetHash(a);
        var hashB = AuditRun.ComputeRulesetHash(b);

        Assert.Equal(hashA, hashB);
        // Lowercase hex SHA-256 -> 64 hex chars, deterministic across repeated calls.
        Assert.Equal(64, hashA.Length);
        Assert.Equal(hashA, AuditRun.ComputeRulesetHash(a));
        Assert.Matches("^[0-9a-f]{64}$", hashA);
    }

    [Fact]
    public void ComputeRulesetHash_ChangedRuleset_DiffersFromTheOriginal()
    {
        var baseline = RulesetLoader.LoadDefault();
        // A single content change (the ruleset name is serialized) flips the hash — so a comparison
        // whose two runs disagree here is genuinely drift under a different ruleset, never a false alarm.
        var renamed = baseline with { Name = baseline.Name + " (edited)" };

        Assert.NotEqual(
            AuditRun.ComputeRulesetHash(baseline),
            AuditRun.ComputeRulesetHash(renamed));
    }

    [Fact]
    public void ComputeRulesetHash_RuleDisabled_DiffersFromTheOriginal()
    {
        var baseline = RulesetLoader.LoadDefault();
        // A behaviourally meaningful change (disabling the empty-group rule) must also flip the hash.
        var edited = baseline with { EmptyGroup = baseline.EmptyGroup with { Enabled = false } };

        Assert.NotEqual(
            AuditRun.ComputeRulesetHash(baseline),
            AuditRun.ComputeRulesetHash(edited));
    }

    // === ToFinding: the persisted projection of one RuleViolation ===============================

    [Fact]
    public void ToFinding_ProjectsRuleViolation_PreservingIdentityDnsAndMessage()
    {
        var violation = new RuleViolation
        {
            RuleId = RuleIds.Nesting,
            Severity = RuleSeverity.Error,
            Dns = new[] { "CN=DL_X,OU=Lab,DC=agdlp,DC=lab", "CN=User,OU=Lab,DC=agdlp,DC=lab" },
            Message = "DL must not contain a direct user member",
        };

        var finding = AuditRun.ToFinding(violation);

        Assert.Equal(RuleIds.Nesting, finding.RuleId);
        Assert.Equal(RuleSeverity.Error, finding.Severity);
        // PrimaryDn is Dns[0]; the full canonical Dns list survives (both nesting endpoints).
        Assert.Equal("CN=DL_X,OU=Lab,DC=agdlp,DC=lab", finding.PrimaryDn);
        Assert.Equal(violation.Dns, finding.Dns);
        Assert.Equal(violation.Message, finding.Message);
    }
}
