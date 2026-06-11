namespace GroupWeaver.Core.Model;

/// <summary>Classification of a directory object as GroupWeaver sees it.</summary>
public enum AdObjectKind
{
    /// <summary>A user account.</summary>
    User,

    /// <summary>A group with global scope (the "G" in AGDLP).</summary>
    GlobalGroup,

    /// <summary>A group with domain-local scope (the "DL" in AGDLP), including builtin groups.</summary>
    DomainLocalGroup,

    /// <summary>A group with universal scope (the "U" in AGUDLP).</summary>
    UniversalGroup,

    /// <summary>An organizational unit.</summary>
    OrganizationalUnit,

    /// <summary>A computer account.</summary>
    Computer,

    /// <summary>Anything outside the loaded scope or not classifiable (e.g. foreign
    /// security principals, contacts, unresolvable member DNs).</summary>
    External,
}
