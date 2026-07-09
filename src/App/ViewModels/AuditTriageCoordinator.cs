namespace GroupWeaver.App.ViewModels;

/// <summary>
/// The Ack/Suppress/Untriage batch-building + shell-seam mechanics extracted out of
/// <see cref="AuditViewModel"/> (#299): owns the triage callback seam
/// (<see cref="UseTriageCallback"/>) and the selected-row / single-row request-building logic the
/// WP5e commands delegate to. Plain class (mirrors <see cref="AuditFindingsView"/>, not
/// <see cref="Export.AuditExportService"/> — there is no unmanaged resource to dispose).
/// </summary>
public sealed class AuditTriageCoordinator
{
    /// <summary>The shell-supplied triage seam (WP5e / ADR-028): the <see cref="AuditViewModel"/>
    /// hands it a batch of <see cref="TriageRequest"/>s; the SHELL appends/removes the global-ignore
    /// entries and routes them through the existing <c>SettingsViewModel</c> gate (the single write
    /// path — never AD). Dead until armed by <see cref="UseTriageCallback"/> (mirrors the Design-plan
    /// callback idiom), so a headless / un-wired audit never half-acts; the commands no-op when null.</summary>
    private Action<IReadOnlyList<TriageRequest>>? _triage;

    /// <summary>Arms the shell's triage seam (WP5e / ADR-028): the install idiom mirrors the
    /// workspace Design-plan/Audit callbacks (<c>OnRootChosen</c>). Until called the Ack/Suppress/
    /// Un-triage commands are inert no-ops, so a renderer-less / headless audit never half-acts.
    /// Idempotent — the last writer wins.</summary>
    public void UseTriageCallback(Action<IReadOnlyList<TriageRequest>> triage) => _triage = triage;

    /// <summary>Acknowledge every SELECTED open finding (WP5e / ADR-028): emits one
    /// <c>[ack]</c> triage request per selected Open row through the shell seam (the gate appends the
    /// tagged ignore entries → RulesetApplied re-threads → the rows re-project as Acknowledged + the
    /// findings drop from the live health report). Already-triaged selected rows are skipped (no-op).</summary>
    public void AcknowledgeSelected(IEnumerable<AuditFindingRowModel> findings) =>
        TriageRows(findings.Where(r => r.IsSelected).ToList(), TriageKind.Acknowledge);

    /// <summary>Suppress every SELECTED open finding (WP5e / ADR-028): the <c>[suppress]</c>
    /// twin of <see cref="AcknowledgeSelected"/> — equal engine strength, different note tag.</summary>
    public void SuppressSelected(IEnumerable<AuditFindingRowModel> findings) =>
        TriageRows(findings.Where(r => r.IsSelected).ToList(), TriageKind.Suppress);

    /// <summary>Reverse triage on every SELECTED triaged finding (WP5e / ADR-028): emits one
    /// Un-triage request per selected Acknowledged/Suppressed row (the gate removes the matching
    /// tagged ignore entry → the finding reappears as Open + re-enters the live report). Open rows
    /// are skipped.</summary>
    public void UntriageSelected(IEnumerable<AuditFindingRowModel> findings)
    {
        var requests = findings
            .Where(r => r.IsSelected && r.IsTriaged)
            .Select(r => new TriageRequest(TriageEntry.Escape(r.PrimaryDn), r.RuleId, TriageKind.Untriage, null))
            .ToList();
        Submit(requests);
    }

    /// <summary>Acknowledge a single open finding (WP5e): the per-row affordance equivalent of
    /// <see cref="AcknowledgeSelected"/> for <paramref name="row"/>.</summary>
    public void AcknowledgeRow(AuditFindingRowModel row) => TriageRows([row], TriageKind.Acknowledge);

    /// <summary>Suppress a single open finding (WP5e): the per-row equivalent of
    /// <see cref="SuppressSelected"/> for <paramref name="row"/>.</summary>
    public void SuppressRow(AuditFindingRowModel row) => TriageRows([row], TriageKind.Suppress);

    /// <summary>Reverse triage on a single triaged finding (WP5e): the per-row equivalent of
    /// <see cref="UntriageSelected"/> for <paramref name="row"/>.</summary>
    public void UntriageRow(AuditFindingRowModel row)
    {
        if (row.IsTriaged)
        {
            Submit([new TriageRequest(TriageEntry.Escape(row.PrimaryDn), row.RuleId, TriageKind.Untriage, null)]);
        }
    }

    /// <summary>Builds an Ack/Suppress batch over the OPEN rows in <paramref name="rows"/> (triaged
    /// rows are skipped — re-tagging an already-ignored finding is a no-op) and submits it through
    /// the shell seam. The DN is glob-escaped so a single-object ignore stays exact.</summary>
    private void TriageRows(IReadOnlyList<AuditFindingRowModel> rows, TriageKind kind)
    {
        var requests = rows
            .Where(r => !r.IsTriaged)
            .Select(r => new TriageRequest(TriageEntry.Escape(r.PrimaryDn), r.RuleId, kind, null))
            .ToList();
        Submit(requests);
    }

    /// <summary>Hands a non-empty triage batch to the shell seam (WP5e). The shell owns the gate; a
    /// null seam (un-armed / headless) or empty batch is a no-op — never a parallel write path.</summary>
    private void Submit(IReadOnlyList<TriageRequest> requests)
    {
        if (requests.Count > 0)
        {
            _triage?.Invoke(requests);
        }
    }
}
