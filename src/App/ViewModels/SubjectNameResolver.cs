using GroupWeaver.Core.Model;

namespace GroupWeaver.App.ViewModels;

/// <summary>
/// THE one App-side finding-subject name resolution (ADR-010 §5): a finding's anchor DN →
/// the in-snapshot object's <c>Name</c>, falling back to the DN itself when the DN is absent
/// from the snapshot (raw-External / unloaded anchors). SNAPSHOT-ONLY — never a provider call,
/// so it stays read-only toward AD (no GetObjectAsync). Shared by the violations sidebar
/// (<see cref="WorkspaceViewModel"/>), the report exporter closure, and the audit findings
/// table (<see cref="AuditViewModel"/>) so the three surfaces resolve identically.
/// </summary>
internal static class SubjectNameResolver
{
    /// <summary>Resolves <paramref name="dn"/> to its snapshot object <c>Name</c>, or the DN
    /// itself when absent (a <see langword="null"/> snapshot also falls back to the DN).</summary>
    public static string Resolve(DirectorySnapshot? snapshot, string dn) =>
        snapshot is not null && snapshot.TryGetObject(dn, out var obj) ? obj!.Name : dn;
}
