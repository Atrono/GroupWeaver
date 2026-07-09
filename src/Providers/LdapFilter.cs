namespace GroupWeaver.Providers;

/// <summary>
/// Escapes a literal value for safe interpolation into an LDAP search filter
/// (RFC 4515 §3, ADR-039 D3). Every filter this provider builds from
/// directory-derived data (member DNs, batched resolution) MUST pass every
/// interpolated value through <see cref="Escape"/> — an unescaped <c>*</c>,
/// <c>(</c>, <c>)</c>, or <c>\</c> in a DN would let a maliciously named object
/// widen or terminate the filter early (LDAP filter injection). This is a
/// distinct escaping domain from <see cref="AdsPath.EscapeDn"/> (which escapes
/// <c>/</c> for ADsPath syntax, not filter syntax) — do not conflate the two.
/// </summary>
internal static class LdapFilter
{
    /// <summary>
    /// Replaces each RFC 4515 filter metacharacter with its two-digit hex
    /// escape: <c>\</c> → <c>\5c</c>, <c>*</c> → <c>\2a</c>, <c>(</c> → <c>\28</c>,
    /// <c>)</c> → <c>\29</c>, NUL → <c>\00</c>. <c>\</c> is replaced first so the
    /// escape sequences it introduces are never themselves re-escaped.
    /// </summary>
    public static string Escape(string value) =>
        value
            .Replace("\\", "\\5c", StringComparison.Ordinal)
            .Replace("*", "\\2a", StringComparison.Ordinal)
            .Replace("(", "\\28", StringComparison.Ordinal)
            .Replace(")", "\\29", StringComparison.Ordinal)
            .Replace("\0", "\\00", StringComparison.Ordinal);
}
