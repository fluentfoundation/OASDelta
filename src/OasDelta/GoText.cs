using System.Text;

namespace OasDelta;

/// <summary>
/// Go's <c>strings.ToLower</c>, <c>strings.ToUpper</c> and <c>strings.EqualFold</c>, which oasdiff and kin use on spec
/// text (header names, media types, URLs, ignore files). .NET's invariant casing (the app runs with
/// InvariantGlobalization) is the same simple per-rune mapping, except for the runes listed here: İ, ı and ſ, which
/// invariant casing leaves alone and Go maps to i, I and S, and the case pairs from Unicode versions newer than .NET 10's
/// tables (go1.27, which builds oasdiff 1.35.0, carries Unicode 17). GoTextTests checks every rune against Go's mapping.
/// </summary>
public static class GoText
{
    public static string ToLower(string s) => Patch(s.ToLowerInvariant(), lower: true);

    public static string ToUpper(string s) => Patch(s.ToUpperInvariant(), lower: false);

    /// <summary>strings.EqualFold: the same length in runes, and each pair of runes in one case-folding orbit.</summary>
    public static bool EqualFold(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal)) return true;
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            int ra = Next(a, ref i), rb = Next(b, ref j);
            if (ra != rb && Fold(ra) != Fold(rb)) return false;
        }
        return i == a.Length && j == b.Length;
    }

    /// <summary>One rune per case-folding orbit: the lower case of its upper case. İ and ı fold to no other rune in Go;
    /// three pairs fold together with no case mapping between them (CaseFolding.txt): ΐ and ΐ, ΰ and ΰ, ﬅ and ﬆ.</summary>
    public static int Fold(int r) => r switch
    {
        0x130 or 0x131 => r,
        0x1FD3 => 0x390,
        0x1FE3 => 0x3B0,
        0xFB06 => 0xFB05,
        _ => Lower(Upper(r)),
    };

    public static int Lower(int r) => Patched(Map(r, lower: true), lower: true);

    public static int Upper(int r) => Patched(Map(r, lower: false), lower: false);

    private static int Map(int r, bool lower)
    {
        if (r is >= 0xD800 and <= 0xDFFF) return r;
        var s = char.ConvertFromUtf32(r);
        var m = lower ? s.ToLowerInvariant() : s.ToUpperInvariant();
        return char.ConvertToUtf32(m, 0);
    }

    // runes .NET's invariant casing leaves unchanged and Go maps; .NET maps nothing to any of them, so they can be
    // patched after the invariant mapping
    private static int Patched(int r, bool lower) => lower
        ? r switch
        {
            0x130 => 'i',
            0xA7CE or 0xA7D2 or 0xA7D4 => r + 1,
            >= 0x16EA0 and <= 0x16EB8 => r + 0x1B,
            _ => r,
        }
        : r switch
        {
            0x131 => 'I',
            0x17F => 'S',
            0xA7CF or 0xA7D3 or 0xA7D5 => r - 1,
            >= 0x16EBB and <= 0x16ED3 => r - 0x1B,
            _ => r,
        };

    private static string Patch(string s, bool lower)
    {
        int k = 0;
        while (k < s.Length && s[k] < 0x80) k++;
        if (k == s.Length) return s;
        StringBuilder? sb = null;
        for (int i = k; i < s.Length;)
        {
            int start = i, r = Next(s, ref i), p = Patched(r, lower);
            if (p == r) { sb?.Append(s, start, i - start); continue; }
            sb ??= new StringBuilder(s.Length).Append(s, 0, start);
            sb.Append(char.ConvertFromUtf32(p));
        }
        return sb?.ToString() ?? s;
    }

    // a lone surrogate stands for itself
    private static int Next(string s, ref int i)
    {
        if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
        {
            int r = char.ConvertToUtf32(s[i], s[i + 1]);
            i += 2;
            return r;
        }
        return s[i++];
    }
}
