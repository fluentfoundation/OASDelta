using System.Globalization;
using Xunit;

namespace OasDelta.Tests;

/// <summary>
/// <see cref="GoText"/> against Go: tests/emitter-oracle/go-case.txt is what strings.ToLower, strings.ToUpper and
/// unicode.SimpleFold (which strings.EqualFold follows) give for each rune under the toolchain that builds oasdiff
/// (tools/go-oracle/case.go; the emitter-oracle workflow regenerates it and fails if it changes). Every rune is checked.
/// </summary>
public class GoTextTests
{
    private static readonly Lazy<Dictionary<int, (int Lower, int Upper, int Orbit)>> Table = new(Load);

    private static Dictionary<int, (int, int, int)> Load()
    {
        var t = new Dictionary<int, (int, int, int)>();
        foreach (var line in File.ReadAllLines(Path.Combine(EmitterOracleTests.FindDir(), "go-case.txt")))
        {
            if (line.StartsWith('#')) continue;
            var f = line.Split(' ').Select(x => int.Parse(x, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
            t[f[0]] = (f[1], f[2], f[3]);
        }
        return t;
    }

    private static IEnumerable<int> Runes() => Enumerable.Range(0, 0x110000).Where(r => r is < 0xD800 or > 0xDFFF);

    private static (int Lower, int Upper, int Orbit) Go(int r) => Table.Value.TryGetValue(r, out var m) ? m : (r, r, r);

    [Fact]
    public void TheTableIsGosUnicode17()
    {
        Assert.StartsWith("# unicode 17.0.0:", File.ReadLines(Path.Combine(EmitterOracleTests.FindDir(), "go-case.txt")).First());
        Assert.True(Table.Value.Count > 2900, $"only {Table.Value.Count} runes");
    }

    [Fact]
    public void EveryRuneLowersAndUppersAsInGo()
    {
        var wrong = new List<string>();
        foreach (var r in Runes())
        {
            var (lower, upper, _) = Go(r);
            var s = char.ConvertFromUtf32(r);
            if (GoText.Lower(r) != lower || GoText.ToLower(s) != char.ConvertFromUtf32(lower)) wrong.Add($"ToLower {r:X}: Go {lower:X}, GoText {GoText.Lower(r):X}");
            if (GoText.Upper(r) != upper || GoText.ToUpper(s) != char.ConvertFromUtf32(upper)) wrong.Add($"ToUpper {r:X}: Go {upper:X}, GoText {GoText.Upper(r):X}");
        }
        Assert.True(wrong.Count == 0, string.Join("\n", wrong.Take(50)));
    }

    /// <summary>GoText.Fold names the orbits Go's EqualFold matches: two runes fold alike exactly when Go puts them in
    /// one orbit.</summary>
    [Fact]
    public void EveryRuneFoldsAsInGo()
    {
        var orbitOfFold = new Dictionary<int, int>();
        var foldOfOrbit = new Dictionary<int, int>();
        var wrong = new List<string>();
        foreach (var r in Runes())
        {
            int fold = GoText.Fold(r), orbit = Go(r).Orbit;
            if (orbitOfFold.TryGetValue(fold, out var o) && o != orbit) wrong.Add($"{r:X} folds to {fold:X}, like orbit {o:X}; Go's orbit is {orbit:X}");
            if (foldOfOrbit.TryGetValue(orbit, out var f) && f != fold) wrong.Add($"{r:X} folds to {fold:X}; its orbit {orbit:X} folds to {f:X}");
            orbitOfFold.TryAdd(fold, orbit);
            foldOfOrbit.TryAdd(orbit, fold);
        }
        Assert.True(wrong.Count == 0, string.Join("\n", wrong.Take(50)));
    }

    [Theory]
    [InlineData("Content-Type", "content-type", true)]
    [InlineData("Kelvin", "kelvin", true)] // KELVIN SIGN folds with k
    [InlineData("ſecret", "Secret", true)] // LONG S folds with s
    [InlineData("straße", "STRAẞE", true)] // sharp s and capital sharp s
    [InlineData("İd", "id", false)] // Go: İ is in no orbit with i
    [InlineData("ıd", "Id", false)] // nor ı with I
    [InlineData("꟒", "ꟓ", true)] // Unicode 17's double thorn
    [InlineData("ab", "abc", false)]
    [InlineData("😀", "😀", true)]
    [InlineData("\uD83D", "\uD83D", true)]
    public void EqualFoldAsInGo(string a, string b, bool equal) => Assert.Equal(equal, GoText.EqualFold(a, b));

    [Theory]
    [InlineData("X-İD", "x-id")]
    [InlineData("꟒-Header", "ꟓ-header")]
    [InlineData("plain", "plain")]
    [InlineData("\uD83D-A", "\uD83D-a")] // a lone surrogate stays
    public void ToLowerAsInGo(string s, string lower) => Assert.Equal(lower, GoText.ToLower(s));
}
