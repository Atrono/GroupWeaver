namespace GroupWeaver.Providers;

/// <summary>
/// Validates the two free-text connection-target inputs surfaced by ADR-031's
/// Advanced disclosure (server/DC host + base DN) BEFORE they reach
/// <see cref="LdapProvider"/> and <see cref="AdsPath.For"/>. This is the one new
/// attack surface the targeting feature introduces; the defenses pinned here are:
/// <list type="bullet">
/// <item><b>server</b> is a bare host/IP only — no embedded scheme/path
/// (<c>LDAP://</c>, <c>//</c>, <c>\</c>), no slashes, no LDAP-filter metacharacters,
/// so nothing can inject a path/scheme into <c>LDAP://{server}/{dn}</c> and redirect
/// the integrated-auth bind to an arbitrary host (issue #16's serverless-bind
/// concern, now reachable from the UI).</item>
/// <item><b>baseDn</b> is a well-formed DN, used ONLY as a search base — it is never
/// concatenated into an LDAP filter (the object-class filters stay fixed string
/// literals), so it is not a filter-injection vector.</item>
/// </list>
/// Both inputs are OPTIONAL: blank/whitespace = "not targeting", which keeps the
/// zero-config serverless default. The validator is static, pure and UI-free; the
/// VM connect command and the provider share it.
/// </summary>
public static class ConnectionTarget
{
    /// <summary>
    /// Validates a server/DC host entry. <c>null</c>/whitespace is the serverless
    /// default and is accepted (returns <see cref="ConnectionTargetResult.Ok"/> with a
    /// <c>null</c> value). A non-blank value must be a bare host or IP: no
    /// scheme/path injectors and no characters that are illegal in a DNS host or that
    /// carry meaning to ADSI/LDAP. On success the value is trimmed.
    /// </summary>
    public static ConnectionTargetResult ValidateServer(string? server)
    {
        if (string.IsNullOrWhiteSpace(server))
        {
            return ConnectionTargetResult.Ok(null);
        }

        var trimmed = server.Trim();
        foreach (char c in trimmed)
        {
            // A host label is letters/digits/hyphen/dot (and ':' for an optional port,
            // '[' ']' for an IPv6 literal). Anything else — slashes, backslashes, the
            // LDAP-filter metacharacters ( ) * \ = , ; and whitespace — is rejected so
            // the value can never terminate the host segment of LDAP://{server}/{dn}
            // or inject scheme/path/filter syntax.
            bool allowed = char.IsAsciiLetterOrDigit(c)
                || c is '-' or '.' or ':' or '[' or ']';
            if (!allowed)
            {
                return ConnectionTargetResult.Error(
                    "Server must be a bare host name or IP (no 'LDAP://', slashes, or other punctuation).");
            }
        }

        return ConnectionTargetResult.Ok(trimmed);
    }

    /// <summary>
    /// Validates a base-DN entry. <c>null</c>/whitespace means "read
    /// <c>defaultNamingContext</c> from RootDSE" and is accepted (returns a
    /// <c>null</c> value). A non-blank value must be a well-formed DN: at least one
    /// RDN of the shape <c>attr=value</c> with a non-empty attribute type and value,
    /// RDN-separated by UNESCAPED commas (RFC 4514). On success the value is trimmed
    /// and stored as-given (DNs are never canonicalized — data-model rule). The DN is
    /// used ONLY as a search base, never composed into an LDAP filter.
    /// </summary>
    public static ConnectionTargetResult ValidateBaseDn(string? baseDn)
    {
        if (string.IsNullOrWhiteSpace(baseDn))
        {
            return ConnectionTargetResult.Ok(null);
        }

        var trimmed = baseDn.Trim();
        return IsWellFormedDn(trimmed)
            ? ConnectionTargetResult.Ok(trimmed)
            : ConnectionTargetResult.Error(
                "Base DN is not a well-formed distinguished name (e.g. 'OU=Groups,DC=corp,DC=example').");
    }

    /// <summary>
    /// A well-formed DN is one-or-more comma-separated RDNs (unescaped commas only),
    /// each <c>type=value</c> with a non-empty attribute type that starts with a
    /// letter and a non-empty value. Deliberately conservative — it is a search-base
    /// gate, not a full RFC 4514 parser; it only needs to reject garbage before the
    /// bind so an unresolvable/garbage base never silently degrades.
    /// </summary>
    private static bool IsWellFormedDn(string dn)
    {
        foreach (string rdn in SplitUnescapedCommas(dn))
        {
            int eq = IndexOfUnescapedEquals(rdn);
            if (eq <= 0 || eq == rdn.Length - 1)
            {
                return false; // no '=', empty type, or empty value
            }

            string type = rdn[..eq].Trim();
            if (type.Length == 0 || !char.IsAsciiLetter(type[0]))
            {
                return false;
            }

            foreach (char c in type)
            {
                if (!char.IsAsciiLetterOrDigit(c) && c != '-')
                {
                    return false; // attribute type is letters/digits/hyphen (e.g. OU, DC, CN)
                }
            }
        }

        return true;
    }

    private static IEnumerable<string> SplitUnescapedCommas(string dn)
    {
        int start = 0;
        for (int i = 0; i < dn.Length; i++)
        {
            switch (dn[i])
            {
                case '\\':
                    i++; // backslash escapes the next char (RFC 4514), even another backslash
                    break;
                case ',':
                    yield return dn[start..i];
                    start = i + 1;
                    break;
            }
        }

        yield return dn[start..];
    }

    private static int IndexOfUnescapedEquals(string rdn)
    {
        for (int i = 0; i < rdn.Length; i++)
        {
            switch (rdn[i])
            {
                case '\\':
                    i++;
                    break;
                case '=':
                    return i;
            }
        }

        return -1;
    }
}

/// <summary>
/// The result of a <see cref="ConnectionTarget"/> validation: either ok (with the
/// trimmed value, <c>null</c> for "use the default") or an error with an
/// auditor-readable, inline-displayable message (ADR-003 D7 inline-error policy).
/// </summary>
public readonly record struct ConnectionTargetResult(bool IsValid, string? Value, string? ErrorMessage)
{
    public static ConnectionTargetResult Ok(string? value) => new(true, value, null);

    public static ConnectionTargetResult Error(string message) => new(false, null, message);
}
