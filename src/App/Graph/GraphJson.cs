using System.Text.Json;
using System.Text.Json.Serialization;

using GroupWeaver.Core.Diff;
using GroupWeaver.Core.Graph;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Rules;

namespace GroupWeaver.App.Graph;

/// <summary>
/// App-side wire mapper (ADR-004 D4): <see cref="GraphModel"/> → camelCase JSON.
/// Node: <c>{"id":DN verbatim,"label","kind":enum name verbatim,"x","y"}</c> plus
/// <c>"root":true</c> on the root node only (absent otherwise); edge:
/// <c>{"id":"m0"/"c0"…,"s","t","rel":"member"|"contains"}</c> with the membership
/// orientation flipped HERE (s := member/ChildDn, t := group/ParentDn — Core keeps
/// the semantic direction). Doubles are invariant-'.' always.
/// Contract pinned by <c>tests/GroupWeaver.App.Tests/Graph/GraphJsonTests.cs</c>.
/// </summary>
public static class GraphJson
{
    /// <summary>Reflection serializer on purpose (ADR-004 D4: source-gen STJ only
    /// if trimming ever lands); the camelCase policy renames PROPERTIES only —
    /// kind values stay enum names verbatim because they are mapped to strings.</summary>
    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Serializes the whole model as one flat <c>{"nodes":[…],"edges":[…]}</c>
    /// document — the <c>--dump-graph</c> file format (ADR-004 D7). Carries no
    /// severity: delegates with <see cref="RuleReport.Empty"/> and no below-map, so
    /// the wire is byte-identical to the pre-AP-3.4 output (unflagged nodes omit
    /// every severity key).
    /// </summary>
    public static string SerializeFlat(GraphModel model) =>
        SerializeFlat(model, RuleReport.Empty, belowMap: null);

    /// <summary>
    /// Serializes the whole model with the AP 3.4 severity join (ADR-010): each
    /// node gains <c>sev</c> from <paramref name="report"/>'s
    /// <see cref="RuleReport.MaxSeverityByDn"/> and <c>below</c>/<c>belowSev</c> from
    /// the VM-computed <paramref name="belowMap"/> (roll-up count + max severity among
    /// loaded descendants). All three are omitted when absent, so unflagged nodes stay
    /// byte-identical to the no-report wire.
    /// </summary>
    public static string SerializeFlat(
        GraphModel model,
        RuleReport report,
        IReadOnlyDictionary<string, (int Count, RuleSeverity Sev)>? belowMap) =>
        SerializeFlat(model, report, belowMap, nodeDiffMap: null, edgeDiffMap: null);

    /// <summary>
    /// Serializes the whole model with the AP 3.4 severity join AND the v0.3 gap-analysis
    /// diff channel (ADR-015 Slice 4): each node/membership edge gains an omit-when-null
    /// <c>diff</c> token from <paramref name="nodeDiffMap"/> /
    /// <paramref name="edgeDiffMap"/>, forwarded into the SAME shared
    /// <see cref="MapNodes"/>/<see cref="MapEdges"/> path so flat and chunked output can
    /// never drift. Two <c>null</c> diff maps yield byte-identical output to the
    /// severity-only overload (the <c>diff</c> field is omitted), so every pre-Slice-4
    /// flat/severity test stays green.
    /// </summary>
    public static string SerializeFlat(
        GraphModel model,
        RuleReport report,
        IReadOnlyDictionary<string, (int Count, RuleSeverity Sev)>? belowMap,
        IReadOnlyDictionary<string, DiffStatus>? nodeDiffMap,
        IReadOnlyDictionary<MembershipEdge, DiffStatus>? edgeDiffMap) =>
        Serialize(new FlatDto(
            MapNodes(model.Nodes, report, belowMap, nodeDiffMap),
            MapEdges(model.Edges, edgeDiffMap)));

    /// <summary>Serializes a wire DTO with the shared options — every wire string
    /// (flat and chunked) goes through here.</summary>
    internal static string Serialize<TValue>(TValue dto) =>
        JsonSerializer.Serialize(dto, WireOptions);

    /// <summary>The ONE node mapping code path, shared with <see cref="GraphChunker"/>
    /// so chunked and flat output can never drift (ADR-010 D2). Severity is joined HERE,
    /// in the App wire mapper — never in Core/GraphBuilder: <paramref name="report"/>'s
    /// <see cref="RuleReport.MaxSeverityByDn"/> drives the per-node <c>sev</c>;
    /// <paramref name="belowMap"/> (VM-computed roll-up, the only sanctioned
    /// <c>MembershipTraversal.Walk</c> consumer) drives <c>below</c>/<c>belowSev</c>.
    /// A <c>null</c> below-map (flat dump default) yields no roll-up fields, and a
    /// count-≤0 entry is treated as absent.</summary>
    internal static List<NodeDto> MapNodes(
        IReadOnlyList<GraphNode> nodes,
        RuleReport report,
        IReadOnlyDictionary<string, (int Count, RuleSeverity Sev)>? belowMap,
        IReadOnlyDictionary<string, DiffStatus>? diffMap = null) =>
        [.. nodes.Select(n =>
        {
            var sev = report.MaxSeverityByDn.TryGetValue(n.Dn, out var s) ? SeverityWire.ToToken(s) : null;
            int? below = null;
            string? belowSev = null;
            if (belowMap is not null && belowMap.TryGetValue(n.Dn, out var roll) && roll.Count > 0)
            {
                below = roll.Count;
                belowSev = SeverityWire.ToToken(roll.Sev);
            }

            var diff = diffMap is not null && diffMap.TryGetValue(n.Dn, out var ds)
                ? DiffWire.ToToken(ds)
                : null;

            return new NodeDto(
                n.Dn, n.Label, n.Kind.ToString(), n.X, n.Y,
                Root: n.IsRoot ? true : null, Sev: sev, Below: below, BelowSev: belowSev, Diff: diff);
        })];

    /// <summary>The lowercase wire token for a <see cref="RuleSeverity"/>
    /// (<c>"error"</c>/<c>"warning"</c>/<c>"info"</c>), DECOUPLED from the enum names
    /// (ADR-010 D2) — the JS stylesheet selectors (<c>node[sev='error']</c> …) key off
    /// these tokens, so renaming the enum must not silently break the parity tripwire.</summary>
    internal static class SeverityWire
    {
        public static string ToToken(RuleSeverity severity) => severity switch
        {
            RuleSeverity.Error => "error",
            RuleSeverity.Warning => "warning",
            _ => "info",
        };
    }

    /// <summary>The lowercase wire token for a <see cref="DiffStatus"/>
    /// (<c>"added"</c>/<c>"removed"</c>/<c>"unchecked"</c>), DECOUPLED from the enum
    /// names (ADR-015 Slice 4) — the JS gap-overlay selectors key off these tokens.
    /// <see cref="DiffStatus.Common"/> maps to <c>null</c> so an unchanged element omits
    /// the <c>diff</c> key and stays byte-identical to the pre-diff wire; this mirrors
    /// <see cref="SeverityWire"/>.</summary>
    internal static class DiffWire
    {
        public static string? ToToken(DiffStatus status) => status switch
        {
            DiffStatus.Added => "added",
            DiffStatus.Removed => "removed",
            DiffStatus.Unchecked => "unchecked",
            _ => null,
        };
    }

    /// <summary>The ONE edge mapping code path, shared with <see cref="GraphChunker"/>:
    /// per-kind <c>m</c>/<c>c</c> id counters in model edge order, membership flipped
    /// (s := member, t := group), containment as-is (s := container).</summary>
    internal static List<EdgeDto> MapEdges(
        IReadOnlyList<GraphEdge> edges,
        IReadOnlyDictionary<MembershipEdge, DiffStatus>? edgeDiffMap = null)
    {
        var dtos = new List<EdgeDto>(edges.Count);
        var membershipCount = 0;
        var containmentCount = 0;
        foreach (var edge in edges)
        {
            if (edge.Kind == GraphEdgeKind.Membership)
            {
                var diff = edgeDiffMap is not null
                    && edgeDiffMap.TryGetValue(new MembershipEdge(edge.ParentDn, edge.ChildDn), out var ds)
                        ? DiffWire.ToToken(ds)
                        : null;
                dtos.Add(new EdgeDto(
                    $"m{membershipCount++}", S: edge.ChildDn, T: edge.ParentDn, Rel: "member", Diff: diff));
            }
            else
            {
                // Containment is never membership: it never reads the edge diff map, even
                // on a (parent, child) key hit (ADR-015 Slice 4).
                dtos.Add(new EdgeDto(
                    $"c{containmentCount++}", S: edge.ParentDn, T: edge.ChildDn, Rel: "contains", Diff: null));
            }
        }

        return dtos;
    }

    /// <summary>Wire node; <paramref name="Root"/> is <c>true</c> or omitted —
    /// never emitted as <c>false</c>. The AP 3.4 severity fields (ADR-010 D2) mirror
    /// the same omit-when-null discipline so an unflagged node stays byte-identical to
    /// the pre-AP wire: <paramref name="Sev"/> (own max severity, token form),
    /// <paramref name="Below"/> (distinct findings among loaded descendants, emitted
    /// only when &gt; 0) and <paramref name="BelowSev"/> (max severity among those).</summary>
    internal sealed record NodeDto(
        string Id,
        string Label,
        string Kind,
        double X,
        double Y,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Root,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Sev,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Below,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BelowSev,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Diff);

    /// <summary>Wire edge in drawn orientation. <paramref name="Diff"/> (ADR-015 Slice 4)
    /// is the omit-when-null gap-analysis token, present only on membership edges.</summary>
    internal sealed record EdgeDto(
        string Id,
        string S,
        string T,
        string Rel,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Diff);

    /// <summary>The flat <c>--dump-graph</c> document shape.</summary>
    private sealed record FlatDto(IReadOnlyList<NodeDto> Nodes, IReadOnlyList<EdgeDto> Edges);
}
