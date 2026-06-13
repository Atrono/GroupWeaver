using System.Text;

namespace GroupWeaver.Core.Plan;

/// <summary>
/// RFC 4514 §2.4 RDN-value escaping (ADR-014). Sits beside
/// <c>GroupWeaver.Core.Graph.DnPath</c>, which is escape-AWARE on read: a name escaped
/// here forms exactly one RDN, so a plan child sits one level below the base OU even
/// when the name carries DN metacharacters. Escapes the always-special characters
/// (<c>, + " \ &lt; &gt; ; =</c>), a leading <c>#</c>, and a leading or trailing space;
/// an interior space and a non-leading <c>#</c> are left literal.
/// </summary>
public static class Rfc4514
{
    /// <summary>Escapes <paramref name="value"/> so it is a single RDN value.</summary>
    public static string EscapeRdnValue(string value)
    {
        var sb = new StringBuilder(value.Length + 4);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var leading = i == 0;
            var trailing = i == value.Length - 1;
            switch (c)
            {
                case ',' or '+' or '"' or '\\' or '<' or '>' or ';' or '=':
                    sb.Append('\\').Append(c);
                    break;
                case '#' when leading:
                    sb.Append("\\#");
                    break;
                case ' ' when leading || trailing:
                    sb.Append("\\ ");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }
}
