using CommunityToolkit.Mvvm.ComponentModel;

using GroupWeaver.Core.Rules;

namespace GroupWeaver.App.ViewModels;

/// <summary>
/// One row of the AP 3.4 violations sidebar (ADR-010 §5): the projection of a
/// <see cref="RuleViolation"/> the <see cref="Views.ViolationsSidebarView"/> binds — the
/// severity (glyph color + redundant letter), the presentation message, the resolved
/// subject name (falls back to the DN for raw-External anchors), and the jump anchor
/// (<see cref="RuleViolation.PrimaryDn"/> = <c>Dns[0]</c>). The four projection fields are
/// immutable; only <see cref="IsActive"/> mutates — the selection-sync highlight the VM
/// flips in <c>OnSelectedDnChanged</c> for every row whose <see cref="PrimaryDn"/> matches
/// the current selection (an <see cref="ObservableProperty"/> so the highlight repaints).
///
/// Severity is NOT a detail-panel attribute (ADR-007); this row is the only App-side
/// surface that pairs a finding with its subject name.
/// </summary>
public sealed partial class ViolationRowModel : ObservableObject
{
    public ViolationRowModel(
        RuleSeverity severity,
        string message,
        string subjectName,
        string primaryDn,
        string whyItMatters,
        IReadOnlyList<string> howToFix)
    {
        Severity = severity;
        Message = message;
        SubjectName = subjectName;
        PrimaryDn = primaryDn;
        WhyItMatters = whyItMatters;
        HowToFix = howToFix;
    }

    /// <summary>Effective severity — drives the glyph color/letter via the one
    /// <see cref="Views.SeverityConverters"/> palette (overlay-color parity with the
    /// graph halos).</summary>
    public RuleSeverity Severity { get; }

    /// <summary>The finding's presentation message (canonical, culture-invariant).</summary>
    public string Message { get; }

    /// <summary>The anchor object's display name, resolved snapshot-only; raw-External
    /// anchors fall back to the DN.</summary>
    public string SubjectName { get; }

    /// <summary>The jump-to-node anchor (<c>Dns[0]</c>) — the command parameter and the
    /// selection-sync match key (compared under <c>Dn.Comparer</c>).</summary>
    public string PrimaryDn { get; }

    /// <summary>The per-rule-class "why it matters" rationale (#198) — the SAME static copy the
    /// Audit screen shows, sourced from <see cref="AuditFindingDetail.WhyItMatters"/> so the two
    /// surfaces are identical by construction. Keyed only on the finding's rule class, never on an
    /// AD attribute; the sidebar (not the ADR-007 whitelist detail panel) legitimately owns this.</summary>
    public string WhyItMatters { get; }

    /// <summary>The per-rule-class numbered "how to fix" steps (#198) — the SAME static copy the
    /// Audit screen shows (<see cref="AuditFindingDetail.HowToFix"/>). Display text only.</summary>
    public IReadOnlyList<string> HowToFix { get; }

    /// <summary>Selection-sync highlight (ADR-010 §5): <c>true</c> while this row's
    /// <see cref="PrimaryDn"/> matches the current graph/panel selection. The VM flips it
    /// in <c>OnSelectedDnChanged</c>; multiple findings on one DN all highlight.</summary>
    [ObservableProperty]
    private bool _isActive;
}
