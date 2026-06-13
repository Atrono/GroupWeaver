using System.Text.Json;
using System.Text.Json.Serialization;

namespace GroupWeaver.Core.Rules;

/// <summary>
/// The single serializer choke point for ruleset files (ADR-008; reflection
/// serializer on purpose, per the ADR-004 D4 precedent). JSONC in: comments
/// and trailing commas are legal, property-NAME case is tolerated, unknown
/// properties are rejected so typos fail loudly with positional info (the
/// sole tolerated extra, top-level <c>$schema</c>, is an explicitly mapped
/// member of <see cref="RulesetDocument"/>).
/// </summary>
internal static class RulesetJson
{
    internal static readonly JsonSerializerOptions ReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>Strict camelCase JSON out, nulls omitted. PROPERTY names only:
    /// matrix dictionary KEYS stay verbatim PascalCase kind names (no
    /// <c>DictionaryKeyPolicy</c> on purpose — the loader parses them exact-case).</summary>
    internal static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
