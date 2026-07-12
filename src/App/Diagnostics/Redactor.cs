using System.Diagnostics.CodeAnalysis;

namespace GroupWeaver.App.Diagnostics;

/// <summary>
/// The static log-redaction facade (ADR-037 D9, WP10 #249): call sites stay
/// <c>Redactor.Dn(...)</c>-static while the behavior lives in the ambient
/// <see cref="Redaction"/> instance. The default is the session-salted redactor
/// (<c>Mode == "redacted"</c> — tokens are the first 8 lowercase hex of
/// SHA-256(<see cref="SessionSalt"/> ‖ value), stable within a session so events join,
/// unlinkable across sessions); Program's <c>--log-plain</c> path swaps
/// <see cref="Redaction.Identity"/> into <see cref="Current"/> (<c>Mode == "identity"</c>,
/// verbatim pass-through) BEFORE the sink is built. Structured fields are pre-redacted at
/// the call site via the typed helpers, never formatter-guessed; <see cref="Scrub"/> is the
/// free-text safety net for exception/jsError messages.
/// </summary>
public static class Redactor
{
    /// <summary>The salted per-process default — minted eagerly so <see cref="SessionSalt"/>
    /// and the ambient tokens exist before any flag parsing.</summary>
    private static readonly Redaction SaltedDefault = Redaction.CreateSalted();

    /// <summary>The ambient instance every facade helper delegates to: the salted default,
    /// or <see cref="Redaction.Identity"/> after Program's <c>--log-plain</c> swap.</summary>
    public static Redaction Current { get; internal set; } = SaltedDefault;

    /// <summary>The redaction mode name the <c>AppStarted</c> banner reports (ADR-037 D6):
    /// <c>"redacted"</c> (default) / <c>"identity"</c> (<c>--log-plain</c>).</summary>
    public static string Mode => Current.Mode;

    /// <summary>The salt of the ambient DEFAULT (what the facade's tokens hash with): stable
    /// within a session so events join, unlinkable across sessions.</summary>
    internal static byte[] SessionSalt => SaltedDefault.Salt!;

    /// <summary>Redacts a DN or DN fragment (<c>dn#&lt;hash8&gt;</c>).</summary>
    [return: NotNullIfNotNull(nameof(value))]
    public static string? Dn(string? value) => Current.Dn(value);

    /// <summary>Redacts a server/domain/baseDn string (<c>host#&lt;hash8&gt;</c>).</summary>
    [return: NotNullIfNotNull(nameof(value))]
    public static string? Host(string? value) => Current.Host(value);

    /// <summary>Redacts a filesystem PATH wholly (<c>path#&lt;hash8&gt;</c> — full user paths
    /// embed the user name; the generic <see cref="Scrub"/> pattern matches no
    /// <c>C:\Users\…</c> path, so path call sites must use THIS helper, never
    /// <see cref="Scrub"/>).</summary>
    [return: NotNullIfNotNull(nameof(value))]
    public static string? Path(string? value) => Current.Path(value);

    /// <summary>Redacts an audit-run FILE NAME (<c>&lt;utcstamp&gt;-&lt;rootDn-slug&gt;.json</c>
    /// → <c>&lt;utcstamp&gt;-run#&lt;hash8&gt;.json</c>, timestamp joinable; a non-canonical
    /// name hashes wholly).</summary>
    [return: NotNullIfNotNull(nameof(value))]
    public static string? RunFile(string? value) => Current.RunFile(value);

    /// <summary>Registers a connect-time server/baseDn string for <see cref="Scrub"/>'s
    /// learned-string replacement (ADR-037 D9); null/whitespace is a safe no-op.</summary>
    public static void Learn(string? value) => Current.Learn(value);

    /// <summary>Scrubs free text (exception / jsError messages — they embed DNs):
    /// <c>(CN|OU|DC)=…</c> runs and <see cref="Learn"/>ed strings become their tokens; every
    /// <c>msgScrubbed</c> field must pass through here.</summary>
    [return: NotNullIfNotNull(nameof(text))]
    public static string? Scrub(string? text) => Current.Scrub(text);
}
