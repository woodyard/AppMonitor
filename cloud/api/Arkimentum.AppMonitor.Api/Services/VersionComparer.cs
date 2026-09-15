using System.Text.RegularExpressions;

namespace Arkimentum.AppMonitor.Api.Services;

/// <summary>
/// Ported from src/Arkimentum.AppMonitor.Core/Versioning/VersionComparer.cs so the inventory sorts and counts
/// versions exactly as the agent does. VersionComparerParityTests keeps the two implementations honest.
/// </summary>
public static partial class VersionComparer
{
    [GeneratedRegex(@"[.\-_+ ]+")]
    private static partial Regex SeparatorRegex();

    [GeneratedRegex(@"(\d+|[A-Za-z]+)")]
    private static partial Regex TokenRegex();

    public static readonly IComparer<string?> Instance = Comparer<string?>.Create(Compare);

    /// <summary>Returns true when <paramref name="available"/> is strictly newer than <paramref name="installed"/>.</summary>
    public static bool IsNewer(string? available, string? installed)
    {
        if (string.IsNullOrWhiteSpace(available)) return false;
        if (IsUnknown(installed)) return true;
        return Compare(available, installed) > 0;
    }

    public static bool IsUnknown(string? version) =>
        string.IsNullOrWhiteSpace(version) ||
        version.Trim().Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
        version.Trim().StartsWith('<') || version.Trim().StartsWith('>');

    public static string Normalize(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return string.Empty;
        var v = version.Trim();
        if (v.Length > 1 && (v[0] == 'v' || v[0] == 'V') && char.IsDigit(v[1])) v = v[1..];
        return v;
    }

    public static int Compare(string? a, string? b)
    {
        var na = Normalize(a);
        var nb = Normalize(b);
        if (na.Length == 0 && nb.Length == 0) return 0;
        if (na.Length == 0) return -1;
        if (nb.Length == 0) return 1;

        var ta = Tokenize(na);
        var tb = Tokenize(nb);
        var n = Math.Max(ta.Count, tb.Count);
        for (var i = 0; i < n; i++)
        {
            var x = i < ta.Count ? ta[i] : Token.Zero;
            var y = i < tb.Count ? tb[i] : Token.Zero;
            var c = x.CompareTo(y);
            if (c != 0) return c;
        }
        return 0;
    }

    private static List<Token> Tokenize(string v)
    {
        var list = new List<Token>();
        foreach (var part in SeparatorRegex().Split(v))
        {
            if (part.Length == 0) continue;
            foreach (Match m in TokenRegex().Matches(part))
            {
                var s = m.Value;
                if (char.IsDigit(s[0]))
                    list.Add(decimal.TryParse(s, out var d) ? new Token(d, null) : new Token(null, s));
                else
                    list.Add(new Token(null, s));
            }
        }
        return list;
    }

    private readonly record struct Token(decimal? Number, string? Text) : IComparable<Token>
    {
        public static readonly Token Zero = new(0, null);

        public int CompareTo(Token other)
        {
            if (Number is { } n && other.Number is { } m) return n.CompareTo(m);
            if (Number is not null) return 1;       // numeric beats text: 1.2.3 > 1.2.3-beta
            if (other.Number is not null) return -1;
            return string.Compare(Text, other.Text, StringComparison.OrdinalIgnoreCase);
        }
    }
}
