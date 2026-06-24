using GroupWeaver.Core.Rules;

namespace GroupWeaver.App.ViewModels;

/// <summary>
/// The triage intent of one request / one ignore entry (WP5e / ADR-028). Acknowledge and
/// Suppress are EQUAL-STRENGTH reversible global-ignore entries — both take the finding out of
/// the live <see cref="RuleEngine.Evaluate"/> report (lifting health, silencing graph + sidebar);
/// the ONLY difference is the <see cref="TriageEntry"/> note tag, a human annotation of intent.
/// <see cref="Untriage"/> removes the matching tagged entry, reopening the finding.
/// </summary>
public enum TriageKind
{
    /// <summary>Acknowledge: a tagged (<c>[ack]</c>) global-ignore entry — "seen, accepted".</summary>
    Acknowledge,

    /// <summary>Suppress: a tagged (<c>[suppress]</c>) global-ignore entry — "intentionally hidden".</summary>
    Suppress,

    /// <summary>Reverse a prior Acknowledge/Suppress — remove the matching tagged ignore entry.</summary>
    Untriage,
}

/// <summary>
/// One audit-triage request the <see cref="AuditViewModel"/> hands the shell's triage seam
/// (WP5e / ADR-028). The shell turns each into a global-ignore <see cref="MatchEntry"/> mutation
/// (append for Acknowledge/Suppress, remove for Untriage) and routes the whole batch through the
/// existing <c>SettingsViewModel</c> gate (BuildRuleset → Serialize → RulesetLoader.Load → Save).
/// </summary>
/// <param name="Dn">The finding subject DN (<see cref="RuleViolation.PrimaryDn"/>) — already
/// glob-ESCAPED by <see cref="TriageEntry.Escape"/> so a literal <c>*</c>/<c>?</c> in the DN stays
/// exact, never a wildcard, in the stored <see cref="MatchEntry.Dn"/>.</param>
/// <param name="RuleId">The finding's rule id (carried for the note + future per-rule precision;
/// matching is by escaped DN + tag).</param>
/// <param name="Kind">Acknowledge, Suppress, or Untriage.</param>
/// <param name="Reason">Optional human reason folded into the entry note after the tag; null/blank
/// falls back to a default phrase.</param>
public sealed record TriageRequest(string Dn, string RuleId, TriageKind Kind, string? Reason);
