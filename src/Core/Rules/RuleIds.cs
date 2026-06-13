namespace GroupWeaver.Core.Rules;

/// <summary>
/// The fixed rule ids (ADR-008). These strings appear in user-edited ruleset
/// files, AP 3.2 findings, and the AP 3.3 settings UI — changing one breaks
/// every shared community ruleset, so they are pinned verbatim by test.
/// Naming rule ids are user-defined per rule, unique case-insensitively.
/// </summary>
public static class RuleIds
{
    /// <summary>The nesting-matrix rule.</summary>
    public const string Nesting = "nesting";

    /// <summary>The circular-membership rule.</summary>
    public const string Circular = "circular";

    /// <summary>The empty-group rule.</summary>
    public const string EmptyGroup = "empty-group";
}
