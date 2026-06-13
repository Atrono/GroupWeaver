using System.Text.Json;
using System.Text.Json.Serialization;

using GroupWeaver.Core.Graph;
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
        Serialize(new FlatDto(MapNodes(model.Nodes, report, belowMap), MapEdges(model.Edges)));

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
        IReadOnlyDictionary<string, (int Count, RuleSeverity Sev)>? belowMap) =>
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

            return new NodeDto(
                n.Dn, n.Label, n.Kind.ToString(), n.X, n.Y,
                Root: n.IsRoot ? true : null, Sev: sev, Below: below, BelowSev: belowSev);
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

    /// <summary>The ONE edge mapping code path, shared with <see cref="GraphChunker"/>:
    /// per-kind <c>m</c>/<c>c</c> id counters in model edge order, membership flipped
    /// (s := member, t := group), containment as-is (s := container).</summary>
    internal static List<EdgeDto> MapEdges(IReadOnlyList<GraphEdge> edges)
    {
        var dtos = new List<EdgeDto>(edges.Count);
        var membershipCount = 0;
        var containmentCount = 0;
        foreach (var edge in edges)
        {
            dtos.Add(edge.Kind == GraphEdgeKind.Membership
                ? new EdgeDto($"m{membershipCount++}", S: edge.ChildDn, T: edge.ParentDn, Rel: "member")
                : new EdgeDto($"c{containmentCount++}", S: edge.ParentDn, T: edge.ChildDn, Rel: "contains"));
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
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BelowSev);

    /// <summary>Wire edge in drawn orientation.</summary>
    internal sealed record EdgeDto(string Id, string S, string T, string Rel);

    /// <summary>The flat <c>--dump-graph</c> document shape.</summary>
    private sealed record FlatDto(IReadOnlyList<NodeDto> Nodes, IReadOnlyList<EdgeDto> Edges);
}
