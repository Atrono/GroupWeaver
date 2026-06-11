namespace GroupWeaver.Providers;

/// <summary>How an LDAP COM failure maps onto the provider error model
/// (data-model contract: unresolvable is a value, unreachable throws).</summary>
internal enum LdapErrorKind
{
    /// <summary>Directory unreachable / bind failure → throw
    /// <c>DirectoryUnavailableException</c>.</summary>
    Unavailable,

    /// <summary>The requested object does not exist → a value
    /// (<c>null</c> / External / empty list), never an exception.</summary>
    NotFound,
}

/// <summary>Classifies <c>COMException</c> HRESULTs from ADSI/LDAP calls.</summary>
internal static class LdapErrors
{
    /// <summary>
    /// Maps an HRESULT to <see cref="LdapErrorKind"/>. Only the two
    /// "object genuinely absent" codes — 0x80072030 (ERROR_DS_NO_SUCH_OBJECT)
    /// and 0x80005000 (E_ADS_BAD_PATHNAME) — classify as
    /// <see cref="LdapErrorKind.NotFound"/>. Known connectivity/bind failures
    /// (0x8007203A server not operational, 0x8007052E logon failure,
    /// 0x800704CF network unreachable, 0x8007054B no such domain) and EVERY
    /// unknown HRESULT classify as <see cref="LdapErrorKind.Unavailable"/>:
    /// an unrecognized error must never silently become "object absent" —
    /// that would fake data.
    /// </summary>
    public static LdapErrorKind ClassifyHResult(int hresult) => unchecked((uint)hresult) switch
    {
        0x80072030 => LdapErrorKind.NotFound, // ERROR_DS_NO_SUCH_OBJECT
        0x80005000 => LdapErrorKind.NotFound, // E_ADS_BAD_PATHNAME
        _ => LdapErrorKind.Unavailable,
    };
}
