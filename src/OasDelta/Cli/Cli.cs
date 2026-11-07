using System.Reflection;
using System.Text;
using OasDelta.Checker;
using OasDelta.Diff;
using OasDelta.Formatters;
using OasDelta.Load;
using OasDelta.Model;
using OasDelta.Yaml;

namespace OasDelta.Cli;

/// <summary>An error that ends the run with a specific exit code (internal/errors.go ReturnError).</summary>
public sealed class ReturnError(string message, int code, bool showUsage = false) : Exception(message)
{
    public readonly int Code = code;
    public readonly bool ShowUsage = showUsage;
}

public sealed class Command
{
    public required string Name, Use, Short;
    public string Long = "";
    public string Group = "";
    public List<FlagDef> Flags = [];
    public Func<FlagValues, Context, int>? Run;
    public Action<FlagValues>? ValidateArgs;
    public List<Command> Sub = [];
    public Command? Parent;
    public string Path => Parent is null ? Name : Parent.Path + " " + Name;
}

public sealed class Context(TextWriter stdout, TextWriter stderr)
{
    public readonly TextWriter Out = stdout, Err = stderr;
}

/// <summary>The oasdiff command line (internal/*.go): cobra-style commands, pflag flags, viper config files.</summary>
public static class Cli
{
    public const string Name = "oasdelta";
    /// <summary>The build's version (dotnet publish -p:Version=...), without the "+commit" suffix the SDK appends.</summary>
    public static readonly string Version =
        typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "dev";
    private const int GeneralExecutionErr = 100;

    private const string SpecHelp = @"
Base and revision can be a path to a file, a URL, a git ref (e.g. main:openapi.yaml), or '-' to read standard input.
In 'composed' mode, base and revision can be a glob and oasdiff will compare matching endpoints between the two sets of files.";

    private static readonly FlagDef ConfigFlag = new("config", "", FlagKind.String, "", "path to config file (overrides .oasdelta.* / .oasdiff.* lookup; can also use the OASDELTA_CONFIG or OASDIFF_CONFIG env var)");

    private static readonly string[] Languages = ["en", "ru", "pt-br", "es"];
    private static readonly string[] Levels = ["ERR", "WARN", "INFO"];
    private static readonly string[] ChangelogFormats = ["githubactions", "html", "json", "junit", "markdown", "markup", "singleline", "text", "yaml"];
    private static readonly string[] DiffFormats = ["html", "json", "markdown", "markup", "text", "yaml"];
    private static readonly string[] SummaryFormats = ["json", "yaml"];
    private static readonly string[] AllFormats = ["yaml", "json", "text", "markup", "markdown", "singleline", "html", "githubactions", "junit", "sarif"];
    private static readonly string[] TemplateFormats = ["html", "markdown", "markup"];

    // ------------------------------------------------------------------ flag sets

    private static FlagDef Enum(string name, string sh, string[] allowed, string def, string usage) =>
        new(name, sh, FlagKind.Enum, def, usage + ": " + FlagDef.ListOf(allowed)) { Allowed = allowed };

    private static FlagDef EnumSlice(string name, string sh, string[] allowed, string usage)
    {
        var sorted = allowed.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        return new(name, sh, FlagKind.EnumSlice, null, usage + ": " + FlagDef.ListOf(sorted)) { Allowed = sorted };
    }

    private static IEnumerable<FlagDef> CommonDiffFlags() =>
    [
        new("match-path", "p", FlagKind.String, "", "include only paths that match this regular expression"),
        new("unmatch-path", "q", FlagKind.String, "", "exclude paths that match this regular expression"),
        new("filter-extension", "", FlagKind.String, "", "exclude paths and operations with an OpenAPI Extension matching this regular expression"),
        new("prefix-base", "", FlagKind.String, "", "add this prefix to paths in base-spec before comparison"),
        new("prefix-revision", "", FlagKind.String, "", "add this prefix to paths in revised-spec before comparison"),
        new("strip-prefix-base", "", FlagKind.String, "", "strip this prefix from paths in base-spec before comparison"),
        new("strip-prefix-revision", "", FlagKind.String, "", "strip this prefix from paths in revised-spec before comparison"),
        new("include-path-params", "", FlagKind.Bool, false, "include path parameter names in endpoint matching"),
        new("match-inline-refs", "", FlagKind.Bool, true, "match validation-equivalent inline/$ref subschemas as the same anyOf/oneOf branch"),
        new("flatten-allof", "", FlagKind.Bool, false, "merge subschemas under allOf before diff"),
        new("flatten-params", "", FlagKind.Bool, false, "merge common parameters at path level with operation parameters"),
        new("case-insensitive-headers", "", FlagKind.Bool, true, "case-insensitive header name comparison (HTTP headers are case-insensitive per RFC 7230)"),
        new("exclude-extensions", "", FlagKind.Strings, null, "OpenAPI Extension names to exclude from diff (e.g., x-internal)"),
        new("allow-external-refs", "", FlagKind.Bool, true, "allow external $refs in specs; disable to prevent SSRF when processing untrusted specs"),
        new("auto-upgrade", "", FlagKind.Bool, false, "canonicalize both specs to the latest OpenAPI 3.x before diffing; useful for cross-version comparisons (e.g. 3.0 vs 3.1)"),
        new("fetch", "", FlagKind.Bool, false, "fetch missing git revisions from the 'origin' remote (writes objects to your local repo)"),
        new("flatten", "", FlagKind.Bool, false, "merge subschemas under allOf before diff") { Hidden = true },
        new("max-circular-dep", "", FlagKind.Int, 5L, "maximum allowed number of circular dependencies between objects in OpenAPI specs") { Hidden = true },
        new("composed", "c", FlagKind.Bool, false, "work in 'composed' mode, compare paths in all specs matching base and revision globs"),
    ];

    private static IEnumerable<FlagDef> CommonBreakingFlags() =>
    [
        Enum("lang", "l", Languages, "en", "language for localized output"),
        new("err-ignore", "", FlagKind.String, "", "configuration file for ignoring errors"),
        new("warn-ignore", "", FlagKind.String, "", "configuration file for ignoring warnings"),
        new("include-checks", "i", FlagKind.Strings, null, "deprecated: use --severity-levels") { Hidden = true },
        new("deprecation-days-beta", "", FlagKind.Uint, 0UL, "min days required between deprecating a beta resource and removing it"),
        new("deprecation-days-stable", "", FlagKind.Uint, 0UL, "min days required between deprecating a stable resource and removing it"),
        Enum("color", "", ["auto", "always", "never"], "auto", "when to colorize textual output"),
        Enum("format", "f", ChangelogFormats, "text", "output format"),
        new("severity-levels", "", FlagKind.String, "", "configuration file for custom severity levels"),
        new("attributes", "", FlagKind.Strings, null, "OpenAPI Extensions to include in json or yaml output"),
        new("template", "", FlagKind.String, "", "path to custom template file for changelog generation"),
        Enum("stability-level", "", ["draft", "alpha", "beta", "stable"], "", "minimum stability level to include"),
    ];

    private static IEnumerable<FlagDef> OpenFlags(string what) =>
    [
        new("open", "", FlagKind.Bool, false, $"after printing the {what}, encrypt the comparison and upload it to oasdiff.com, then open the side-by-side review in a browser"),
        new("review-token", "", FlagKind.String, "", "with --open, upload an authenticated review using this token instead of the free anonymous one") { Hidden = true },
        new("review-meta", "", FlagKind.Strings, null, "with --open and --review-token, attach repeatable key=value metadata to the authenticated review (opaque; not interpreted by the CLI)") { Hidden = true },
    ];

    private static readonly string[] ExcludeElements = DiffConfig.ExcludeDiffOptions;

    private static Command Root()
    {
        var root = new Command { Name = Name, Use = Name, Short = "OpenAPI specification diff" };
        void Add(Command c, string group) { c.Parent = root; c.Group = group; root.Sub.Add(c); }

        Add(new Command
        {
            Name = "breaking", Use = "breaking base revision [flags]", Short = "Display breaking changes",
            Long = "Display breaking changes between base and revision specs." + SpecHelp,
            Flags = [.. CommonDiffFlags(), .. CommonBreakingFlags(), Enum("fail-on", "o", ["ERR", "WARN"], "", "exit with return code 1 when output includes errors with this level or higher"), .. OpenFlags("breaking changes")],
            ValidateArgs = ValidateCompareArgs, Run = (f, c) => RunChangelog(f, c, Level.WARN, true),
        }, "compare");
        Add(new Command
        {
            Name = "changelog", Use = "changelog base revision [flags]", Short = "Display changelog",
            Long = "Display changes between base and revision specs." + SpecHelp,
            Flags = [.. CommonDiffFlags(), .. CommonBreakingFlags(), Enum("fail-on", "o", Levels, "", "exit with return code 1 when output includes errors with this level or higher"),
                Enum("level", "", Levels, "INFO", "output errors with this level or higher"), .. OpenFlags("changelog")],
            ValidateArgs = ValidateCompareArgs,
            Run = (f, c) =>
            {
                var lvl = EnumText.ParseLevel(f.Str("level"));
                if (lvl == Level.INVALID) throw new ReturnError($"invalid level value: {Fmt.GoQuote(f.Str("level"))}", 101);
                return RunChangelog(f, c, lvl, false);
            },
        }, "compare");
        Add(new Command
        {
            Name = "diff", Use = "diff base revision [flags]", Short = "Generate a diff report",
            Long = "Generate a diff report between base and revision specs." + SpecHelp,
            Flags = [.. CommonDiffFlags(), EnumSlice("exclude-elements", "e", ExcludeElements, "elements to exclude"), Enum("format", "f", DiffFormats, "yaml", "output format"),
                new("fail-on-diff", "o", FlagKind.Bool, false, "exit with return code 1 when any change is found")],
            ValidateArgs = ValidateCompareArgs, Run = RunDiff,
        }, "compare");
        Add(new Command
        {
            Name = "summary", Use = "summary base revision [flags]", Short = "Generate a diff summary",
            Long = "Display a summary of changes between base and revision specs." + SpecHelp,
            Flags = [.. CommonDiffFlags(), EnumSlice("exclude-elements", "e", ExcludeElements, "elements to exclude"), Enum("format", "f", SummaryFormats, "yaml", "output format"),
                new("fail-on-diff", "", FlagKind.Bool, false, "exit with return code 1 when any change is found")],
            ValidateArgs = ValidateCompareArgs, Run = RunSummary,
        }, "compare");

        var checks = new Command { Name = "checks", Use = "checks", Short = "Display checks", Long = "Display the rules oasdelta applies, one listing per rule set." };
        var checksChangelog = new Command
        {
            Name = "changelog", Use = "changelog", Short = "Display changelog and breaking-change checks",
            Long = "Display a list of all supported changelog and breaking-change checks.", Parent = checks,
            Flags =
            [
                Enum("format", "f", ChecksFormatter.Formats, "text", "output format"),
                EnumSlice("severity", "s", ["info", "warn", "error"], "include only checks with any of specified severities"),
                EnumSlice("tags", "t", ChangelogTags.All, "include only checks matching the tags: values of the same dimension are ORed, dimensions are ANDed"),
                Enum("lang", "l", Languages, "en", "language for localized output"),
                new("id", "", FlagKind.String, "", "display only the check with this id"),
                new("location", "", FlagKind.String, "", "include only checks with a location containing this string"),
            ],
            ValidateArgs = f =>
            {
                if (f.Args.Count > 0) throw new ReturnError($"unknown command {Fmt.GoQuote(f.Args[0])} for {Fmt.GoQuote(Name + " checks changelog")}", GeneralExecutionErr, true);
                if (f.Str("id") is { Length: > 0 } id && !Rules.All().Any(r => r.Id == id)) throw new ReturnError($"unknown check id {Fmt.GoQuote(id)}", GeneralExecutionErr, true);
            },
            Run = RunChecksChangelog,
        };
        checks.Sub.Add(checksChangelog);
        Add(checks, "reference");

        Add(new Command
        {
            Name = "schema", Use = "schema [flags]", Short = "Display the JSON Schema of the JSON output",
            Long = "Print a JSON Schema for the \"--format json\" output of the breaking and changelog commands.\nThe \"--format yaml\" output has the same structure.",
            ValidateArgs = f => { if (f.Args.Count > 0) throw new ReturnError($"unknown command {Fmt.GoQuote(f.Args[0])} for {Fmt.GoQuote(Name + " schema")}", GeneralExecutionErr, true); },
            Run = (_, c) => { c.Out.Write(SchemaJson.Text); c.Out.Write('\n'); return 0; },
        }, "reference");
        return root;
    }

    // ------------------------------------------------------------------ entry point

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        var ctx = new Context(stdout, stderr);
        var root = Root();
        if (args.Length == 0) { stdout.Write(RootHelp(root)); return 0; }
        if (args[0] is "-v" or "--version" || (args[0] is "version" && args.Length == 1)) { stdout.WriteLine($"{Name} version {Version}"); return 0; }
        if (args[0] is "-h" or "--help") { stdout.Write(RootHelp(root)); return 0; }
        if (args[0] == "help")
        {
            var target = args.Length > 1 ? Find(root, args.Skip(1).ToList(), out _) : null;
            if (target is null) stdout.Write(RootHelp(root)); else stdout.Write(Help(target));
            return 0;
        }

        var cmd = Find(root, args.ToList(), out int consumed);
        if (cmd is null)
        {
            stderr.WriteLine($"Error: unknown command {Fmt.GoQuote(args[0])} for {Fmt.GoQuote(Name)}");
            stderr.WriteLine($"Run '{Name} --help' for usage.");
            return GeneralExecutionErr;
        }

        var flags = new FlagValues([.. cmd.Flags, ConfigFlag]);
        try
        {
            flags.Parse(args.Skip(consumed).ToList());
        }
        catch (FlagException e)
        {
            stderr.WriteLine("Error: " + e.Message);
            stdout.Write(Usage(cmd));
            stdout.Write('\n');
            return GeneralExecutionErr;
        }
        if (flags.Help) { stdout.Write(Help(cmd)); return 0; }
        if (cmd.Run is null)
        {
            if (flags.Args.Count > 0)
            {
                stderr.WriteLine($"Error: unknown command {Fmt.GoQuote(flags.Args[0])} for {Fmt.GoQuote(cmd.Path)}");
                stderr.WriteLine($"Run '{cmd.Path} --help' for usage.");
                return GeneralExecutionErr;
            }
            stdout.Write(Help(cmd));
            return 0;
        }

        try
        {
            cmd.ValidateArgs?.Invoke(flags);
            ReadConfig(cmd, flags);
            return cmd.Run(flags, ctx);
        }
        catch (ReturnError e)
        {
            stderr.WriteLine("Error: " + e.Message);
            if (e.ShowUsage) { stdout.Write(Usage(cmd)); stdout.Write('\n'); }
            return e.Code;
        }
    }

    private static Command? Find(Command root, List<string> args, out int consumed)
    {
        int n = 0;
        var cur = root;
        while (n < args.Count)
        {
            var name = args[n];
            var next = cur.Sub.FirstOrDefault(s => s.Name == name);
            if (next is null) break;
            cur = next;
            n++;
        }
        consumed = n;
        return cur == root ? null : cur;
    }

    private static void ValidateCompareArgs(FlagValues f)
    {
        if (f.Args.Count < 2)
            throw new ReturnError("please specify base and revision arguments as a path to a file, a glob (in composed mode), a URL, a git ref (e.g. main:openapi.yaml), or '-' to read standard input", GeneralExecutionErr, true);
        if (f.Args.Count > 2) throw new ReturnError("invalid arguments after base and revision", GeneralExecutionErr, true);
        if (f.Bool("composed") && (f.Args[0] == "-" || f.Args[1] == "-")) throw new ReturnError("can't read from stdin in composed mode", GeneralExecutionErr, true);
        if (!f.Bool("open") && (f.Changed("review-token") || f.Changed("review-meta"))) throw new ReturnError("--review-token and --review-meta require --open", GeneralExecutionErr, true);
        if (f.Changed("color") && f.Str("format") is not ("text" or "singleline"))
            throw new ReturnError("--color flag is only relevant with 'text' or 'singleline' formats", GeneralExecutionErr, true);
        if (f.Str("template").Length > 0 && Array.IndexOf(AllFormats, f.Str("format")) >= 0 && Array.IndexOf(TemplateFormats, f.Str("format")) < 0)
            throw new ReturnError($"template flag is not supported for format {Fmt.GoQuote(f.Str("format"))}. Supported formats for templates are: {string.Join(", ", TemplateFormats)}", 111, true);
    }

    // ------------------------------------------------------------------ config file (viper)

    private static readonly HashSet<string> ConfigKeys = new(StringComparer.Ordinal)
    {
        "attributes", "composed", "flatten-allof", "flatten-params", "case-insensitive-headers", "deprecation-days-beta", "deprecation-days-stable", "lang", "color",
        "warn-ignore", "err-ignore", "format", "fail-on", "level", "fail-on-diff", "severity-levels", "stability-level", "exclude-elements", "exclude-extensions",
        "severity", "tags", "match-path", "unmatch-path", "filter-extension", "prefix-base", "prefix-revision", "strip-prefix-base", "strip-prefix-revision",
        "include-path-params", "match-inline-refs", "allow-external-refs", "auto-upgrade", "fetch", "template",
    };

    private static readonly string[] ConfigRelativePathKeys = ["err-ignore", "warn-ignore", "severity-levels", "template"];
    private static readonly string[] ConfigExtensions = ["json", "yaml", "yml"];

    private static void ReadConfig(Command cmd, FlagValues flags)
    {
        string? file = flags.Str("config") is { Length: > 0 } p ? p
            : Environment.GetEnvironmentVariable("OASDELTA_CONFIG") is { Length: > 0 } e1 ? e1
            : Environment.GetEnvironmentVariable("OASDIFF_CONFIG") is { Length: > 0 } e2 ? e2 : null;
        if (file is null)
        {
            foreach (var name in new[] { ".oasdelta", "oasdelta", ".oasdiff", "oasdiff" })
            {
                file = ConfigExtensions.Select(ext => $"{name}.{ext}").FirstOrDefault(File.Exists);
                if (file is not null) break;
            }
            if (file is null) return;
        }
        Node? root;
        try
        {
            var text = File.ReadAllText(file);
            root = NodeReader.Parse(text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ReturnError($"failed to load config file: read error: open {file}: no such file or directory", 107, true);
        }
        catch (YamlParseException ex)
        {
            throw new ReturnError($"failed to load config file: read error: While parsing config: {ex.Message}", 107, true);
        }
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (root is { IsMapping: true })
            foreach (var (k, v) in root.Pairs!) values[k.KeyString] = v.ToValue();
        var invalid = values.Keys.Where(k => !ConfigKeys.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (invalid.Count > 0)
            throw new ReturnError("failed to load config file: validation error: decoding failed due to the following error(s):\n\n'internal.Config' has invalid keys: " + string.Join(", ", invalid), 107, true);

        string S(string k) => values.TryGetValue(k, out var v) && v is not null ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "" : "";
        List<string> L(string k) => values.TryGetValue(k, out var v) ? v switch { List<object?> l => l.Select(x => x?.ToString() ?? "").ToList(), string s => [s], _ => [] } : [];
        void Check(string[] allowed, string value, string name)
        {
            if (value.Length > 0 && Array.IndexOf(allowed, value) < 0)
                throw new ReturnError($"failed to load config file: invalid {name} {Fmt.GoQuote(value)}, allowed values: {string.Join(", ", allowed)}", 107, true);
        }
        Check(["en", "es", "pt-br", "ru"], S("lang"), "lang");
        Check(["auto", "always", "never"], S("color"), "color");
        Check(AllFormats, S("format"), "format");
        Check(Levels, S("fail-on"), "fail-on");
        Check(Levels, S("level"), "level");
        foreach (var v in L("exclude-elements")) Check(ExcludeElements, v, "exclude-elements");
        foreach (var v in L("severity")) Check(["error", "warn", "info"], v, "severity");
        foreach (var v in L("tags")) Check(ChangelogTags.All, v, "tags");

        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(file)) ?? "";
        var configDir = System.IO.Path.GetDirectoryName(file) ?? "";
        foreach (var (k, v) in values)
        {
            object? value = v switch { List<object?> l => l.Select(x => x?.ToString() ?? "").ToList(), _ => v };
            // file paths in a config file are relative to the config file
            if (Array.IndexOf(ConfigRelativePathKeys, k) >= 0 && value is string s && s.Length > 0 && !System.IO.Path.IsPathRooted(s))
                value = configDir.Length == 0 ? s : SpecLoader.CleanPath(configDir + "/" + s);
            flags.SetConfig(k, value);
        }
        _ = dir;
    }

    // ------------------------------------------------------------------ compare commands

    private sealed class DiffResult
    {
        public OpenApiDiff? Diff;
        public Sources Sources = new();
        public string BaseVersion = "n/a", RevisionVersion = "n/a";
    }

    private static DiffConfig ToConfig(FlagValues f)
    {
        var c = new DiffConfig
        {
            MatchPath = f.Str("match-path"), UnmatchPath = f.Str("unmatch-path"), FilterExtension = f.Str("filter-extension"),
            PathPrefixBase = f.Str("prefix-base"), PathPrefixRevision = f.Str("prefix-revision"),
            PathStripPrefixBase = f.Str("strip-prefix-base"), PathStripPrefixRevision = f.Str("strip-prefix-revision"),
            IncludePathParams = f.Bool("include-path-params"), MatchInlineRefs = f.Bool("match-inline-refs"),
        };
        foreach (var e in f.Strings("exclude-elements")) if (e.Length > 0) c.ExcludeElements.Add(e);
        foreach (var e in f.Strings("exclude-extensions")) if (e.Length > 0) c.ExcludeExtensions.Add(e);
        return c;
    }

    private static void RequireSupported(FlagValues f)
    {
        if (f.Bool("flatten-allof") || f.Bool("flatten"))
            throw new ReturnError("--flatten-allof is not supported by oasdelta yet", 101);
        if (f.Bool("auto-upgrade")) throw new ReturnError("--auto-upgrade is not supported by oasdelta yet", 101);
        if (f.Bool("open")) throw new ReturnError("--open is not supported by oasdelta", 101);
    }

    private static void ApplyOptions(FlagValues f, OpenApiDoc spec)
    {
        if (f.Bool("flatten-params")) SpecOptions.FlattenParams(spec);
        if (f.Bool("case-insensitive-headers")) SpecOptions.LowercaseHeaders(spec);
    }

    private static DiffResult CalcDiff(FlagValues f)
    {
        RequireSupported(f);
        var opts = new LoaderOptions { IncludeOrigin = true, AllowExternalRefs = f.Bool("allow-external-refs") };
        var config = ToConfig(f);
        var result = new DiffResult();
        if (f.Bool("composed"))
        {
            List<(OpenApiDoc, string)> LoadGlob(string what, string glob)
            {
                var files = Glob.Expand(glob);
                var specs = new List<(OpenApiDoc, string)>();
                foreach (var file in files)
                {
                    OpenApiDoc spec;
                    try { spec = new SpecLoader(opts).LoadFile(file); }
                    catch (LoadException e) { throw SpecsLoadError(what, glob, $"failed to load {Fmt.GoQuote(file)}: {e.Message}"); }
                    catch (YamlParseException e) { throw SpecsLoadError(what, glob, $"failed to load {Fmt.GoQuote(file)}: {e.Message}"); }
                    ApplyOptions(f, spec);
                    specs.Add((spec, file));
                }
                if (specs.Count == 0)
                    throw SpecsLoadError(what, glob, Uri.TryCreate(glob, UriKind.Absolute, out var u) && u.Scheme.Length > 1 ? "no matching files (should be a glob, not a URL)" : "no matching files");
                return specs;
            }
            var b = LoadGlob("base", f.Args[0]);
            var r = LoadGlob("revision", f.Args[1]);
            try
            {
                var (d, sources) = OpenApiDiff.GetPathsDiff(config, b, r);
                result.Diff = d;
                foreach (var (k, v) in sources) result.Sources.Map[k] = v;
            }
            catch (Exception e) when (e is DiffException or DuplicateEndpointException) { throw new ReturnError("diff failed: " + e.Message, 104); }
            return result;
        }

        var baseSrc = SpecSource.Of(f.Args[0]); baseSrc.Fetch = f.Bool("fetch");
        var revSrc = SpecSource.Of(f.Args[1]); revSrc.Fetch = f.Bool("fetch");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var s1 = LoadSpec("base", baseSrc, opts);
        Timing("load base", sw);
        ApplyOptions(f, s1);
        OpenApiDoc s2;
        if (baseSrc.IsStdin && revSrc.IsStdin) s2 = s1;
        else { s2 = LoadSpec("revision", revSrc, opts); ApplyOptions(f, s2); }
        Timing("load revision", sw);
        try { result.Diff = OpenApiDiff.Get(config, s1, s2); }
        catch (Exception e) when (e is DiffException or DuplicateEndpointException) { throw new ReturnError("diff failed: " + e.Message, 104); }
        Timing("diff", sw);
        foreach (var (k, v) in OpenApiDiff.OperationSources(s1, baseSrc.DisplayPath)) result.Sources.Map[k] = v;
        foreach (var (k, v) in OpenApiDiff.OperationSources(s2, revSrc.DisplayPath)) result.Sources.Map[k] = v;
        Timing("sources", sw);
        result.BaseVersion = s1.Info?.Version is { Length: > 0 } bv ? bv : "n/a";
        result.RevisionVersion = s2.Info?.Version is { Length: > 0 } rv ? rv : "n/a";
        return result;
    }

    private static readonly bool ShowTiming = Environment.GetEnvironmentVariable("OASDELTA_TIMING") is { Length: > 0 };

    private static void Timing(string phase, System.Diagnostics.Stopwatch sw)
    {
        if (!ShowTiming) return;
        Console.Error.WriteLine($"[timing] {phase}: {sw.Elapsed.TotalMilliseconds:F1} ms, allocated so far {GC.GetTotalAllocatedBytes() / 1048576} MB, gcs {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}");
        sw.Restart();
    }

    private static ReturnError SpecsLoadError(string what, string glob, string msg) =>
        new($"failed to load {what} specs from glob {Fmt.GoQuote(glob)}: {msg}", msg.Contains("disallowed external reference", StringComparison.Ordinal) ? 123 : 103);

    private static OpenApiDoc LoadSpec(string what, SpecSource src, LoaderOptions opts)
    {
        try { return src.Load(new SpecLoader(opts)); }
        catch (Exception e) when (e is LoadException or YamlParseException)
        {
            var msg = $"failed to load {what} spec from {src.Out}: {e.Message}";
            throw new ReturnError(msg, e.Message.Contains("disallowed external reference", StringComparison.Ordinal) ? 123 : 102);
        }
    }

    private static int RunDiff(FlagValues f, Context c)
    {
        var format = f.Str("format");
        if (format == "json") f.Override("exclude-elements", (List<string>)[.. f.Strings("exclude-elements").Where(x => x.Length > 0), DiffConfig.ExcludeEndpointsOption]);
        var r = CalcDiff(f);
        string text = format switch
        {
            "json" => r.Diff is null ? "{}" : JsonEmitter.Emit(r.Diff),
            "yaml" => r.Diff is null ? "{}" : YamlEmitter.Emit(r.Diff).TrimEnd('\n') + "\n",
            "html" => HtmlReport.Render(DiffReport.Text(r.Diff)),
            "text" or "markup" or "markdown" => DiffReport.Text(r.Diff),
            _ => throw new ReturnError($"format {Fmt.GoQuote(format)} is not supported by \"diff\"", 110),
        };
        c.Out.Write(text);
        c.Out.Write('\n');
        return f.Bool("fail-on-diff") && r.Diff is { Empty: false } ? 1 : 0;
    }

    private static int RunSummary(FlagValues f, Context c)
    {
        var format = f.Str("format");
        var r = CalcDiff(f);
        object summary = r.Diff?.GetSummary() ?? new Summary();
        string text = format switch
        {
            "json" => JsonEmitter.Emit(summary),
            "yaml" => YamlEmitter.Emit(summary).TrimEnd('\n') + "\n",
            _ => throw new ReturnError($"format {Fmt.GoQuote(format)} is not supported by \"summary\"", 110),
        };
        c.Out.Write(text);
        c.Out.Write('\n');
        return f.Bool("fail-on-diff") && r.Diff is { Empty: false } ? 1 : 0;
    }

    private static int RunChangelog(FlagValues f, Context c, Level level, bool isBreaking)
    {
        var r = CalcDiff(f);

        Dictionary<string, Level>? severity = null;
        if (f.Str("severity-levels") is { Length: > 0 } sevFile)
        {
            try { severity = Rules.ParseSeverityLevels(File.ReadAllText(sevFile)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { throw new ReturnError($"failed to load custom severity levels from {sevFile}: open {sevFile}: no such file or directory", 106); }
            catch (FormatException e) { throw new ReturnError($"failed to load custom severity levels from {sevFile}: {e.Message}", 106); }
        }

        var cc = new CheckerConfig
        {
            MinSunsetBetaDays = f.Uint("deprecation-days-beta"), MinSunsetStableDays = f.Uint("deprecation-days-stable"),
            Attributes = f.Strings("attributes").Where(a => a.Length > 0).ToList(),
        };
        if (severity is not null) foreach (var (id, lv) in severity) cc.SetLevel(id, lv);
        cc.SetStabilityLevel(f.Str("stability-level"));

        var l = new Localizer(f.Str("lang"));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var errs = BackwardCompatibility.Check(cc, r.Diff, r.Sources, level);
        Timing("checks", sw);
        errs = FilterIgnored(errs, f.Str("warn-ignore"), Level.WARN, "warn", l);
        errs = FilterIgnored(errs, f.Str("err-ignore"), Level.ERR, "err", l);

        var format = f.Str("format");
        var color = f.Str("color") switch { "always" => ColorMode.Always, "never" => ColorMode.Never, _ => ColorMode.Auto };
        var opts = new RenderOpts
        {
            ColorMode = color, TemplatePath = f.Str("template"), DiffEmpty = r.Diff is null or { Empty: true }, IsBreaking = isBreaking,
            BaseVersion = r.BaseVersion, RevisionVersion = r.RevisionVersion,
        };
        string text;
        try { text = ChangelogRenderer.Render(format, errs, l, opts); }
        catch (Exception e) when (e is IOException or TemplateException)
        { throw new ReturnError($"failed to print {Fmt.GoQuote("changelog " + format)}: {e.Message}", 105); }
        Timing("render", sw);
        c.Out.Write(text);
        c.Out.Write('\n');

        if (f.Str("fail-on") is { Length: > 0 } failOn)
        {
            var fl = EnumText.ParseLevel(failOn);
            if (fl == Level.INVALID) throw new ReturnError($"invalid fail-on value {Fmt.GoQuote(failOn)}", 101);
            return errs.Any(e => e.Level >= fl) ? 1 : 0;
        }
        return 0;
    }

    private static List<Change> FilterIgnored(List<Change> errs, string file, Level level, string what, Localizer l)
    {
        if (file.Length == 0) return errs;
        try { return BackwardCompatibility.ProcessIgnored(level, errs, file, l); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { throw new ReturnError($"can't process {what} ignore file: open {file}: no such file or directory", 121); }
    }

    // ------------------------------------------------------------------ checks

    private static int RunChecksChangelog(FlagValues f, Context c)
    {
        var l = new Localizer(f.Str("lang"));
        string id = f.Str("id"), location = f.Str("location");
        var severity = f.Strings("severity").Where(s => s.Length > 0).ToList();
        var tags = f.Strings("tags").Where(s => s.Length > 0).ToList();
        var list = new List<CheckOut>();
        foreach (var rule in Rules.All())
        {
            if (id.Length > 0 && rule.Id != id) continue;
            if (location.Length > 0 && !rule.Locations.Any(x => x.Contains(location, StringComparison.Ordinal))) continue;
            if (severity.Count > 0 && !(rule.Level switch
                {
                    Level.ERR => severity.Contains("error"), Level.WARN => severity.Contains("warn"), Level.INFO => severity.Contains("info"), _ => true,
                })) continue;
            if (!ChangelogTags.Match(tags, rule)) continue;
            list.Add(CheckOut.From(rule, l));
        }
        list.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        c.Out.Write(ChecksFormatter.Render(f.Str("format"), list, l));
        c.Out.Write('\n');
        return 0;
    }

    // ------------------------------------------------------------------ help (cobra templates)

    private static string UseLine(Command cmd)
    {
        var parentPath = cmd.Parent is null ? "" : cmd.Parent.Path + " ";
        var use = parentPath + cmd.Use;
        if (cmd.Flags.Count > 0 && !use.Contains("[flags]", StringComparison.Ordinal)) use += " [flags]";
        return use;
    }

    private static string Usage(Command cmd)
    {
        var sb = new StringBuilder("Usage:");
        if (cmd.Run is not null) sb.Append("\n  ").Append(UseLine(cmd));
        if (cmd.Sub.Count > 0) sb.Append("\n  ").Append(cmd.Path).Append(" [command]");
        if (cmd.Sub.Count > 0)
        {
            sb.Append("\n\nAvailable Commands:");
            int pad = Math.Max(11, cmd.Sub.Max(s => s.Name.Length) + 1);
            foreach (var s in cmd.Sub.OrderBy(s => s.Name, StringComparer.Ordinal)) sb.Append("\n  ").Append(s.Name.PadRight(pad)).Append(s.Short);
        }
        var help = new FlagDef("help", "h", FlagKind.Bool, false, "help for " + cmd.Name);
        sb.Append("\n\nFlags:\n").Append(FlagValues.Usages([.. cmd.Flags, help]).TrimEnd());
        sb.Append("\n\nGlobal Flags:\n").Append(FlagValues.Usages([ConfigFlag]).TrimEnd());
        if (cmd.Sub.Count > 0) sb.Append($"\n\nUse \"{cmd.Path} [command] --help\" for more information about a command.");
        return sb.Append('\n').ToString();
    }

    private static string Help(Command cmd)
    {
        var text = cmd.Long.Length > 0 ? cmd.Long : cmd.Short;
        return text.TrimEnd() + "\n\n" + Usage(cmd);
    }

    private static string RootHelp(Command root)
    {
        var sb = new StringBuilder($"OpenAPI specification diff (a .NET port of oasdiff)\n\nUsage:\n  {Name} [command]\n");
        void Group(string title, string id)
        {
            var cmds = root.Sub.Where(s => s.Group == id).ToList();
            if (cmds.Count == 0) return;
            sb.Append('\n').Append(title).Append('\n');
            foreach (var s in cmds) sb.Append("  ").Append(s.Name.PadRight(15)).Append(' ').Append(s.Short).Append('\n');
        }
        Group("Compare two specs:", "compare");
        Group("Reference:", "reference");
        sb.Append("\nAdditional Commands:\n  help            Help about any command\n");
        sb.Append("\nFlags:\n      --config string   " + ConfigFlag.Usage + "\n");
        sb.Append($"  -h, --help            help for {Name}\n  -v, --version         version for {Name}\n");
        sb.Append($"\nUse \"{Name} [command] --help\" for more information about a command.\n");
        return sb.ToString();
    }
}

/// <summary>internal/checks_changelog.go tag dimensions: values within a dimension are ORed, dimensions are ANDed.</summary>
public static class ChangelogTags
{
    private static readonly (string[] values, Func<string, Rule, bool> match)[] Dimensions =
    [
        (["request", "response"], (v, r) => v == r.Direction.Name()),
        (["add", "remove", "set", "unset", "change", "increase", "decrease"], (v, r) => CheckOut.RuleActions(r).Contains(v)),
        (["widens", "narrows"], (v, r) => v == r.Effect.Name()),
        (["schema", "parameters", "requestBody", "responses", "paths", "headers", "security", "tags", "components"], (v, r) => v == r.Area.Name()),
        (["existence", "requiredness", "mutability", "type", "constraints", "values", "structure", "lifecycle"], (v, r) => v == r.Kind.Name()),
        (["read-only", "write-only", "sanctioned", "non-success", "has-default", "negotiated", "required-in-requests"], (v, r) => r.Guards.Any(g => g.Name() == v)),
        (["generated", "hand-written"], (v, r) => (v == "generated") == r.Generated),
    ];

    public static readonly string[] All = Dimensions.SelectMany(d => d.values).ToArray();

    public static bool Match(List<string> tags, Rule rule)
    {
        foreach (var (values, match) in Dimensions)
        {
            bool requested = false, matched = false;
            foreach (var t in tags)
            {
                if (Array.IndexOf(values, t) < 0) continue;
                requested = true;
                if (match(t, rule)) { matched = true; break; }
            }
            if (requested && !matched) return false;
        }
        return true;
    }
}
