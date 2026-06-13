using GroupWeaver.Core.Rules;

namespace GroupWeaver.App.ViewModels;

/// <summary>
/// One row of the AP 3.4 violations sidebar (ADR-010 §5): the immutable projection of
/// a <see cref="RuleViolation"/> the <see cref="ViolationsSidebarView"/> binds — the
/// severity (glyph color + redundant letter), the presentation message, the resolved
/// subject name (falls back to the DN for raw-External anchors), and the jump anchor
/// (<see cref="RuleViolation.PrimaryDn"/> = <c>Dns[0]</c>).
///
/// S4 ships the record + the view binding; the S5 VM integration fills the
/// projection (<c>OnReportChanged</c>, in canonical report order — unshuffled) and the
/// jump-to-node command. Severity is NOT a detail-panel attribute (ADR-007); this row
/// is the only App-side surface that pairs a finding with its subject name.
/// </summary>
public sealed record ViolationRowModel(
    RuleSeverity Severity,
    string Message,
    string SubjectName,
    string PrimaryDn);
