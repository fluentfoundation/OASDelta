using System.Text;
using OasDelta.Checker;
using OasDelta.Yaml;

namespace OasDelta.Formatters;

/// <summary>formatters.Check: one rule as listed by "oasdiff checks changelog".</summary>
public sealed class CheckOut : IOutput
{
    public string Id = "", Level = "", Direction = "", Area = "", Kind = "", Effect = "", Description = "", Mitigation = "";
    public List<string> Actions = [], Guards = [], Locations = [];
    public bool Generated;

    public object? ToOutput()
    {
        var m = new OMap { { "id", Id } };
        m.OptStr("level", Level); m.OptStr("direction", Direction); m.OptStr("area", Area); m.OptStr("kind", Kind);
        m.OptList("actions", Actions); m.OptStr("effect", Effect); m.OptList("guards", Guards); m.OptList("locations", Locations);
        m.OptTrue("generated", Generated); m.OptStr("description", Description); m.OptStr("mitigation", Mitigation);
        return m;
    }

    public static CheckOut From(Rule r, Localizer l)
    {
        var mitigationKey = r.Id + "-comment";
        var mitigation = l.Get(mitigationKey, []);
        return new CheckOut
        {
            Id = r.Id, Level = r.Level.Name(), Direction = r.Direction.Name(), Area = r.Area.Name(), Kind = r.Kind.Name(), Effect = r.Effect.Name(),
            Actions = RuleActions(r), Guards = r.Guards.Select(g => g.Name()).ToList(), Locations = [.. r.Locations], Generated = r.Generated,
            Description = l.Get(r.Description, []), Mitigation = mitigation == mitigationKey ? "" : mitigation,
        };
    }

    /// <summary>rules.Rule.Actions: the distinct actions named by the rule's location claims, sorted.</summary>
    public static List<string> RuleActions(Rule r)
    {
        var set = new List<string>();
        foreach (var loc in r.Locations)
        {
            int colon = loc.IndexOf(':');
            if (colon <= 0 || colon == loc.Length - 1) continue;
            var acts = loc[(colon + 1)..].Split(',');
            if (acts.Any(a => a is not ("add" or "remove" or "set" or "unset" or "change" or "increase" or "decrease"))) continue;
            foreach (var a in acts) if (!set.Contains(a)) set.Add(a);
        }
        set.Sort(StringComparer.Ordinal);
        return set;
    }
}

public static class ChecksFormatter
{
    public static readonly string[] Formats = ["json", "text", "yaml"];

    public static string Render(string format, List<CheckOut> checks, Localizer l) => format switch
    {
        "json" => checks.Count == 0 ? "[]" : JsonEmitter.Emit(checks),
        "yaml" => checks.Count == 0 ? "[]\n" : YamlEmitter.Emit(checks).TrimEnd('\n') + "\n",
        _ => Text(checks, l),
    };

    /// <summary>text/tabwriter with minwidth 1, tabwidth 1, padding 1, space padding; the description column is left ragged.</summary>
    private static string Text(List<CheckOut> checks, Localizer l)
    {
        var rows = new List<string[]> { new[] { "ID", "LEVEL", "GUARDS", "GENERATED", "DESCRIPTION" } };
        foreach (var c in checks) rows.Add([c.Id, c.Level, string.Join(",", c.Guards), c.Generated ? "yes" : "", l.Get(c.Description, [])]);
        var widths = new int[4];
        foreach (var r in rows) for (int i = 0; i < 4; i++) widths[i] = Math.Max(widths[i], Math.Max(1, r[i].Length) + 1);
        var sb = new StringBuilder();
        foreach (var r in rows)
        {
            for (int i = 0; i < 4; i++) sb.Append(r[i]).Append(' ', widths[i] - r[i].Length);
            sb.Append(r[4]).Append('\n');
        }
        return sb.ToString();
    }
}
