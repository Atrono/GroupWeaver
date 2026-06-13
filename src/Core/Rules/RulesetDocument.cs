using System.Text.Json.Serialization;

namespace GroupWeaver.Core.Rules;

/// <summary>
/// Raw deserialization shape of a ruleset file (ADR-008). Everything is
/// nullable and string-typed on purpose: phase 1 (syntax) only binds JSON,
/// phase 2 (<see cref="RulesetLoader"/>) converts tokens and collects ALL
/// semantic errors with JSON paths in one pass. Internal — exercised through
/// the public loader surface.
/// </summary>
internal sealed class RulesetDocument
{
    /// <summary>The single tolerated extra property (editor tooling).</summary>
    [JsonPropertyName("$schema")]
    public string? Schema { get; set; }

    public int? SchemaVersion { get; set; }

    public string? Name { get; set; }

    public string? Description { get; set; }

    public string? Author { get; set; }

    public NestingDocument? Nesting { get; set; }

    public List<NamingRuleDocument>? Naming { get; set; }

    public SimpleRuleDocument? Circular { get; set; }

    public SimpleRuleDocument? EmptyGroup { get; set; }

    public List<MatchEntryDocument>? Ignore { get; set; }
}

/// <summary>Raw shape of the <c>nesting</c> section.</summary>
internal sealed class NestingDocument
{
    public bool? Enabled { get; set; }

    public string? Severity { get; set; }

    public string? Unlisted { get; set; }

    /// <summary>Rows keyed by parent kind name, cells keyed by member kind
    /// name. Dictionary KEYS stay verbatim (exact-case kind names); only
    /// property names are case-insensitive.</summary>
    public Dictionary<string, Dictionary<string, string>>? Matrix { get; set; }

    public List<MatchEntryDocument>? Exceptions { get; set; }
}

/// <summary>Raw shape of one <c>naming</c> rule.</summary>
internal sealed class NamingRuleDocument
{
    public string? Id { get; set; }

    public bool? Enabled { get; set; }

    public string? Severity { get; set; }

    public string? Kind { get; set; }

    public string? Pattern { get; set; }

    public string? Description { get; set; }

    public List<MatchEntryDocument>? Exceptions { get; set; }
}

/// <summary>Raw shape of the <c>circular</c> / <c>emptyGroup</c> sections.</summary>
internal sealed class SimpleRuleDocument
{
    public bool? Enabled { get; set; }

    public string? Severity { get; set; }

    public List<MatchEntryDocument>? Exceptions { get; set; }
}

/// <summary>Raw shape of one ignore/exception entry.</summary>
internal sealed class MatchEntryDocument
{
    public string? Dn { get; set; }

    public string? Name { get; set; }

    public string? Note { get; set; }

    public string? Endpoint { get; set; }
}
