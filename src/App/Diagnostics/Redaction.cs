using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace GroupWeaver.App.Diagnostics;

/// <summary>
/// The ADR-037 D9 instance redaction core (WP10 #249) — the constructible twin behind the
/// static <see cref="Redactor"/> facade. A SALTED instance (<see cref="CreateSalted"/>)
/// replaces sensitive values with typed tokens <c>dn#</c>/<c>host#</c>/<c>path#</c>/<c>run#</c>
/// + the first 8 lowercase hex of SHA-256(salt ‖ UTF-8(value as-given)) — stable within a
/// session so events join, salt-dependent so tokens are unlinkable across sessions. The
/// <see cref="Identity"/> instance is the <c>--log-plain</c> twin: every helper passes its
/// input through verbatim (the same string instance) and <see cref="Learn"/> is inert.
///
/// <para><b>Null/empty shape:</b> <c>null</c> stays <c>null</c> (no data to protect — the
/// <c>[return: NotNullIfNotNull]</c> contract call sites rely on); empty/whitespace becomes
/// the harmless fixed token <c>dn#empty</c> etc., never hashed (a hash of "" would
/// masquerade as a real subject).</para>
/// </summary>
public sealed class Redaction
{
    private const string RunFileExtension = ".json";

    /// <summary>The <c>yyyyMMddTHHmmssZ</c> stamp length of a canonical run-file name.</summary>
    private const int RunFileStampLength = 16;

    /// <summary><c>null</c> ⇒ the identity pass-through instance.</summary>
    private readonly byte[]? _salt;

    private readonly object _learnGate = new();

    /// <summary>Copy-on-write snapshot of the learned connect-time strings, longest first
    /// (so an overlapping shorter entry can never split a longer one mid-replacement).</summary>
    private volatile string[] _learned = [];

    private Redaction(byte[]? salt) => _salt = salt;

    /// <summary>The pass-through singleton Program's <c>--log-plain</c> path installs as
    /// <see cref="Redactor.Current"/> (and hands the sink for its <c>-PLAIN</c> variant).</summary>
    public static Redaction Identity { get; } = new(null);

    /// <summary>Mints a fresh random 16-byte session salt — a new "session": tokens join
    /// within the instance and are unlinkable across instances.</summary>
    public static Redaction CreateSalted() => new(RandomNumberGenerator.GetBytes(16));

    /// <summary>The mode name the <c>AppStarted</c> banner reports (ADR-037 D6):
    /// <c>"redacted"</c> (salted) / <c>"identity"</c> (<c>--log-plain</c>).</summary>
    public string Mode => _salt is null ? "identity" : "redacted";

    /// <summary>True for the <c>--log-plain</c> pass-through instance — the sink keys its
    /// <c>-PLAIN</c> file suffix and first-line warning off this.</summary>
    internal bool IsIdentity => _salt is null;

    /// <summary>The session salt (<c>null</c> on <see cref="Identity"/>) — exposed for the
    /// facade's <see cref="Redactor.SessionSalt"/> and its token-formula test pins.</summary>
    internal byte[]? Salt => _salt;

    /// <summary>Redacts a DN or DN fragment: <c>dn#&lt;hash8&gt;</c>.</summary>
    [return: NotNullIfNotNull(nameof(value))]
    public string? Dn(string? value) => Redact("dn", value);

    /// <summary>Redacts a server/domain/baseDn string: <c>host#&lt;hash8&gt;</c>.</summary>
    [return: NotNullIfNotNull(nameof(value))]
    public string? Host(string? value) => Redact("host", value);

    /// <summary>Redacts a filesystem PATH wholly (<c>path#&lt;hash8&gt;</c>): full user paths
    /// embed the user name, and the <see cref="Scrub"/> pattern matches no <c>C:\Users\…</c>
    /// shape — path call sites must use THIS helper, never <see cref="Scrub"/>.</summary>
    [return: NotNullIfNotNull(nameof(value))]
    public string? Path(string? value) => Redact("path", value);

    /// <summary>Redacts an audit-run FILE NAME. The canonical AuditRunStore shape
    /// <c>&lt;utcstamp&gt;-&lt;rootDn-slug&gt;.json</c> keeps its timestamp JOINABLE and hashes
    /// only the slug (<c>&lt;utcstamp&gt;-run#&lt;hash8&gt;.json</c>); anything else fails safe
    /// and hashes WHOLLY to <c>run#&lt;hash8&gt;</c> (nothing of an unrecognized name is
    /// trusted to be harmless).</summary>
    [return: NotNullIfNotNull(nameof(value))]
    public string? RunFile(string? value)
    {
        if (_salt is null || value is null)
        {
            return value;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return "run#empty";
        }

        return TryParseCanonicalRunFile(value, out var stamp, out var slug)
            ? $"{stamp}-{Token("run", slug)}{RunFileExtension}"
            : Token("run", value);
    }

    /// <summary>Registers a connect-time server/baseDn string for <see cref="Scrub"/>'s
    /// learned-string replacement (hostnames match no <c>(CN|OU|DC)=</c> pattern).
    /// Null/whitespace registrations are safe no-ops; inert on <see cref="Identity"/>.</summary>
    public void Learn(string? value)
    {
        if (_salt is null || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        lock (_learnGate)
        {
            if (_learned.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            _learned = [.. _learned.Append(value).OrderByDescending(v => v.Length)];
        }
    }

    /// <summary>Scrubs free text (exception / jsError messages) — the D9 safety net for text
    /// that never went through a typed helper. Every <c>(CN|OU|DC)=…</c> run is replaced by
    /// ITS <c>dn#</c> token (the token of the exact substring, so scrubbed text joins the
    /// structured fields that redacted the same DN), then every <see cref="Learn"/>ed string
    /// is replaced case-insensitively by the <c>host#</c> token of its learned spelling.
    /// Non-sensitive text passes through byte-identical; beyond the pinned grammar the
    /// implementation over-redacts (D9 is safety-biased).</summary>
    [return: NotNullIfNotNull(nameof(text))]
    public string? Scrub(string? text)
    {
        if (_salt is null || string.IsNullOrEmpty(text))
        {
            return text;
        }

        return ReplaceLearned(ScrubDnRuns(text));
    }

    // ---------- DN-run grammar (the RedactorScrubTests corpus is the contract) ----------
    // A run starts at CN=/OU=/DC= (case-insensitive); an UNESCAPED comma continues the run
    // only into another component (as-given spaces after it included); the escaped comma
    // "\," and interior spaces stay INSIDE a component value; ':', quotes and end-of-text
    // end the run. Trailing whitespace is trimmed OUT of the run so end-of-text tokens
    // stay joinable with the exact DN.

    private string ScrubDnRuns(string text)
    {
        var start = FindComponentStart(text, 0);
        if (start < 0)
        {
            return text; // the common case: non-sensitive text passes through byte-identical
        }

        var builder = new StringBuilder(text.Length);
        var consumed = 0;
        while (start >= 0)
        {
            builder.Append(text, consumed, start - consumed);
            var end = WalkRun(text, start);
            var trimmedEnd = end;
            while (trimmedEnd > start && char.IsWhiteSpace(text[trimmedEnd - 1]))
            {
                trimmedEnd--;
            }

            builder.Append(Token("dn", text[start..trimmedEnd]));
            builder.Append(text, trimmedEnd, end - trimmedEnd);
            consumed = end;
            start = FindComponentStart(text, consumed);
        }

        builder.Append(text, consumed, text.Length - consumed);
        return builder.ToString();
    }

    private static int FindComponentStart(string text, int from)
    {
        for (var i = from; i <= text.Length - 3; i++)
        {
            if (IsComponentAt(text, i))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary><c>CN=</c> / <c>OU=</c> / <c>DC=</c> at <paramref name="index"/>, case-insensitive.</summary>
    private static bool IsComponentAt(string text, int index)
    {
        if (index + 2 >= text.Length || text[index + 2] != '=')
        {
            return false;
        }

        var first = char.ToUpperInvariant(text[index]);
        var second = char.ToUpperInvariant(text[index + 1]);
        return (first, second) is ('C', 'N') or ('O', 'U') or ('D', 'C');
    }

    /// <summary>Walks one DN run starting at a component; returns the EXCLUSIVE end index.</summary>
    private static int WalkRun(string text, int start)
    {
        var i = start;
        while (i < text.Length)
        {
            var ch = text[i];
            if (ch is ':' or '\'' or '"' or '\r' or '\n')
            {
                break; // run terminators — the non-sensitive suffix survives verbatim
            }

            if (ch == '\\' && i + 1 < text.Length)
            {
                i += 2; // "\," and friends stay inside the component value
                continue;
            }

            if (ch == ',')
            {
                // An unescaped comma continues only into another component (spaces as-given).
                var next = i + 1;
                while (next < text.Length && text[next] == ' ')
                {
                    next++;
                }

                if (next < text.Length && IsComponentAt(text, next))
                {
                    i = next + 3;
                    continue;
                }

                break;
            }

            i++;
        }

        return i;
    }

    private string ReplaceLearned(string text)
    {
        foreach (var learned in _learned)
        {
            if (text.Contains(learned, StringComparison.OrdinalIgnoreCase))
            {
                // The token of the LEARNED spelling, so replacements join Host()-redacted fields.
                text = text.Replace(learned, Token("host", learned), StringComparison.OrdinalIgnoreCase);
            }
        }

        return text;
    }

    // ---------- token formula (pinned: prefix # first 8 lowercase hex of SHA-256(salt ‖ UTF-8)) ----------

    [return: NotNullIfNotNull(nameof(value))]
    private string? Redact(string prefix, string? value)
    {
        if (_salt is null || value is null)
        {
            return value;
        }

        return string.IsNullOrWhiteSpace(value) ? prefix + "#empty" : Token(prefix, value);
    }

    private string Token(string prefix, string value)
    {
        var salt = _salt!;
        var payload = new byte[salt.Length + Encoding.UTF8.GetByteCount(value)];
        salt.CopyTo(payload, 0);
        Encoding.UTF8.GetBytes(value, 0, value.Length, payload, salt.Length);
        return $"{prefix}#{Convert.ToHexString(SHA256.HashData(payload), 0, 4).ToLowerInvariant()}";
    }

    /// <summary>The AuditRunStore file-name shape <c>yyyyMMddTHHmmssZ-&lt;slug&gt;.json</c>.</summary>
    private static bool TryParseCanonicalRunFile(string name, out string stamp, out string slug)
    {
        stamp = string.Empty;
        slug = string.Empty;
        if (name.Length < RunFileStampLength + 2 + RunFileExtension.Length
            || !name.EndsWith(RunFileExtension, StringComparison.Ordinal)
            || name[RunFileStampLength] != '-')
        {
            return false;
        }

        for (var i = 0; i < RunFileStampLength; i++)
        {
            var ok = i switch
            {
                8 => name[i] == 'T',
                15 => name[i] == 'Z',
                _ => char.IsAsciiDigit(name[i]),
            };
            if (!ok)
            {
                return false;
            }
        }

        stamp = name[..RunFileStampLength];
        slug = name[(RunFileStampLength + 1)..^RunFileExtension.Length];
        return slug.Length > 0;
    }
}
