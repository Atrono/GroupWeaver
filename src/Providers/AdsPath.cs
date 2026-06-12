namespace GroupWeaver.Providers;

/// <summary>
/// Builds ADsPaths (<c>LDAP://…</c>) from DNs. ADSI treats '/' as a path
/// separator, yet '/' is legal inside an RDN and the directory returns it
/// unescaped (RFC 2253 escaping never covers '/') — ADsPath additionally
/// requires it escaped as <c>\/</c>. Raw interpolation silently degrades such
/// objects to not-found, and under serverless binding lets a crafted base DN
/// like <c>evilhost/DC=x</c> terminate the host segment and redirect the
/// integrated-auth bind to an arbitrary host; escaping neutralizes both
/// (issue #16).
/// </summary>
public static class AdsPath
{
    /// <summary>
    /// ADsPath for <paramref name="dn"/>: <c>LDAP://{dn}</c> under serverless
    /// binding (<paramref name="server"/> is <c>null</c>), else
    /// <c>LDAP://{server}/{dn}</c>; the DN goes through <see cref="EscapeDn"/>
    /// either way. The server is operator configuration, never directory data,
    /// and is interpolated as-is.
    /// </summary>
    public static string For(string? server, string dn) =>
        server is null ? $"LDAP://{EscapeDn(dn)}" : $"LDAP://{server}/{EscapeDn(dn)}";

    /// <summary>
    /// Escapes every '/' in <paramref name="dn"/> as <c>\/</c> for use inside an
    /// ADsPath. Input contract: a DN exactly as the directory returns it — DNs
    /// are stored and passed as-given, never canonicalized (data-model rule). A
    /// caller passing a pre-ADsPath-escaped DN (<c>\/</c>) is out of contract:
    /// it gets double-escaped and resolves to <c>null</c>/External per the
    /// provider error model. Outright rejection of scheme-ish input
    /// (<c>ldap://…</c>) was considered (issue #16) and is not needed: after
    /// escaping, such a string is just an unresolvable DN and comes back as a
    /// value, consistent with "unresolvable is a value, never an exception".
    /// </summary>
    public static string EscapeDn(string dn) => dn.Replace("/", "\\/");
}
