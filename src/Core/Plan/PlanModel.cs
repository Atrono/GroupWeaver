using System.Diagnostics.CodeAnalysis;

using GroupWeaver.Core.Model;

namespace GroupWeaver.Core.Plan;

/// <summary>
/// The mutable, DN-keyed authoring store for Plan Mode (ADR-014) — the edit surface the
/// read-only <see cref="DirectorySnapshot"/> deliberately is not (the snapshot is
/// append-only: no single-edge op, no removal, <c>SetMembers</c> replaces a whole list).
/// The DN is the sole identity; every node/edge collection keys via <see cref="Dn.Comparer"/>
/// and edges reuse <see cref="MembershipEdge"/> verbatim (direction-sensitive,
/// case-insensitive). A node's DN is <c>CN=&lt;rfc4514-escaped name&gt;,&lt;BaseOuDn&gt;</c>,
/// stored as-formed and never re-canonicalized.
///
/// AUDIT CORRECTION (binding, ADR-014): self-membership A→A and cycles A→B→A are
/// AUTHORABLE — they are findings the engine reports, not structural errors, so the
/// model never rejects them. Only an unknown endpoint and a non-group parent are
/// rejected at the model boundary. Not thread-safe (mirrors the snapshot contract).
/// </summary>
public sealed class PlanModel
{
    private readonly Dictionary<string, PlanObject> _nodes = new(Dn.Comparer);
    private readonly HashSet<MembershipEdge> _edges = new();

    /// <summary>Creates an empty plan rooted at <paramref name="baseOuDn"/>.</summary>
    public PlanModel(string baseOuDn) => BaseOuDn = baseOuDn;

    /// <summary>The base OU every authored object is formed under.</summary>
    public string BaseOuDn { get; }

    /// <summary>All authored objects.</summary>
    public IReadOnlyCollection<PlanObject> Nodes => _nodes.Values;

    /// <summary>All authored membership edges (direction-sensitive, de-duplicated).</summary>
    public IReadOnlyCollection<MembershipEdge> Edges => _edges;

    /// <summary>The single DN-formation rule: <c>CN=&lt;escaped name&gt;,&lt;BaseOuDn&gt;</c>.</summary>
    public string FormDn(string name) => $"CN={Rfc4514.EscapeRdnValue(name)},{BaseOuDn}";

    /// <summary>Looks up an authored object by DN, case-insensitively.</summary>
    public bool TryGetNode(string dn, [MaybeNullWhen(false)] out PlanObject node) =>
        _nodes.TryGetValue(dn, out node);

    /// <summary>
    /// Authors a new object. The DN is formed from <paramref name="name"/>; a
    /// case-insensitive duplicate DN is rejected. Control characters in the name or SAM
    /// are rejected here (defense-in-depth for the exporter), since the formed DN and
    /// the emitted script must stay clean.
    /// </summary>
    public PlanObject AddNode(PlanCreatableKind kind, string name, string? sam = null)
    {
        if (PlanText.ContainsUnsafe(name) || (sam is not null && PlanText.ContainsUnsafe(sam)))
        {
            throw new PlanConflictException(
                "A name carries a character (a control character, line separator, or curly quote) "
                + "that is unsafe to embed in the exported script and must not be used.");
        }

        var dn = FormDn(name);
        if (_nodes.ContainsKey(dn))
        {
            throw new PlanConflictException(
                $"An object named '{name}' already exists under the base OU.");
        }

        var node = new PlanObject { Dn = dn, Kind = kind, Name = name, SamAccountName = sam };
        _nodes[dn] = node;
        return node;
    }

    /// <summary>
    /// Removes the object with <paramref name="dn"/> and cascades every incident edge
    /// (in either direction) — the capability the append-only snapshot lacks. Returns
    /// <c>false</c> (no throw) when the DN is not authored.
    /// </summary>
    public bool RemoveNode(string dn)
    {
        if (!_nodes.Remove(dn))
        {
            return false;
        }

        _edges.RemoveWhere(e => IsIncident(e, dn));
        return true;
    }

    /// <summary>
    /// Renames the object to <paramref name="newName"/>: replace-by-DN (the DN is
    /// identity, so this is not an in-place mutation). The new DN is formed, every
    /// incident edge has its matching endpoint rewritten, and the old DN is dropped.
    /// Rejects an unknown DN and a rename onto an existing (different) DN.
    /// </summary>
    public void RenameNode(string dn, string newName)
    {
        if (PlanText.ContainsUnsafe(newName))
        {
            throw new PlanConflictException(
                "A name carries a character (a control character, line separator, or curly quote) "
                + "that is unsafe to embed in the exported script and must not be used.");
        }

        if (!_nodes.TryGetValue(dn, out var node))
        {
            throw new PlanConflictException("No such object to rename.");
        }

        var newDn = FormDn(newName);
        if (!Dn.Comparer.Equals(newDn, dn) && _nodes.ContainsKey(newDn))
        {
            throw new PlanConflictException($"An object named '{newName}' already exists.");
        }

        var incident = _edges.Where(e => IsIncident(e, dn)).ToList();
        _nodes.Remove(dn);
        _edges.RemoveWhere(e => IsIncident(e, dn));

        _nodes[newDn] = new PlanObject
        {
            Dn = newDn,
            Kind = node.Kind,
            Name = newName,
            SamAccountName = node.SamAccountName,
        };

        foreach (var e in incident)
        {
            var parent = Dn.Comparer.Equals(e.ParentDn, dn) ? newDn : e.ParentDn;
            var child = Dn.Comparer.Equals(e.ChildDn, dn) ? newDn : e.ChildDn;
            _edges.Add(new MembershipEdge(parent, child));
        }
    }

    /// <summary>Mutates the typed kind of an authored object in place (no-op when
    /// the DN is unknown).</summary>
    public void SetKind(string dn, PlanCreatableKind kind)
    {
        if (_nodes.TryGetValue(dn, out var node))
        {
            node.Kind = kind;
        }
    }

    /// <summary>
    /// Authors a directed membership (<paramref name="parentDn"/> lists
    /// <paramref name="childDn"/>). Idempotent (the edge set de-duplicates). Rejects an
    /// endpoint that is not an authored object and a non-group parent. Does NOT reject
    /// A→A or a cycle — those are authorable findings. Returns whether a new edge was
    /// added.
    /// </summary>
    public bool AddEdge(string parentDn, string childDn)
    {
        if (!_nodes.TryGetValue(parentDn, out var parent) || !_nodes.ContainsKey(childDn))
        {
            throw new PlanConflictException(
                "Both endpoints of a membership must be authored objects.");
        }

        if (!PlanKindMap.IsGroup(parent.Kind))
        {
            throw new PlanConflictException("Only a group can have members.");
        }

        return _edges.Add(new MembershipEdge(parentDn, childDn));
    }

    /// <summary>Removes a directed membership; returns whether it was present.</summary>
    public bool RemoveEdge(string parentDn, string childDn) =>
        _edges.Remove(new MembershipEdge(parentDn, childDn));

    /// <summary>The child DNs of <paramref name="parentDn"/>'s out-edges, in stored order.</summary>
    public IEnumerable<string> ChildrenOf(string parentDn) =>
        _edges.Where(e => Dn.Comparer.Equals(e.ParentDn, parentDn)).Select(e => e.ChildDn);

    private static bool IsIncident(MembershipEdge edge, string dn) =>
        Dn.Comparer.Equals(edge.ParentDn, dn) || Dn.Comparer.Equals(edge.ChildDn, dn);
}
