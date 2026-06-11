namespace GroupWeaver.Core.Model;

/// <summary>Maps raw directory classification data to an <see cref="AdObjectKind"/>.</summary>
public static class AdObjectKindMapper
{
    private const int BuiltinFlag = 0x1;
    private const int GlobalFlag = 0x2;
    private const int DomainLocalFlag = 0x4;
    private const int UniversalFlag = 0x8;

    /// <summary>
    /// Classifies an object from its <c>objectClass</c> chain and (for groups) its
    /// <c>groupType</c>. Comparison is case-insensitive and the most specific class
    /// wins: a chain containing <c>computer</c> is a Computer even though it also
    /// contains <c>user</c>. Anything unrecognized — including
    /// <c>foreignSecurityPrincipal</c> and e.g. <c>top</c>+<c>contact</c> — maps to
    /// <see cref="AdObjectKind.External"/>.
    /// </summary>
    /// <param name="objectClasses">The object's full <c>objectClass</c> value chain.</param>
    /// <param name="groupType">The <c>groupType</c> attribute, if present.</param>
    public static AdObjectKind Map(IReadOnlyCollection<string> objectClasses, int? groupType)
    {
        if (ContainsClass(objectClasses, "computer"))
        {
            return AdObjectKind.Computer;
        }

        if (ContainsClass(objectClasses, "user"))
        {
            return AdObjectKind.User;
        }

        if (ContainsClass(objectClasses, "organizationalUnit"))
        {
            return AdObjectKind.OrganizationalUnit;
        }

        if (ContainsClass(objectClasses, "group"))
        {
            return MapGroupScope(groupType);
        }

        return AdObjectKind.External;
    }

    /// <summary>
    /// Scope from the groupType bits. The builtin flag (0x1) forces DomainLocalGroup;
    /// the security bit (0x80000000) is deliberately ignored — a distribution global
    /// group is simply a GlobalGroup. Null or meaningless values map to External.
    /// </summary>
    private static AdObjectKind MapGroupScope(int? groupType)
    {
        if (groupType is not int type)
        {
            return AdObjectKind.External;
        }

        if ((type & BuiltinFlag) != 0)
        {
            return AdObjectKind.DomainLocalGroup;
        }

        if ((type & GlobalFlag) != 0)
        {
            return AdObjectKind.GlobalGroup;
        }

        if ((type & DomainLocalFlag) != 0)
        {
            return AdObjectKind.DomainLocalGroup;
        }

        if ((type & UniversalFlag) != 0)
        {
            return AdObjectKind.UniversalGroup;
        }

        return AdObjectKind.External;
    }

    private static bool ContainsClass(IReadOnlyCollection<string> objectClasses, string name) =>
        objectClasses.Contains(name, StringComparer.OrdinalIgnoreCase);
}
