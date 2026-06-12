namespace GroupWeaver.Core.Graph;

/// <summary>
/// Escape-aware DN ancestry helpers (ADR-004): RDNs are separated by UNESCAPED
/// commas only — <c>\,</c> inside an RDN value does not separate. All DN
/// comparisons go through <see cref="Model.Dn.Comparer"/>; DN strings are never
/// canonicalized.
/// </summary>
public static class DnPath
{
    /// <summary>The DN with its leading RDN removed; <c>null</c> when
    /// <paramref name="dn"/> consists of a single RDN.</summary>
    public static string? Parent(string dn)
    {
        var separator = IndexOfNextUnescapedComma(dn, 0);
        return separator < 0 ? null : dn[(separator + 1)..];
    }

    /// <summary>How many RDN levels <paramref name="dn"/> sits below
    /// <paramref name="baseDn"/>: 0 when equal, n for an n-level descendant,
    /// -1 when <paramref name="dn"/> is not under <paramref name="baseDn"/>.</summary>
    public static int RelativeDepth(string dn, string baseDn)
    {
        // Component-wise suffix match: a textual suffix is NOT enough (an escaped
        // comma can make a raw string end in ",OU=X,..." without OU=X being an
        // actual ancestor), so both DNs are split escape-aware first.
        var components = Split(dn);
        var baseComponents = Split(baseDn);
        var depth = components.Count - baseComponents.Count;
        if (depth < 0)
        {
            return -1;
        }

        for (var i = 0; i < baseComponents.Count; i++)
        {
            if (!Model.Dn.Comparer.Equals(components[depth + i], baseComponents[i]))
            {
                return -1;
            }
        }

        return depth;
    }

    private static List<string> Split(string dn)
    {
        var components = new List<string>();
        var start = 0;
        while (true)
        {
            var separator = IndexOfNextUnescapedComma(dn, start);
            if (separator < 0)
            {
                components.Add(dn[start..]);
                return components;
            }

            components.Add(dn[start..separator]);
            start = separator + 1;
        }
    }

    private static int IndexOfNextUnescapedComma(string dn, int start)
    {
        for (var i = start; i < dn.Length; i++)
        {
            switch (dn[i])
            {
                case '\\':
                    i++; // backslash escapes the next character (RFC 4514), even another backslash
                    break;
                case ',':
                    return i;
            }
        }

        return -1;
    }
}
