using System;
using System.Security.Cryptography;
using System.Text;

namespace GroupWeaver.App.Tests.Diagnostics;

/// <summary>
/// Computes the EXPECTED ADR-037 D9 tokens independently of the implementation under test:
/// <c>&lt;prefix&gt;#&lt;first 8 lowercase hex of SHA-256(salt ‖ UTF-8(value))&gt;</c>. This is the
/// pinned token FORMULA (WP10 #249) — the salt bytes are prepended verbatim, the value is
/// hashed as its raw UTF-8 bytes (as-given, never canonicalized — the [[data-model]] DN
/// discipline), and the token is the first 4 hash bytes as lowercase hex. Duplicating the
/// formula here (instead of calling the redactor) is what makes the token tests non-circular.
/// </summary>
internal static class RedactionTokens
{
    internal static string Dn(byte[] salt, string value) => Token("dn", salt, value);

    internal static string Host(byte[] salt, string value) => Token("host", salt, value);

    internal static string Path(byte[] salt, string value) => Token("path", salt, value);

    internal static string Run(byte[] salt, string value) => Token("run", salt, value);

    private static string Token(string prefix, byte[] salt, string value)
    {
        var payload = new byte[salt.Length + Encoding.UTF8.GetByteCount(value)];
        salt.CopyTo(payload, 0);
        Encoding.UTF8.GetBytes(value, 0, value.Length, payload, salt.Length);
        return $"{prefix}#{Convert.ToHexString(SHA256.HashData(payload), 0, 4).ToLowerInvariant()}";
    }
}
