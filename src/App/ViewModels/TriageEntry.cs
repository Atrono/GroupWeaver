using GroupWeaver.Core.Rules;

namespace GroupWeaver.App.ViewModels;

/// <summary>
/// The single source of truth for audit-triage ↔ global-ignore-entry translation (WP5e / ADR-028):
/// the <c>[ack]</c>/<c>[suppress]</c> note tags, DN glob-escaping, building a tagged
/// <see cref="MatchEntry"/>, recognising one, and matching a finding to its triage entry. Both the
/// shell (append/remove via the gate) and the <see cref="AuditViewModel"/> (would-be findings +
/// per-row status) consume it, so the tag grammar can never drift between the writer and the reader.
///
/// <para><b>Triage is an ignore entry, nothing more (ADR-028).</b> A tagged entry is an ordinary
/// dn-mode global ignore — the engine ignores it exactly as any other ignore entry; the tag lives
/// only in the human-facing <see cref="MatchEntry.Note"/>. No schema field, no schemaVersion bump.
/// <b>This writes only the ruleset file (via the gate) + in-memory state — never AD.</b></para>
/// </summary>
public static class TriageEntry
{
    /// <summary>The note tag marking an Acknowledge ignore entry.</summary>
    public const string AckTag = "[ack]";

    /// <summary>The note tag marking a Suppress ignore entry.</summary>
    public const string SuppressTag = "[suppress]";

    private const string DefaultReason = "triaged in audit";

    /// <summary>Escapes a subject DN for use as a <see cref="MatchEntry.Dn"/> glob. The ADR-008 glob
    /// dialect has NO literal-escape for its two metacharacters, so a DN containing a literal <c>*</c>
    /// (= "any run", crosses commas) or <c>?</c> (= "any one char") would otherwise match far more
    /// than the single object. We replace each with a single <c>?</c> wildcard: that keeps the glob
    /// LENGTH-EXACT (one glob char per DN char) so it still matches the intended DN — a literal <c>*</c>
    /// can no longer swallow the rest of the string. The price is that the one replaced position
    /// becomes "any single char" (a DN differing only at that exact column would also match) — a
    /// deliberate, documented FAIL-SAFE for a pathological case: real AD DNs never contain a bare
    /// <c>*</c>/<c>?</c> (RFC 4514 escapes them), so <see cref="Escape"/> is the identity on every
    /// genuine DN. An exact-only guarantee would need a glob literal-escape mechanism (a Core/ADR-008
    /// change), out of scope here.</summary>
    public static string Escape(string dn)
    {
        if (dn.IndexOf('*') < 0 && dn.IndexOf('?') < 0)
        {
            return dn;
        }

        var chars = dn.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] == '*')
            {
                chars[i] = '?';
            }
        }

        return new string(chars);
    }

    /// <summary>The note tag for a triage <paramref name="kind"/> (Acknowledge/Suppress only —
    /// Untriage is a removal, never an entry).</summary>
    public static string TagFor(TriageKind kind) => kind switch
    {
        TriageKind.Acknowledge => AckTag,
        TriageKind.Suppress => SuppressTag,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Untriage builds no entry."),
    };

    /// <summary>Builds the tagged dn-mode global-ignore entry for <paramref name="request"/>: the
    /// already-escaped DN in <see cref="MatchEntry.Dn"/>, the tag + reason in <see cref="MatchEntry.Note"/>
    /// (e.g. <c>"[suppress] triaged in audit"</c>). Endpoint stays <see cref="MatchEndpoint.Any"/> —
    /// a global ignore is never endpoint-narrowed (that is nesting-exception-only).</summary>
    public static MatchEntry Build(TriageRequest request) => new()
    {
        Dn = request.Dn,
        Note = $"{TagFor(request.Kind)} {NormalizeReason(request.Reason)}",
    };

    /// <summary>The triage kind a tagged ignore <paramref name="entry"/> represents, or
    /// <c>null</c> when it is a plain (untagged) ignore entry — those are NEVER touched by triage
    /// reversal. Recognises the tag at the start of the note (the <see cref="Build"/> grammar).</summary>
    public static TriageKind? KindOf(MatchEntry entry)
    {
        var note = entry.Note;
        if (note is null)
        {
            return null;
        }

        if (note.StartsWith(AckTag, StringComparison.Ordinal))
        {
            return TriageKind.Acknowledge;
        }

        if (note.StartsWith(SuppressTag, StringComparison.Ordinal))
        {
            return TriageKind.Suppress;
        }

        return null;
    }

    /// <summary>Whether <paramref name="entry"/> is the triage entry for the finding whose ESCAPED
    /// primary DN is <paramref name="escapedDn"/> with the given <paramref name="kind"/>: a tagged
    /// dn-mode entry whose <see cref="MatchEntry.Dn"/> equals the escaped DN (the stored spelling,
    /// ordinal — the engine match is glob/anchored but identity here is the literal stored string)
    /// and whose tag matches. The match key is escaped-DN + tag, so a per-finding entry is removed
    /// precisely on reversal without disturbing any other ignore entry.</summary>
    public static bool MatchesFinding(MatchEntry entry, string escapedDn, TriageKind kind) =>
        entry.Dn is { } dn
        && KindOf(entry) == kind
        && string.Equals(dn, escapedDn, StringComparison.Ordinal);

    /// <summary>Whether ANY tagged triage entry in <paramref name="ignore"/> covers the finding
    /// whose ESCAPED primary DN is <paramref name="escapedDn"/>, returning its kind (Acknowledge or
    /// Suppress) or <c>null</c> when none does. Drives the would-be table's per-row Status.</summary>
    public static TriageKind? StatusFor(IReadOnlyList<MatchEntry> ignore, string escapedDn)
    {
        foreach (var entry in ignore)
        {
            if (MatchesFinding(entry, escapedDn, TriageKind.Acknowledge))
            {
                return TriageKind.Acknowledge;
            }

            if (MatchesFinding(entry, escapedDn, TriageKind.Suppress))
            {
                return TriageKind.Suppress;
            }
        }

        return null;
    }

    private static string NormalizeReason(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? DefaultReason : reason.Trim();
}
