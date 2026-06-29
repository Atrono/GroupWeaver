using GroupWeaver.Core.Audit;

namespace GroupWeaver.App.ViewModels;

/// <summary>
/// The view-bound projection of an <see cref="AuditRunDiff"/> (ADR-032 D4 / #190): the four bucket
/// COUNTS (Fixed / New / Still-open / Now-unchecked), the honesty banner flags, and the captions the
/// <see cref="Views.AuditView"/> compare section renders. A pure projection — built from the diff and
/// the two runs it summarizes, never persisted, never an AD touch.
///
/// <para>The <b>honesty banner</b> fires when EITHER run had unchecked areas (the comparison is
/// partial — parallel to the existing Unchecked caveat); the <b>ruleset-mismatch banner</b> fires when
/// the two runs ran under different rulesets (drift labelled, not blended — <see cref="AuditRunDiff
/// .RulesetHashMismatch"/>). The bucket counts are projections (the determinism discipline); consumers
/// compare counts/flags, never record identity.</para>
/// </summary>
public sealed record AuditRunComparison(
    bool HasPreviousRun,
    int FixedCount,
    int NewCount,
    int StillOpenCount,
    int NowUncheckedCount,
    bool UncheckedPresent,
    bool RulesetMismatch,
    string PreviousRunLabel,
    string RulesetMismatchText)
{
    /// <summary>True when at least one finding was remediated (in a checked area) since the prior run.</summary>
    public bool HasFixed => FixedCount > 0;

    /// <summary>True when at least one finding newly surfaced since the prior run.</summary>
    public bool HasNew => NewCount > 0;

    /// <summary>True when at least one finding persists across both runs.</summary>
    public bool HasStillOpen => StillOpenCount > 0;

    /// <summary>True when at least one prior finding vanished only because its area was not expanded
    /// this run — the honesty-bearing bucket (never counted as Fixed).</summary>
    public bool HasNowUnchecked => NowUncheckedCount > 0;

    /// <summary>Builds the projection from a computed <paramref name="diff"/> and the two runs it
    /// summarizes (ADR-032 D4). The honesty banner fires when either run carried unchecked areas; the
    /// previous-run label restates the prior run's timestamp + ruleset for context.</summary>
    public static AuditRunComparison From(AuditRunDiff diff, AuditRun previous, AuditRun current)
    {
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        var uncheckedPresent = previous.UncheckedDns.Count > 0 || current.UncheckedDns.Count > 0;
        var label =
            $"vs. run {previous.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm} · ruleset '{previous.RulesetName}'";
        var mismatchText = diff.RulesetHashMismatch
            ? $"The previous run used a different ruleset ('{previous.RulesetName}') than this one ('{current.RulesetName}') — read this drift as a ruleset change, not pure remediation."
            : string.Empty;

        return new AuditRunComparison(
            HasPreviousRun: true,
            FixedCount: diff.Fixed.Count,
            NewCount: diff.New.Count,
            StillOpenCount: diff.StillOpen.Count,
            NowUncheckedCount: diff.NowUnchecked.Count,
            UncheckedPresent: uncheckedPresent,
            RulesetMismatch: diff.RulesetHashMismatch,
            PreviousRunLabel: label,
            RulesetMismatchText: mismatchText);
    }

    /// <summary>The "no previous run for this scope" state (ADR-032 D4): all buckets empty, no banners,
    /// a hint label — so the compare section explains why there is nothing to diff yet.</summary>
    public static AuditRunComparison NoPreviousRun(string rootDn) => new(
        HasPreviousRun: false,
        FixedCount: 0,
        NewCount: 0,
        StillOpenCount: 0,
        NowUncheckedCount: 0,
        UncheckedPresent: false,
        RulesetMismatch: false,
        PreviousRunLabel: $"No prior saved run for {rootDn} — save this run, then compare a later one against it.",
        RulesetMismatchText: string.Empty);
}
