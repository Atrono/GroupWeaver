namespace GroupWeaver.Core.Rules;

/// <summary>
/// Severity of a rule finding (ADR-008). The numeric order Info &lt; Warning
/// &lt; Error is PINNED by test: AP 3.4's traffic-light roll-up takes the
/// max() over a node's findings, so reordering or renumbering silently flips
/// the roll-up. Adding a fourth severity is a schemaVersion bump.
/// </summary>
public enum RuleSeverity
{
    /// <summary>Informational: visible, not judged a problem.</summary>
    Info = 0,

    /// <summary>A convention violation worth attention.</summary>
    Warning = 1,

    /// <summary>A structural violation of the configured rules.</summary>
    Error = 2,
}
