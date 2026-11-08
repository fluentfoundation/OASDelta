using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace OasDelta.Load;

public enum SourceType : byte { Stdin, Url, File, GitRevision }

/// <summary>load.Source: where a spec comes from (file, URL, stdin or a git revision "ref:path").</summary>
public sealed class SpecSource
{
    public string Path = "";
    public SourceType Type;
    public bool Fetch;

    public static SpecSource Of(string path)
    {
        if (path == "-") return new SpecSource { Path = "stdin", Type = SourceType.Stdin };
        if (IsGitRevision(path)) return new SpecSource { Path = path, Type = SourceType.GitRevision };
        if (IsUrl(path)) return new SpecSource { Path = path, Type = SourceType.Url };
        return new SpecSource { Path = path, Type = SourceType.File };
    }

    public static bool IsGitRevision(string path)
    {
        int idx = path.IndexOf(':');
        if (idx < 0 || path.Contains("://", StringComparison.Ordinal)) return false;
        if (idx == 1 && path[0] is >= 'A' and <= 'Z') return false; // Windows drive letter
        return true;
    }

    private static bool IsUrl(string s) =>
        (s.StartsWith("http://", StringComparison.Ordinal) || s.StartsWith("https://", StringComparison.Ordinal)) && Uri.TryCreate(s, UriKind.Absolute, out _);

    public bool IsStdin => Type == SourceType.Stdin;

    /// <summary>Quoted for error messages, except stdin.</summary>
    public string Out => IsStdin ? Path : Checker.Fmt.GoQuote(Path);

    public string DisplayPath => Type != SourceType.GitRevision ? Path : Path[(Path.IndexOf(':') + 1)..];

    public Model.OpenApiDoc Load(SpecLoader loader) => Type switch
    {
        SourceType.Stdin => loader.LoadFile("-"),
        SourceType.GitRevision => loader.LoadGitRevision(Path, Fetch),
        _ => loader.LoadFile(Path),
    };
}

/// <summary>git plumbing used to read specs from revisions (load/git.go).</summary>
public static class GitSource
{
    public static string ReadRefContent(string gitRef, bool fetch)
    {
        if (BlobHashFromRef(gitRef) is { } hex) return Show(hex);
        try { return Show(gitRef); }
        catch (LoadException e)
        {
            if (fetch)
            {
                var r = RefBeforeColon(gitRef);
                if (!CommitExistsLocally(r))
                {
                    var (code, _, err) = Git("fetch", "--end-of-options", "origin", r);
                    if (code != 0) throw new LoadException($"failed to fetch git revision {Checker.Fmt.GoQuote(r)} from origin: {err.Trim()}\n\noriginal error: {e.Message}");
                    return Show(gitRef);
                }
            }
            throw HintMissingObject(gitRef, e);
        }
    }

    private static LoadException HintMissingObject(string gitRef, LoadException e)
    {
        if (e.Message.Contains("is git installed", StringComparison.Ordinal)) return e;
        var r = RefBeforeColon(gitRef);
        if (CommitExistsLocally(r)) return e;
        var q = Checker.Fmt.GoQuote(r);
        return new LoadException($"{e.Message}\n\noasdelta reads git revisions from local objects only and does not modify your repository.\n" +
            $"Commit {q} is not in this clone. Fetch it yourself with:\n\n    git fetch origin {r}\n\n" +
            "then re-run oasdelta, or re-run with --fetch to let oasdelta fetch it for you");
    }

    public static string Show(string gitRef)
    {
        CheckRef(gitRef);
        var (code, stdout, stderr) = Git("show", "--end-of-options", gitRef);
        if (code < 0) throw new LoadException("is git installed and in PATH?: " + stderr);
        if (code != 0) throw new LoadException(stderr.Trim().Length > 0 ? stderr.Trim() : $"exit status {code}");
        return stdout;
    }

    private static void CheckRef(string r)
    {
        if (r.StartsWith('-')) throw new LoadException($"git revision must not start with '-': {Checker.Fmt.GoQuote(r)}");
    }

    private static string RefBeforeColon(string gitRef) { int i = gitRef.IndexOf(':'); return i < 0 ? gitRef : gitRef[..i]; }

    private static bool CommitExistsLocally(string r) => !r.StartsWith('-') && Git("cat-file", "-e", "--end-of-options", r).code == 0;

    private static string? BlobHashFromRef(string gitRef)
    {
        int i = gitRef.IndexOf(':');
        if (i < 0) return null;
        var r = gitRef[..i];
        if (r.StartsWith('-')) return null;
        var (code, stdout, _) = Git("cat-file", "-t", "--end-of-options", r);
        return code == 0 && stdout.Trim() == "blob" ? r : null;
    }

    private static (int code, string stdout, string stderr) Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var errTask = p.StandardError.ReadToEndAsync();
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, output, errTask.Result);
        }
        catch (System.ComponentModel.Win32Exception e) { return (-1, "", e.Message); }
    }
}

/// <summary>github.com/yargevad/filepathx Glob: the pattern is split on "**"; each piece is matched with filepath.Glob
/// against every hit so far, and every hit is expanded with filepath.Walk (so a matching directory brings in its tree).</summary>
public static class Glob
{
    public static List<string> Expand(string pattern)
    {
        var matches = new List<string> { "" };
        foreach (var piece in pattern.Split("**"))
        {
            var hits = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in matches)
                foreach (var path in FilepathGlob(m + piece))
                    WalkTree(path, p => { if (seen.Add(p)) hits.Add(p); });
            matches = hits;
        }
        return matches;
    }

    private static void WalkTree(string root, Action<string> visit)
    {
        visit(root);
        if (!Directory.Exists(root)) return;
        foreach (var name in SortedNames(root)) WalkTree(JoinClean(root, name), visit);
    }

    private static string JoinClean(string dir, string name) => SpecLoader.CleanPath(dir + "/" + name);

    /// <summary>Go's filepath.Match on Windows: '\' is a path separator, not an escape character.</summary>
    private static readonly bool Windows = OperatingSystem.IsWindows();

    private static bool HasMeta(string s) => Windows ? s.IndexOfAny(['*', '?', '[']) >= 0 : s.IndexOfAny(['*', '?', '[', '\\']) >= 0;

    private static int LastSeparator(string s) => Windows ? s.LastIndexOfAny(['/', '\\']) : s.LastIndexOf('/');

    /// <summary>Go's cleanGlobPath: drop the trailing separator except on a root ("/", or "C:\" on Windows).</summary>
    private static string CleanGlobDir(string dir)
    {
        if (dir.Length == 0) return ".";
        if (dir is "/" || (Windows && (dir is "\\" || (dir.Length == 3 && dir[1] == ':')))) return dir;
        return dir[..^1];
    }

    private static List<string> SortedNames(string dir)
    {
        try
        {
            var names = Directory.EnumerateFileSystemEntries(dir).Select(System.IO.Path.GetFileName).Where(n => n is not null).Select(n => n!).ToList();
            names.Sort(StringComparer.Ordinal);
            return names;
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    /// <summary>Go filepath.Glob.</summary>
    private static List<string> FilepathGlob(string pattern)
    {
        if (!HasMeta(pattern)) return File.Exists(pattern) || Directory.Exists(pattern) ? [pattern] : [];
        int slash = LastSeparator(pattern);
        string dir = slash < 0 ? "" : pattern[..(slash + 1)], file = pattern[(slash + 1)..];
        dir = CleanGlobDir(dir);
        var result = new List<string>();
        if (!HasMeta(dir)) { GlobDir(dir, file, result); return result; }
        if (dir == pattern) return [];
        foreach (var d in FilepathGlob(dir)) GlobDir(d, file, result);
        return result;
    }

    private static void GlobDir(string dir, string pattern, List<string> result)
    {
        if (!Directory.Exists(dir)) return;
        var re = ToRegex(pattern);
        foreach (var n in SortedNames(dir))
            if (re.IsMatch(n)) result.Add(SpecLoader.CleanPath(dir + "/" + n));
    }

    /// <summary>path.Match syntax: *, ?, [class], \ escapes; '*' never crosses '/'.</summary>
    private static Regex ToRegex(string part)
    {
        var sb = new StringBuilder("^");
        for (int i = 0; i < part.Length; i++)
        {
            char c = part[i];
            switch (c)
            {
                case '*': sb.Append(Windows ? @"[^/\\]*" : "[^/]*"); break;
                case '?': sb.Append(Windows ? @"[^/\\]" : "[^/]"); break;
                case '\\' when !Windows && i + 1 < part.Length: sb.Append(Regex.Escape(part[++i].ToString())); break;
                case '[':
                    int j = part.IndexOf(']', i + 1);
                    if (j < 0) { sb.Append("\\["); break; }
                    var cls = part[(i + 1)..j].Replace("\\", "\\\\");
                    if (cls.StartsWith('^')) cls = "^" + cls[1..];
                    sb.Append('[').Append(cls).Append(']');
                    i = j;
                    break;
                default: sb.Append(Regex.Escape(c.ToString())); break;
            }
        }
        return new Regex(sb.Append('$').ToString(), RegexOptions.CultureInvariant);
    }
}
