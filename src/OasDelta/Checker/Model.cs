using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OasDelta.Diff;
using OasDelta.Model;
using OasDelta.Yaml;

namespace OasDelta.Checker;

public enum Level { INVALID = -1, NONE = 0, INFO = 1, WARN = 2, ERR = 3 }
public enum Direction : sbyte { Request, Response, None }
public enum Area : sbyte { Schema, Parameters, RequestBody, Responses, Paths, Headers, Security, Tags, Components, Info, Servers, None }
public enum Kind : sbyte { Existence, Requiredness, Mutability, Type, Constraints, Values, Structure, Lifecycle, None }
public enum Effect : sbyte { None, Widens, Narrows, Incomparable, Unknown, Violation }
public enum Guard : sbyte { ReadOnly, WriteOnly, Sanctioned, NonSuccess, HasDefault, Negotiated, RequiredInRequests }
public enum Disclaimer { AllOfNotFlattened = 1 }
public enum ColorMode { Auto, Always, Never }

public static class EnumText
{
    public static string Name(this Level l) => l switch { Level.ERR => "error", Level.WARN => "warning", Level.INFO => "info", _ => "issue" };
    public static bool IsBreaking(this Level l) => l is Level.ERR or Level.WARN;
    public static string Pretty(this Level l) => l switch
    {
        Level.ERR => Color.Red(l.Name()), Level.WARN => Color.Purple(l.Name()), Level.INFO => Color.Cyan(l.Name()), _ => Color.Gray(l.Name()),
    };
    public static string Name(this Direction d) => d switch { Direction.Request => "request", Direction.Response => "response", _ => "none" };
    public static string Name(this Area a) => a switch
    {
        Area.Schema => "schema", Area.Parameters => "parameters", Area.RequestBody => "requestBody", Area.Responses => "responses", Area.Paths => "paths",
        Area.Headers => "headers", Area.Security => "security", Area.Tags => "tags", Area.Components => "components", Area.Info => "info", Area.Servers => "servers", _ => "none",
    };
    public static string Name(this Kind k) => k switch
    {
        Kind.Existence => "existence", Kind.Requiredness => "requiredness", Kind.Mutability => "mutability", Kind.Type => "type", Kind.Constraints => "constraints",
        Kind.Values => "values", Kind.Structure => "structure", Kind.Lifecycle => "lifecycle", _ => "none",
    };
    public static string Name(this Effect e) => e switch
    {
        Effect.Widens => "widens", Effect.Narrows => "narrows", Effect.Incomparable => "incomparable", Effect.Unknown => "unknown", Effect.Violation => "violation", _ => "none",
    };
    public static string Name(this Guard g) => g switch
    {
        Guard.ReadOnly => "read-only", Guard.WriteOnly => "write-only", Guard.Sanctioned => "sanctioned", Guard.NonSuccess => "non-success",
        Guard.HasDefault => "has-default", Guard.Negotiated => "negotiated", _ => "required-in-requests",
    };
    public static string Name(this Disclaimer d) => d == Disclaimer.AllOfNotFlattened ? "all-of-not-flattened" : "unknown-disclaimer";

    public static Level ParseLevel(string s) => s switch
    {
        "ERR" or "err" => Level.ERR, "WARN" or "warn" => Level.WARN, "INFO" or "info" => Level.INFO, "NONE" or "none" => Level.NONE, _ => Level.INVALID,
    };
}

public static class Color
{
    private const string Reset = "\u001b[0m";
    public static string Red(string s) => "\u001b[31m" + s + Reset;
    public static string Green(string s) => "\u001b[32m" + s + Reset;
    public static string Yellow(string s) => "\u001b[33m" + s + Reset;
    public static string Purple(string s) => "\u001b[35m" + s + Reset;
    public static string Cyan(string s) => "\u001b[36m" + s + Reset;
    public static string Gray(string s) => "\u001b[37m" + s + Reset;
    public static string Bold(string s) => "\u001b[1m" + s + Reset;

    public static bool PipedOutput = Console.IsOutputRedirected;
    public static bool Enabled(ColorMode m) => m == ColorMode.Always || (m == ColorMode.Auto && !PipedOutput);
}

public delegate List<Change> CheckFn(OpenApiDiff diff, Sources sources, CheckerConfig config);

/// <summary>Maps operations to the spec file they came from (diff.OperationsSourcesMap).</summary>
public sealed class Sources(Dictionary<Operation, string>? map = null)
{
    public readonly Dictionary<Operation, string> Map = map ?? new(ReferenceEqualityComparer.Instance);
    public string this[Operation? op] => op is not null && Map.TryGetValue(op, out var s) ? s : "";
}

public sealed record Rule(string Id, Level Level, CheckFn? Handler, Direction Direction, Area Area, Kind Kind, Effect Effect, Guard[] Guards, string[] Locations)
{
    public bool Generated { get; init; }
    public string Description => Id + "-description";
    public Level DerivedLevel => Derive.LevelOf(Effect, Direction, Guards);
}

public static class Derive
{
    public static Level LevelOf(Effect effect, Direction direction, IEnumerable<Guard> guards)
    {
        foreach (var g in guards)
        {
            switch (g)
            {
                case Guard.ReadOnly: if (direction == Direction.Request) effect = Effect.None; break;
                case Guard.WriteOnly: if (direction == Direction.Response) effect = Effect.None; break;
                case Guard.NonSuccess: case Guard.Sanctioned: effect = Effect.None; break;
                case Guard.RequiredInRequests: if (direction == Direction.Request) effect = Effect.None; break;
                case Guard.Negotiated: direction = Direction.Request; break;
            }
        }
        return effect switch
        {
            Effect.Violation or Effect.Incomparable => Level.ERR,
            Effect.None => Level.INFO,
            Effect.Unknown => Level.WARN,
            Effect.Narrows => direction == Direction.Response ? Level.INFO : Level.ERR,
            Effect.Widens => direction == Direction.Response ? Level.ERR : Level.INFO,
            _ => Level.INFO,
        };
    }
}

public enum StabilityLevel { Draft = 1, Alpha, Beta, Stable }

public sealed class CheckerConfig
{
    public List<CheckFn> Checks;
    public uint MinSunsetBetaDays, MinSunsetStableDays;
    public Dictionary<string, Level> LogLevels;
    internal HashSet<string> OverriddenLevels = new(StringComparer.Ordinal);
    public List<string> Attributes = [];
    public StabilityLevel StabilityLevel = StabilityLevel.Beta;

    public CheckerConfig(List<CheckFn>? checks = null)
    {
        Checks = checks ?? Rules.AllChecks();
        LogLevels = Rules.CheckLevels();
    }

    public Level LevelOf(string id) => LogLevels.TryGetValue(id, out var l) ? l : throw new InvalidOperationException("failed to get log level with invalid check id: " + id);

    public void SetLevel(string id, Level level)
    {
        if (!LogLevels.ContainsKey(id)) throw new InvalidOperationException("failed to set log level with invalid check id: " + id);
        OverriddenLevels.Add(id);
        LogLevels[id] = level;
    }

    public void SetStabilityLevel(string s) { if (s != "") StabilityLevel = Stability.Parse(s); }
}

public static class Stability
{
    public const string Draft = "draft", Alpha = "alpha", Beta = "beta", Stable = "stable";
    public static readonly string[] Supported = [Draft, Alpha, Beta, Stable];
    public static StabilityLevel Parse(string s) => s switch { Draft => StabilityLevel.Draft, Alpha => StabilityLevel.Alpha, Beta => StabilityLevel.Beta, _ => StabilityLevel.Stable };
    public static bool IsIncluded(this StabilityLevel l, string stability) => Parse(stability) >= l;
    public static string Normalized(string label) => label == "" ? Stable : label;

    /// <summary>getStabilityLevel: returns label ("" when absent) or an error message.</summary>
    public static (string label, string? error) Get(Dictionary<string, object?>? ext)
    {
        if (ext is null || !ext.TryGetValue(DiffConfig.XStabilityLevelExtension, out var v) || v is null) return ("", null);
        if (v is not string s)
            return ("", $"value is not one of {Draft}, {Alpha}, {Beta} or {Stable}: {Fmt.ExtValueText(v)}");
        if (s is not (Draft or Alpha or Beta or Stable))
            return ("", $"value is not one of {Draft}, {Alpha}, {Beta} or {Stable}: {Fmt.GoQuote(s)}");
        return (s, null);
    }

    public static uint DeprecationDays(CheckerConfig config, string stability) => stability switch
    {
        Draft or Alpha => 0,
        Beta => config.MinSunsetBetaDays,
        _ => config.MinSunsetStableDays,
    };
}

/// <summary>A source location attached to a change (checker/internal/location.Source).</summary>
public sealed class SourceLoc : IOutput
{
    public string File = "";
    public int Line, Column, EndLine, EndColumn;
    public object? ToOutput()
    {
        var m = new OMap();
        m.OptStr("file", File);
        if (Line != 0) m.Add("line", (long)Line);
        if (Column != 0) m.Add("column", (long)Column);
        if (EndLine != 0) m.Add("endLine", (long)EndLine);
        if (EndColumn != 0) m.Add("endColumn", (long)EndColumn);
        return m;
    }
}

/// <summary>Error values used as change args (Go error, printed with %v).</summary>
public sealed record GoError(string Message)
{
    public override string ToString() => Message;
}

public sealed class SharedSchema : IOutput
{
    public string Name = "";
    public List<string> Properties = [];
    public int Count;
    public bool Cyclic;

    public object? ToOutput()
    {
        var m = new OMap();
        m.OptStr("name", Name);
        m.Add("properties", Properties);
        if (Count != 0) m.Add("count", (long)Count);
        m.OptTrue("cyclic", Cyclic);
        return m;
    }

    internal string Detail(Localizer l, Func<List<object?>, List<object?>> format)
    {
        var parts = new List<string>();
        if (Name != "") parts.Add(l.Get(Ids.SharedSchemaDetailName, format([Name])));
        if (Properties.Count > 1)
        {
            var others = Properties.Skip(1).Select(x => (object?)x).ToList();
            var list = string.Join(", ", format(others).Select(Fmt.InterfaceToString));
            int more = Count - Properties.Count;
            if (Cyclic) parts.Add(l.Get(Ids.SharedSchemaDetailAlsoCyclic, [list]));
            else if (more > 0) parts.Add(l.Get(Ids.SharedSchemaDetailAlsoMore, [list, more]));
            else parts.Add(l.Get(Ids.SharedSchemaDetailAlso, [list]));
        }
        return parts.Count == 0 ? "" : "(" + string.Join(", ", parts) + ")";
    }
}

public enum ChangeSection { Api, Component, Security, Info }

/// <summary>A detected change (ApiChange / ComponentChange / SecurityChange / InfoChange).</summary>
public sealed class Change
{
    public ChangeSection Kind;
    public string Id = "";
    public List<object?> Args = [];
    public string Comment = "";
    public List<Disclaimer> Disclaimers = [];
    public string Details = "";
    public Level Level;
    public string Operation = "", OperationId = "", Path = "";
    public string SourcePath = "";     // spec source for api changes
    public string Component = "";      // component changes
    public Dictionary<string, object?>? Attributes;
    public SourceLoc? BaseSource, RevisionSource;
    public string SourceFile = "";
    public int SourceLine, SourceLineEnd, SourceColumn, SourceColumnEnd;

    internal SchemaDiff? Schema, Root;
    internal string PropertyPath = "";
    internal SharedSchema? Shared;
    internal List<Guard> Guards = [];

    public Change Clone()
    {
        var c = (Change)MemberwiseClone();
        c.Args = [.. Args];
        c.Disclaimers = [.. Disclaimers];
        c.Guards = [.. Guards];
        return c;
    }

    public Change WithSources(SourceLoc? b, SourceLoc? r) { var c = Clone(); c.BaseSource = b; c.RevisionSource = r; return c; }
    public Change WithSchema(SchemaDiff? root, SchemaDiff? schema, string propertyPath) { var c = Clone(); c.Root = root; c.Schema = schema; c.PropertyPath = propertyPath; return c; }
    public Change WithDetails(string details) { var c = Clone(); c.Details = details; return c; }
    public Change WithGuards(IEnumerable<Guard> guards)
    {
        var c = Clone();
        foreach (var g in guards) if (!c.Guards.Contains(g)) c.Guards.Add(g);
        return c;
    }
    public Change WithoutGuard(Guard g) { var c = Clone(); c.Guards.RemoveAll(x => x == g); return c; }
    public Change WithDisclaimers(IEnumerable<Disclaimer> ds)
    {
        var c = Clone();
        foreach (var d in ds) if (!c.Disclaimers.Contains(d)) c.Disclaimers.Add(d);
        return c;
    }
    public Change WithAllOfDisclaimers(bool underAllOf, SchemaDiff? schemaDiff) => WithDisclaimers(Walkers.AllOfDisclaimers(underAllOf, schemaDiff, Id));
    public Change WithShared(SharedSchema? s) { var c = Clone(); c.Shared = s; return c; }
    public SharedSchema? GetSharedSchema() => Shared;

    public string Section => Kind switch { ChangeSection.Api => "paths", ChangeSection.Component => "components", ChangeSection.Security => "security", _ => "info" };
    public bool IsBreaking => Level.IsBreaking();
    public string Source => Kind == ChangeSection.Api ? SourceDisplay(SourcePath) : "";

    public static string SourceDisplay(string path)
    {
        if (path == "-") return "stdin";
        if (IsGitRevision(path)) return path[(path.IndexOf(':') + 1)..];
        return path;
    }

    public static bool IsGitRevision(string path)
    {
        int idx = path.IndexOf(':');
        if (idx < 0 || path.Contains("://", StringComparison.Ordinal)) return false;
        if (idx == 1 && path[0] >= 'A' && path[0] <= 'Z') return false;
        return true;
    }

    public string GetSourceFile()
    {
        if (Kind != ChangeSection.Api) return SourceFile;
        if (SourceFile != "") return SourceFile;
        var p = SourcePath;
        bool isFile = p != "-" && p != "stdin" && !IsGitRevision(p) && !p.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !p.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        return isFile ? p : "";
    }

    private string DetailsSuffix(Localizer l, Func<List<object?>, List<object?>> format)
    {
        var details = Walkers.CombineDetails(Details, Shared?.Detail(l, format) ?? "");
        return details == "" ? "" : " " + details;
    }

    public string GetText(Localizer l) => l.Get(Id, Fmt.Colorized(Args)) + (Kind == ChangeSection.Api ? DetailsSuffix(l, Fmt.Colorized) : "");
    public string GetUncolorizedText(Localizer l) => l.Get(Id, Fmt.Quoted(Args)) + (Kind == ChangeSection.Api ? DetailsSuffix(l, Fmt.Quoted) : "");

    public string GetComment(Localizer l)
    {
        if (Kind != ChangeSection.Api) return l.Get(Comment, []);
        var parts = new List<string>();
        if (Comment != "") parts.Add(l.Get(Comment, []));
        foreach (var d in Disclaimers) parts.Add(l.Get(d.Name() + "-comment", []));
        foreach (var g in Guards) parts.Add(l.Get(g.Name() + "-comment", []));
        return string.Join(" ", parts);
    }

    public bool MatchIgnore(string ignorePath, string ignoreLine, Localizer l)
    {
        switch (Kind)
        {
            case ChangeSection.Api:
                if (ignorePath == "") return false;
                return ignorePath == Path.ToLowerInvariant() && ignoreLine.Contains((Operation + " " + Path).ToLowerInvariant(), StringComparison.Ordinal)
                    && ignoreLine.Contains(GetUncolorizedText(l).ToLowerInvariant(), StringComparison.Ordinal);
            case ChangeSection.Component:
                return ignoreLine.Contains(GetUncolorizedText(l).ToLowerInvariant(), StringComparison.Ordinal) && ignoreLine.Contains("components", StringComparison.Ordinal);
            case ChangeSection.Security:
                return ignoreLine.Contains(GetUncolorizedText(l).ToLowerInvariant(), StringComparison.Ordinal) && ignoreLine.Contains("security", StringComparison.Ordinal);
            default:
                return ignoreLine.Contains(GetUncolorizedText(l).ToLowerInvariant(), StringComparison.Ordinal) && ignoreLine.Contains("info", StringComparison.Ordinal);
        }
    }

    private static string MultiLineComment(string c) => c == "" ? "" : "\n\t\t" + c;

    public string SingleLineError(Localizer l, ColorMode mode)
    {
        bool color = Color.Enabled(mode);
        switch (Kind)
        {
            case ChangeSection.Api:
                return color
                    ? $"{Level.Pretty()} {l.Get("at", [])} {Source}, {l.Get("in", [])} API {Color.Green(Operation)} {Color.Green(Path)} {GetText(l)} [{Color.Yellow(Id)}]. {GetComment(l)}"
                    : $"{Level.Name()} {l.Get("at", [])} {Source}, {l.Get("in", [])} API {Operation} {Path} {GetUncolorizedText(l)} [{Id}]. {GetComment(l)}";
            case ChangeSection.Component:
                return color
                    ? $"{Level.Pretty()}, {l.Get("in", [])} components/{Component} {GetText(l)} [{Color.Yellow(Id)}]. {GetComment(l)}"
                    : $"{Level.Name()}, {l.Get("in", [])} components/{Component} {GetUncolorizedText(l)} [{Id}]. {GetComment(l)}";
            default:
                var section = Kind == ChangeSection.Security ? "security" : "info";
                return color
                    ? $"{Level.Pretty()}, {l.Get("in", [])} {section} {GetText(l)} [{Color.Yellow(Id)}]. {GetComment(l)}"
                    : $"{Level.Name()}, {l.Get("in", [])} {section} {GetUncolorizedText(l)} [{Id}]. {GetComment(l)}";
        }
    }

    public string MultiLineError(Localizer l, ColorMode mode)
    {
        bool color = Color.Enabled(mode);
        switch (Kind)
        {
            case ChangeSection.Api:
                return color
                    ? $"{Level.Pretty()}\t[{Color.Yellow(Id)}] {l.Get("at", [])} {Source}\n\t{l.Get("in", [])} API {Color.Green(Operation)} {Color.Green(Path)}\n\t\t{GetText(l)}{MultiLineComment(GetComment(l))}"
                    : $"{Level.Name()}\t[{Id}] {l.Get("at", [])} {Source}\n\t{l.Get("in", [])} API {Operation} {Path}\n\t\t{GetUncolorizedText(l)}{MultiLineComment(GetComment(l))}";
            case ChangeSection.Component:
                return color
                    ? $"{Level.Pretty()}\t[{Color.Yellow(Id)}]\n\t{l.Get("in", [])} components/{Component}\n\t\t{GetText(l)}{MultiLineComment(GetComment(l))}"
                    : $"{Level.Name()}\t[{Id}]\n\t{l.Get("in", [])} components/{Component}\n\t\t{GetUncolorizedText(l)}{MultiLineComment(GetComment(l))}";
            default:
                var section = Kind == ChangeSection.Security ? "security" : "info";
                return color
                    ? $"{Level.Pretty()}\t[{Color.Yellow(Id)}]\n\t{l.Get("in", [])} {section}\n\t\t{GetText(l)}{MultiLineComment(GetComment(l))}"
                    : $"{Level.Name()}\t[{Id}]\n\t{l.Get("in", [])} {section}\n\t\t{GetUncolorizedText(l)}{MultiLineComment(GetComment(l))}";
        }
    }

    public string Fingerprint()
    {
        var args = Args.Count == 0 ? "" : string.Join(";", Args.Select(Fmt.GoV));
        var h = $"{Id}:{Operation}:{Path}:{args}";
        var sum = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(h));
        return Convert.ToHexStringLower(sum)[..12];
    }

    public static int Compare(Change a, Change b)
    {
        int c = b.Level.CompareTo(a.Level);
        if (c != 0) return c;
        if ((c = string.CompareOrdinal(a.Path, b.Path)) != 0) return c;
        if ((c = string.CompareOrdinal(a.Operation, b.Operation)) != 0) return c;
        if ((c = string.CompareOrdinal(a.Id, b.Id)) != 0) return c;
        if ((c = a.Args.Count.CompareTo(b.Args.Count)) != 0) return c;
        for (int i = 0; i < a.Args.Count; i++)
            if ((c = string.CompareOrdinal(Fmt.InterfaceToString(a.Args[i]), Fmt.InterfaceToString(b.Args[i]))) != 0) return c;
        return string.CompareOrdinal(a.Kind == ChangeSection.Api ? a.Details : "", b.Kind == ChangeSection.Api ? b.Details : "");
    }
}

/// <summary>Go fmt helpers used for messages.</summary>
public static class Fmt
{
    /// <summary>checker.interfaceToString</summary>
    public static string InterfaceToString(object? arg) => arg switch
    {
        null => "undefined",
        string s => s,
        List<string> ls => string.Join(", ", ls),
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        ulong u => u.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString("F2", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        _ => GoV(arg),
    };

    public static string GoV(object? v) => v switch
    {
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        GoError e => e.Message,
        List<object?> l => "[" + string.Join(" ", l.Select(GoV)) + "]",
        _ => Values.GoV(v),
    };

    public static List<object?> Colorized(List<object?> args) => args.Select(a => (object?)Color.Bold("'" + InterfaceToString(a) + "'")).ToList();
    public static List<object?> Quoted(List<object?> args) => args.Select(a => (object?)("`" + InterfaceToString(a) + "`")).ToList();

    public static string ExtValueText(object? v) => v is string s ? s : JsonEmitter.Emit(v);

    public static string GoQuote(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var r in s.EnumerateRunes())
        {
            int c = r.Value;
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case 7: sb.Append("\\a"); break;
                case 8: sb.Append("\\b"); break;
                case 0xC: sb.Append("\\f"); break;
                case 0xB: sb.Append("\\v"); break;
                default:
                    if (c < 0x20 || c == 0x7F) sb.Append("\\x").Append(c.ToString("x2", CultureInfo.InvariantCulture));
                    else sb.Append(r.ToString());
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    private static string GoTypeName(object? v) => v switch
    {
        null => "<nil>", string => "string", int => "int", long => "int64", ulong => "uint64", double => "float64", bool => "bool", _ => "string",
    };

    /// <summary>A subset of Go's fmt.Sprintf: %s %v %d %q %t %f %%, with %!(EXTRA ...) and %!s(MISSING) semantics.</summary>
    public static string Sprintf(string format, IReadOnlyList<object?> args)
    {
        var sb = new StringBuilder(format.Length + 32);
        int argi = 0;
        for (int i = 0; i < format.Length; i++)
        {
            char c = format[i];
            if (c != '%') { sb.Append(c); continue; }
            if (i + 1 >= format.Length) { sb.Append("%!(NOVERB)"); break; }
            // flags / width / precision
            int j = i + 1;
            string precision = "";
            while (j < format.Length && (format[j] is '+' or '-' or '#' or ' ' or '0' || char.IsAsciiDigit(format[j]))) j++;
            if (j < format.Length && format[j] == '.')
            {
                int p0 = ++j;
                while (j < format.Length && char.IsAsciiDigit(format[j])) j++;
                precision = format[p0..j];
            }
            if (j >= format.Length) { sb.Append("%!(NOVERB)"); break; }
            char verb = format[j];
            i = j;
            if (verb == '%') { sb.Append('%'); continue; }
            if (argi >= args.Count) { sb.Append("%!").Append(verb).Append("(MISSING)"); continue; }
            var a = args[argi++];
            switch (verb)
            {
                case 's': case 'v': sb.Append(a is string s ? s : GoV(a)); break;
                case 'd':
                    sb.Append(a switch
                    {
                        int x => x.ToString(CultureInfo.InvariantCulture), long x => x.ToString(CultureInfo.InvariantCulture),
                        ulong x => x.ToString(CultureInfo.InvariantCulture), uint x => x.ToString(CultureInfo.InvariantCulture),
                        _ => "%!d(" + GoTypeName(a) + "=" + GoV(a) + ")",
                    });
                    break;
                case 'q': sb.Append(a is string qs ? GoQuote(qs) : GoQuote(GoV(a))); break;
                case 't': sb.Append(a is bool b ? (b ? "true" : "false") : "%!t(" + GoTypeName(a) + "=" + GoV(a) + ")"); break;
                case 'f':
                    if (a is double d)
                        sb.Append(d.ToString("F" + (precision == "" ? "6" : precision), CultureInfo.InvariantCulture));
                    else sb.Append("%!f(" + GoTypeName(a) + "=" + GoV(a) + ")");
                    break;
                default: sb.Append('%').Append(verb); break;
            }
        }
        if (argi < args.Count)
        {
            sb.Append("%!(EXTRA ");
            for (int k = argi; k < args.Count; k++)
            {
                if (k > argi) sb.Append(", ");
                sb.Append(GoTypeName(args[k])).Append('=').Append(GoV(args[k]));
            }
            sb.Append(')');
        }
        return sb.ToString();
    }
}

public sealed partial class Localizer
{
    private readonly Dictionary<string, string> _messages;
    public Localizer(string lang = "en") => _messages = Localizations.Get(lang);
    public static Localizer Default { get; } = new("en");

    public string Get(string key, IReadOnlyList<object?> args)
    {
        if (!_messages.TryGetValue(key, out var pattern)) return key;
        return Fmt.Sprintf(pattern, args);
    }
    public string this[string key] => Get(key, []);
}

public static partial class Localizations
{
    private static readonly Dictionary<string, Dictionary<string, string>> Cache = new(StringComparer.Ordinal);
    public static Dictionary<string, string> Get(string lang)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(lang, out var d))
            {
                d = ForLang(lang);
                if (lang != "en")
                {
                    // localizations.New(locale, LangDefault): keys missing from a translation fall back to English
                    var merged = new Dictionary<string, string>(ForLang("en"), StringComparer.Ordinal);
                    foreach (var (k, v) in d) merged[k] = v;
                    d = merged;
                }
                Cache[lang] = d;
            }
            return d;
        }
    }
}
