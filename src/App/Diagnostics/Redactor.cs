using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace GroupWeaver.App.Diagnostics;

/// <summary>
/// Log-redaction seam (ADR-037 D9) — <b>WP1 identity STUB</b>: every helper returns its
/// input unchanged so WP1/WP2 call sites are written redaction-correct NOW (sensitive
/// values pass through a typed helper at the call site, never formatter-guessed) and
/// WP10 (#249) only swaps the implementation for session-salted hashes
/// (<c>dn#a1b2c3d4</c> = first 8 hex of SHA-256(<see cref="SessionSalt"/> ‖ value)) plus
/// the free-text scrubber. Until WP10 lands, log output is PLAIN — redaction gates the
/// next tagged release (ADR-037 D9), never ships without it.
/// </summary>
public static class Redactor
{
    /// <summary>The redaction mode name the <c>AppStarted</c> banner reports (ADR-037 D6).
    /// WP10 flips this to <c>"redacted"</c> (default) / <c>"plain"</c> (<c>--log-plain</c>).</summary>
    public static string Mode => "identity";

    /// <summary>Per-process salt for WP10's session-salted hashes: stable within a session so
    /// events join, unlinkable across sessions. Generated eagerly here so the WP1→WP10 swap
    /// is implementation-only.</summary>
    internal static byte[] SessionSalt { get; } = RandomNumberGenerator.GetBytes(16);

    /// <summary>Redacts a DN or DN fragment (WP10: <c>dn#&lt;hash8&gt;</c>). Identity in WP1.</summary>
    [return: NotNullIfNotNull(nameof(value))]
    public static string? Dn(string? value) => value;

    /// <summary>Redacts a server/domain/baseDn string (WP10: <c>host#&lt;hash8&gt;</c>). Identity in WP1.</summary>
    [return: NotNullIfNotNull(nameof(value))]
    public static string? Host(string? value) => value;

    /// <summary>Scrubs free text (exception / jsError messages — they embed DNs; WP10 removes
    /// <c>(CN|OU|DC)=…</c> runs + learned server/baseDn strings). Identity in WP1; every
    /// <c>msgScrubbed</c> field must pass through here.</summary>
    [return: NotNullIfNotNull(nameof(text))]
    public static string? Scrub(string? text) => text;
}
