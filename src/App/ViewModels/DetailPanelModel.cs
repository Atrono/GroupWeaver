using GroupWeaver.Core.Model;
using GroupWeaver.Core.Rules;
using GroupWeaver.Providers;

namespace GroupWeaver.App.ViewModels;

/// <summary>
/// Load-state of the selected object, derived from snapshot state ALONE
/// (ADR-007 D3): DN absent or External ∧ ¬IsLoaded → <see cref="NotLoaded"/>
/// ("expand/Refresh to resolve"); External ∧ IsLoaded → <see cref="Unresolvable"/>
/// (FSP — attributes genuinely unavailable); everything else → <see cref="Loaded"/>
/// (a group whose MEMBERS are unloaded still has loaded attributes).
/// </summary>
public enum DetailPanelState
{
    /// <summary>The object's attributes are in the snapshot.</summary>
    Loaded,

    /// <summary>Never fetched — expand or Refresh resolves it.</summary>
    NotLoaded,

    /// <summary>Fetched and genuinely unresolvable (FSP, AP 1.5).</summary>
    Unresolvable,
}

/// <summary>One attribute row of the detail panel: label and value, verbatim.</summary>
public sealed record DetailRow(string Label, string Value);

/// <summary>
/// One audit-state chip of the detail-panel header (WP4 / #148): rule-class +
/// severity + count, derived ENGINE-SIDE from <see cref="RuleReport.ViolationsFor"/>
/// — NEVER from AD attributes (it carries no attribute data, so the whitelist baseline
/// is untouched). <see cref="HasFindings"/> is <c>true</c> for a class with ≥1 finding
/// (colour = that class's max <see cref="Severity"/>, with <see cref="Count"/>); the
/// single <c>false</c> "No findings" chip (green) is emitted only when the DN is clean.
/// Naming findings (user-chosen kebab ids) collapse under one "Naming" chip.
/// </summary>
public sealed record AuditChip(string Label, RuleSeverity Severity, int Count, bool HasFindings);

/// <summary>
/// The immutable detail-panel projection (ADR-007 D2): <see cref="Build"/> is the
/// SINGLE choke point between the snapshot and the view — the panel binds this
/// type and nothing else, so binding a domain object into XAML is structurally
/// impossible without editing <see cref="Build"/>. Header = the four typed members
/// of the data-model contract; <see cref="Rows"/> mirror
/// <see cref="AdObject.Attributes"/> VERBATIM — the UI never re-filters, a provider
/// whitelist bug must become visible, not masked. Pinned by
/// <c>tests/GroupWeaver.App.Tests/WorkspaceDetailTests.cs</c>.
/// </summary>
public sealed record DetailPanelModel
{
    /// <summary>The selected DN, verbatim — never canonicalized (data-model rule).</summary>
    public required string Dn { get; init; }

    /// <summary>Kind per the <see cref="DirectorySnapshot.GetKind"/> contract:
    /// a DN absent from the snapshot is <see cref="AdObjectKind.External"/>.</summary>
    public required AdObjectKind Kind { get; init; }

    /// <summary>Display name of the snapshot object; <c>null</c> when the DN is
    /// absent from the snapshot (nothing fetched, nothing to fabricate).</summary>
    public required string? Name { get; init; }

    /// <summary>SAM account name of the snapshot object, if it has one.</summary>
    public required string? SamAccountName { get; init; }

    /// <summary>Load-state honesty per ADR-007 D3 (see <see cref="DetailPanelState"/>).</summary>
    public required DetailPanelState State { get; init; }

    /// <summary>The <see cref="AdObject.Attributes"/> mirror — same count, same pairs,
    /// NO re-filtering. Known keys in <see cref="AttributeWhitelist.FetchProperties"/>
    /// declaration order, unknown keys appended alphabetically (ADR-007 D4); empty
    /// when the DN is absent from the snapshot or the object has no attributes.</summary>
    public required IReadOnlyList<DetailRow> Rows { get; init; }

    /// <summary>
    /// The WP4 (#148) audit-state chips for this DN, derived ENGINE-SIDE from the
    /// supplied <see cref="RuleReport"/> (<see cref="RuleReport.ViolationsFor"/>) — rule
    /// class + severity + count, NEVER attribute data, so the whitelist baseline (the
    /// <see cref="Rows"/> mirror) is untouched. Empty when <see cref="Build"/> is called
    /// with a <c>null</c> report (Plan/Gap contexts that hold no report) or for a frontier
    /// DN — degrade gracefully, no chips. Otherwise one chip per finding-bearing rule class
    /// (max severity + count), or the single green "No findings" chip when the DN is clean.
    /// </summary>
    public required IReadOnlyList<AuditChip> AuditChips { get; init; }

    /// <summary>
    /// The WP4 (#148) privacy-baseline caption shown under the attribute rows when any
    /// are present — grammatically pluralized on <see cref="Rows"/> count so a single
    /// whitelisted attribute reads "1 whitelisted attribute" (redesign-fidelity fix; the
    /// view binds this instead of a count-only <c>StringFormat</c> that was always plural).
    /// </summary>
    public string PrivacyNote =>
        $"Showing {Rows.Count} whitelisted attribute{(Rows.Count == 1 ? "" : "s")} — "
        + "all others are hidden by the privacy baseline.";

    /// <summary>
    /// Projects <paramref name="dn"/> from <paramref name="snapshot"/> — a pure,
    /// synchronous snapshot read: never calls a provider, never touches the busy
    /// gate (ADR-007 D1). Returns <c>null</c> iff <paramref name="dn"/> is
    /// <c>null</c> (no selection, no panel). <paramref name="report"/> sources the WP4
    /// audit chips (engine-side, <see cref="RuleReport.ViolationsFor"/>); <c>null</c>
    /// (Plan/Gap contexts with no report) yields no chips — never an attribute read.
    /// </summary>
    public static DetailPanelModel? Build(DirectorySnapshot snapshot, string? dn, RuleReport? report = null)
    {
        if (dn is null)
        {
            return null;
        }

        if (!snapshot.TryGetObject(dn, out var obj))
        {
            // Frontier DN (member-edge endpoint outside Objects): never fetched —
            // an honest NotLoaded header with no fabricated name, rows or chips.
            return new DetailPanelModel
            {
                Dn = dn,
                Kind = AdObjectKind.External,
                Name = null,
                SamAccountName = null,
                State = DetailPanelState.NotLoaded,
                Rows = [],
                AuditChips = [],
            };
        }

        var state = obj.Kind is not AdObjectKind.External
            ? DetailPanelState.Loaded
            : snapshot.IsLoaded(dn) ? DetailPanelState.Unresolvable : DetailPanelState.NotLoaded;

        return new DetailPanelModel
        {
            Dn = dn,
            Kind = obj.Kind,
            Name = obj.Name,
            SamAccountName = obj.SamAccountName,
            State = state,
            Rows = BuildRows(obj.Attributes),
            AuditChips = BuildAuditChips(report, dn),
        };
    }

    /// <summary>The WP4 chip projection: groups <see cref="RuleReport.ViolationsFor"/>
    /// by rule CLASS (naming's user kebab-ids collapse under "Naming"), one chip per
    /// class carrying its max severity + finding count, in fixed class order. A
    /// <c>null</c> report (Plan/Gap) yields no chips; a known-but-clean DN yields the
    /// single green "No findings" chip. Pure finding-structure (RuleId/Severity) — never
    /// an attribute read, so the whitelist baseline stays intact.</summary>
    private static IReadOnlyList<AuditChip> BuildAuditChips(RuleReport? report, string dn)
    {
        if (report is null)
        {
            return [];
        }

        var findings = report.ViolationsFor(dn);
        if (findings.Count == 0)
        {
            // Clean DN under a real report: the honest floor (no per-class "pass" claim,
            // which would need a fragile kind+ruleset applicability map — WP4 ships the floor).
            return [new AuditChip("No findings", RuleSeverity.Info, 0, HasFindings: false)];
        }

        // Max severity + count per class, in fixed presentation order so chips never reshuffle.
        var maxByClass = new Dictionary<string, (RuleSeverity Severity, int Count)>(StringComparer.Ordinal);
        foreach (var finding in findings)
        {
            var label = ClassLabel(finding.RuleId);
            if (maxByClass.TryGetValue(label, out var acc))
            {
                maxByClass[label] = (finding.Severity > acc.Severity ? finding.Severity : acc.Severity, acc.Count + 1);
            }
            else
            {
                maxByClass[label] = (finding.Severity, 1);
            }
        }

        var chips = new List<AuditChip>(maxByClass.Count);
        foreach (var label in ClassOrder)
        {
            if (maxByClass.TryGetValue(label, out var acc))
            {
                chips.Add(new AuditChip(label, acc.Severity, acc.Count, HasFindings: true));
            }
        }

        return chips;
    }

    /// <summary>Fixed presentation order of the audit chip classes.</summary>
    private static readonly string[] ClassOrder = ["Nesting", "Circular", "Naming", "Empty groups"];

    /// <summary>Maps a <see cref="RuleViolation.RuleId"/> to its chip class label: the three
    /// fixed ids to their names, every user-chosen naming id to "Naming".</summary>
    private static string ClassLabel(string ruleId) => ruleId switch
    {
        RuleIds.Nesting => "Nesting",
        RuleIds.Circular => "Circular",
        RuleIds.EmptyGroup => "Empty groups",
        _ => "Naming",
    };

    /// <summary>The D2 mirror: one row per attribute, values verbatim. Known labels
    /// use the whitelist's canonical casing (the provider's casing by contract).</summary>
    private static IReadOnlyList<DetailRow> BuildRows(
        IReadOnlyDictionary<string, string> attributes)
    {
        if (attributes.Count == 0)
        {
            return [];
        }

        var rows = new List<DetailRow>(attributes.Count);
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in AttributeWhitelist.FetchProperties)
        {
            if (attributes.TryGetValue(property, out var value))
            {
                rows.Add(new DetailRow(property, value));
                known.Add(property);
            }
        }

        foreach (var (label, value) in attributes
            .Where(pair => !known.Contains(pair.Key))
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(new DetailRow(label, value));
        }

        return rows;
    }
}
