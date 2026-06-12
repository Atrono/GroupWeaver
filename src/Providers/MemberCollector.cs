namespace GroupWeaver.Providers;

/// <summary>
/// Drives LDAP ranged retrieval of the <c>member</c> attribute: AD caps a single
/// read at 1500 values and signals the cap by returning the values under a
/// <c>member;range=&lt;low&gt;-&lt;high&gt;</c> key instead of <c>member</c>.
/// Pure — the actual LDAP round trip is injected.
/// </summary>
internal static class MemberCollector
{
    private const string MemberAttribute = "member";
    private const int MaxFetches = 10_000;

    /// <summary>
    /// Collects the complete member DN list of one group.
    /// <para>Semantics (binding):</para>
    /// <list type="bullet">
    /// <item><paramref name="initialProperties"/> is scanned case-insensitively. A
    /// plain <c>member</c> key wins and its values are returned as-is (it is the
    /// complete list); otherwise the first key that parses via
    /// <see cref="MemberRangeParser.TryParse"/> seeds the ranged loop. Neither
    /// present → empty list (LDAP omits empty attributes entirely).</item>
    /// <item>The loop requests
    /// <see cref="MemberRangeParser.NextRangeAttribute"/>(<c>high + 1</c>) via
    /// <paramref name="fetchRange"/>, appends the returned values, and continues
    /// with the returned attribute's high bound until the returned range ends in
    /// <c>*</c> (parsed end is <c>null</c>) — the normal termination. Defensive
    /// terminations (after appending): an empty value list, an exactly-<c>member</c>
    /// returned name, or a returned name that does not parse as a member range.</item>
    /// <item>Order is preserved; duplicates are tolerated
    /// (<c>DirectorySnapshot.SetMembers</c> de-duplicates).</item>
    /// <item>More than 10000 fetches → <see cref="InvalidDataException"/>
    /// (hostile or broken server; never loop forever).</item>
    /// </list>
    /// </summary>
    /// <param name="dn">The group DN, for error reporting only.</param>
    /// <param name="initialProperties">Attribute name → values of the initial search result.</param>
    /// <param name="fetchRange">Performs one follow-up read: takes the attribute name to
    /// request, returns the attribute name the server actually answered with plus its values.</param>
    public static IReadOnlyList<string> CollectAllMembers(
        string dn,
        IReadOnlyDictionary<string, IReadOnlyList<string>> initialProperties,
        Func<string, (string ReturnedAttribute, IReadOnlyList<string> Values)> fetchRange)
    {
        string? rangedKey = null;
        int? rangedEnd = null;
        foreach ((string name, IReadOnlyList<string> values) in initialProperties)
        {
            if (string.Equals(name, MemberAttribute, StringComparison.OrdinalIgnoreCase))
            {
                return values;
            }

            if (rangedKey is null && MemberRangeParser.TryParse(name, out _, out int? end))
            {
                rangedKey = name;
                rangedEnd = end;
            }
        }

        if (rangedKey is null)
        {
            return [];
        }

        var members = new List<string>(initialProperties[rangedKey]);
        int? high = rangedEnd;
        for (int fetches = 0; high is int lastHigh; fetches++)
        {
            if (fetches >= MaxFetches)
            {
                throw new InvalidDataException(
                    $"ranged member retrieval for '{dn}' did not terminate after {MaxFetches} fetches.");
            }

            (string returned, IReadOnlyList<string> values) =
                fetchRange(MemberRangeParser.NextRangeAttribute(lastHigh + 1));
            members.AddRange(values);

            if (values.Count == 0 ||
                string.Equals(returned, MemberAttribute, StringComparison.OrdinalIgnoreCase) ||
                !MemberRangeParser.TryParse(returned, out _, out int? nextEnd))
            {
                break;
            }

            high = nextEnd; // null = range ends in '*': complete, loop condition exits
        }

        return members;
    }
}
