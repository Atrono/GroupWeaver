using GroupWeaver.Core.Model;

namespace GroupWeaver.Core.Plan;

/// <summary>
/// The object kinds a plan can author (ADR-014). Plan Mode creates accounts and the
/// three security-group scopes that make up AGUDLP; it never authors OUs, computers,
/// or external principals (those are not things an operator scripts into existence as
/// part of a proposed group structure).
/// </summary>
public enum PlanCreatableKind
{
    /// <summary>A user account (the "A" in AGUDLP).</summary>
    User,

    /// <summary>A global-scoped security group (the "G").</summary>
    GlobalGroup,

    /// <summary>A domain-local-scoped security group (the "DL").</summary>
    DomainLocalGroup,

    /// <summary>A universal-scoped security group (the "U").</summary>
    UniversalGroup,
}

/// <summary>
/// Maps a <see cref="PlanCreatableKind"/> onto the engine's <see cref="AdObjectKind"/>
/// (so the projection reuses the unchanged <c>RuleEngine</c>/<c>GraphBuilder</c>) and
/// answers the only structural question the model asks: can this kind have members?
/// </summary>
public static class PlanKindMap
{
    /// <summary>The engine kind a projected plan object carries.</summary>
    public static AdObjectKind ToAdObjectKind(PlanCreatableKind kind) => kind switch
    {
        PlanCreatableKind.GlobalGroup => AdObjectKind.GlobalGroup,
        PlanCreatableKind.DomainLocalGroup => AdObjectKind.DomainLocalGroup,
        PlanCreatableKind.UniversalGroup => AdObjectKind.UniversalGroup,
        _ => AdObjectKind.User,
    };

    /// <summary>Whether the kind is a group (only a group can have members).</summary>
    public static bool IsGroup(PlanCreatableKind kind) => kind != PlanCreatableKind.User;
}
