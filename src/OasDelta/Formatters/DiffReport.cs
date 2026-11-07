using System.Text;
using OasDelta.Diff;
using OasDelta.Yaml;

namespace OasDelta.Formatters;

/// <summary>Port of oasdiff/report: the human-readable diff report (text / markdown; HTML is rendered from it).</summary>
public sealed class DiffReport
{
    private readonly StringBuilder _sb;
    private readonly int _level;

    private DiffReport(StringBuilder sb, int level) { _sb = sb; _level = level; }

    public static string Text(OpenApiDiff? d)
    {
        var r = new DiffReport(new StringBuilder(), 0);
        r.Output(d);
        return r._sb.ToString();
    }

    private DiffReport Indent() => new(_sb, _level + 1);

    // fmt.Fprintln: operands formatted with %v and always separated by one space
    private void Print(params object?[] output)
    {
        bool first = true;
        if (_level == 1) { _sb.Append('-'); first = false; }
        else if (_level > 1) { _sb.Append(' ', 2 * (_level - 1)).Append('-'); first = false; }
        foreach (var o in output)
        {
            if (!first) _sb.Append(' ');
            first = false;
            _sb.Append(Values.GoV(o));
        }
        _sb.Append('\n');
    }

    private void Cond(bool b, params object?[] output) { if (b) Print(output); }
    private void Message(IDiff? d, string text) => Cond(d is { Empty: false }, text);

    private static object Quote(object? v) => v switch { null => "null", string s => "'" + s + "'", _ => v };

    private void Value(ValueDiff? d, string title) { if (d is not null) Print(title, "changed from", Quote(d.From), "to", Quote(d.To)); }

    private void Strings(StringsDiff? d, string title)
    {
        if (d is not { Empty: false }) return;
        Print(title, "changed from", Quote(string.Join(", ", d.Deleted)), "to", Quote(string.Join(", ", d.Added)));
    }

    private static List<string> Sorted(List<string> l) { var c = new List<string>(l); c.Sort(StringComparer.Ordinal); return c; }

    private void Output(OpenApiDiff? d)
    {
        if (d is null || d.Empty) { Print("No changes"); return; }
        if (d.EndpointsDiff is not { Empty: false }) Print("No endpoint changes, but there are some other changes");
        else Endpoints(d.EndpointsDiff);
        if (d.ExtensionsDiff is not { Empty: false } && d.SecurityDiff is not { Empty: false } && d.ServersDiff is not { Empty: false }) return;
        Print("Other Changes");
        Print("-------------");
        if (d.ExtensionsDiff is { Empty: false })
        {
            Print("Extensions changed");
            Indent().Extensions(d.ExtensionsDiff);
            Print("");
        }
        Value(d.OpenAPIDiff, "Version");
        if (d.SecurityDiff is { Empty: false })
        {
            Print("Security Requirements changed");
            Indent().SecurityRequirements(d.SecurityDiff);
            Print("");
        }
        if (d.ServersDiff is { Empty: false })
        {
            Print("Servers changed");
            Indent().Servers(d.ServersDiff);
            Print("");
        }
    }

    private void Title(string title, int count)
    {
        var text = count == 0 ? $"### {title}: None" : $"### {title}: {count}";
        Print(text);
        Print(new string('-', Encoding.UTF8.GetByteCount(text)));
    }

    private void Endpoints(EndpointsDiff d)
    {
        Title("New Endpoints", d.Added.Count);
        foreach (var e in d.Added.Order()) Print(e.Method, e.Path, " ");
        Print("");
        Title("Deleted Endpoints", d.Deleted.Count);
        foreach (var e in d.Deleted.Order()) Print(e.Method, e.Path, " ");
        Print("");
        Title("Modified Endpoints", d.Modified.Count);
        foreach (var (e, md) in d.Modified)
        {
            Print(e.Method, e.Path);
            Indent().Method(md);
            Print("");
        }
    }

    private void Servers(ServersDiff? d)
    {
        if (d is not { Empty: false }) return;
        foreach (var s in Sorted(d.Added)) Print("New server:", s);
        foreach (var s in Sorted(d.Deleted)) Print("Deleted server:", s);
        foreach (var (s, sd) in d.Modified) { Print("Modified server:", s); Indent().Server(sd); }
    }

    private void Method(MethodDiff d)
    {
        if (d.Empty) return;
        if (d.ExtensionsDiff is { Empty: false }) { Print("Extensions changed"); Indent().Extensions(d.ExtensionsDiff); }
        Strings(d.TagsDiff, "Tags");
        Value(d.SummaryDiff, "Summary");
        Value(d.DescriptionDiff, "Description");
        Value(d.OperationIDDiff, "OperationID");
        Params(d.ParametersDiff);
        if (d.RequestBodyDiff is { Empty: false }) { Print("Request body changed"); Indent().RequestBody(d.RequestBodyDiff); }
        if (d.ResponsesDiff is { Empty: false }) { Print("Responses changed"); Indent().Responses(d.ResponsesDiff); }
        Message(d.CallbacksDiff, "Callbacks changed");
        Value(d.DeprecatedDiff, "Deprecated");
        if (d.SecurityDiff is { Empty: false }) { Print("Security changed"); Indent().SecurityRequirements(d.SecurityDiff); }
        if (d.ServersDiff is { Empty: false }) { Print("Servers changed"); Indent().Servers(d.ServersDiff); }
    }

    private void Params(ParametersDiffByLocation? d)
    {
        if (d is not { Empty: false }) return;
        foreach (var loc in ParametersDiffByLocation.ParamLocations)
            if (d.Added.TryGetValue(loc, out var ps)) foreach (var p in Sorted(ps)) Print("New", loc, "param:", p);
        foreach (var loc in ParametersDiffByLocation.ParamLocations)
            if (d.Deleted.TryGetValue(loc, out var ps)) foreach (var p in Sorted(ps)) Print("Deleted", loc, "param:", p);
        foreach (var loc in ParametersDiffByLocation.ParamLocations)
            if (d.Modified.TryGetValue(loc, out var pds))
                foreach (var (p, pd) in pds) { Print("Modified", loc, "param:", p); Indent().Param(pd); }
    }

    private void Param(ParameterDiff d)
    {
        Value(d.NameDiff, "Name");
        Value(d.InDiff, "In");
        if (d.ExtensionsDiff is { Empty: false }) { Print("Extensions changed"); Indent().Extensions(d.ExtensionsDiff); }
        Value(d.DescriptionDiff, "Description");
        Value(d.StyleDiff, "Style");
        Value(d.ExplodeDiff, "Explode");
        Value(d.AllowEmptyValueDiff, "AllowEmptyValue");
        Value(d.AllowReservedDiff, "AllowReserved");
        Value(d.DeprecatedDiff, "Deprecated");
        Value(d.RequiredDiff, "Required");
        if (d.SchemaDiff is { Empty: false }) { Print("Schema changed"); Indent().Schema(d.SchemaDiff); }
        Value(d.ExampleDiff, "Example");
        if (d.ExamplesDiff is { Empty: false }) { Print("Examples changed"); Indent().Examples(d.ExamplesDiff); }
        if (d.ContentDiff is { Empty: false }) { Print("Content changed"); Indent().Content(d.ContentDiff); }
    }

    private void Examples(ExamplesDiff? d)
    {
        if (d is not { Empty: false }) return;
        foreach (var e in Sorted(d.Added)) Print("New example:", e);
        foreach (var e in Sorted(d.Deleted)) Print("Deleted example:", e);
        foreach (var (e, ed) in d.Modified) { Print("Modified example:", e); Indent().Example(ed); }
    }

    private void Example(ExampleDiff d)
    {
        if (d.Empty) return;
        if (d.ExtensionsDiff is { Empty: false }) { Print("Extensions changed"); Indent().Extensions(d.ExtensionsDiff); }
        Value(d.SummaryDiff, "Summary");
        Value(d.DescriptionDiff, "Description");
        Value(d.ValueDiff, "Value");
        Value(d.ExternalValueDiff, "ExternalValue");
    }

    private void Server(ServerDiff d)
    {
        if (d.Empty) return;
        Cond(d.Added, "Server added");
        Cond(d.Deleted, "Server deleted");
        if (d.ExtensionsDiff is { Empty: false }) { Print("Extensions changed"); Indent().Extensions(d.ExtensionsDiff); }
        Value(d.URLDiff, "URL");
        Value(d.DescriptionDiff, "Description");
        if (d.VariablesDiff is { Empty: false }) { Print("Variables changed"); Indent().Variables(d.VariablesDiff); }
    }

    private void Variables(VariablesDiff d)
    {
        foreach (var v in Sorted(d.Added)) Print("New variable:", v);
        foreach (var v in Sorted(d.Deleted)) Print("Deleted variable:", v);
        foreach (var (v, vd) in d.Modified) { Print("Modified variable:", v); Indent().Variable(vd); }
    }

    private void Variable(VariableDiff d)
    {
        if (d.Empty) return;
        if (d.ExtensionsDiff is { Empty: false }) { Print("Extensions changed"); Indent().Extensions(d.ExtensionsDiff); }
        if (d.EnumDiff is { Empty: false })
        {
            Cond(d.EnumDiff.Added.Count > 0, "New enum values:", d.EnumDiff.Added);
            Cond(d.EnumDiff.Deleted.Count > 0, "Deleted enum values:", d.EnumDiff.Deleted);
        }
        Value(d.DefaultDiff, "Default");
        Value(d.DescriptionDiff, "Description");
    }

    private void Extensions(ExtensionsDiff? d)
    {
        if (d is not { Empty: false }) return;
        foreach (var e in Sorted(d.Added)) Print("New extension:", e);
        foreach (var e in Sorted(d.Deleted)) Print("Deleted extension:", e);
        // upstream ranges over a Go map here (random order); sorted keys keep the output stable
        foreach (var (e, patch) in d.Modified)
        {
            Print("Modified extension:", e);
            var ind = Indent();
            foreach (var op in patch) ind.Print(op.ToString());
        }
    }

    private void Schema(SchemaDiff? d)
    {
        if (d is not { Empty: false }) return;
        Cond(d.SchemaAdded, "Schema added");
        Cond(d.SchemaDeleted, "Schema deleted");
        if (d.ExtensionsDiff is { Empty: false }) { Print("Extensions changed"); Indent().Extensions(d.ExtensionsDiff); }
        if (d.OneOfDiff is { Empty: false }) { Print("Property 'OneOf' changed"); Indent().SubschemaList(d.OneOfDiff); }
        if (d.AnyOfDiff is { Empty: false }) { Print("Property 'AnyOf' changed"); Indent().SubschemaList(d.AnyOfDiff); }
        if (d.AllOfDiff is { Empty: false }) { Print("Property 'AllOf' changed"); Indent().SubschemaList(d.AllOfDiff); }
        if (d.NotDiff is { Empty: false }) { Print("Property 'Not' changed"); Indent().Schema(d.NotDiff); }
        Strings(d.TypeDiff, "Type");
        Value(d.TitleDiff, "Title");
        Value(d.FormatDiff, "Format");
        Value(d.DescriptionDiff, "Description");
        if (d.EnumDiff is { Empty: false })
        {
            Cond(d.EnumDiff.Added.Count > 0, "New enum values:", d.EnumDiff.Added);
            Cond(d.EnumDiff.Deleted.Count > 0, "Deleted enum values:", d.EnumDiff.Deleted);
        }
        Value(d.DefaultDiff, "Default");
        Value(d.ExampleDiff, "Example");
        Value(d.AdditionalPropertiesAllowedDiff, "AdditionalProperties");
        Value(d.UniqueItemsDiff, "UniqueItems");
        Value(d.ExclusiveMinDiff, "ExclusiveMin");
        Value(d.ExclusiveMaxDiff, "ExclusiveMax");
        Value(d.NullableDiff, "Nullable");
        Value(d.ReadOnlyDiff, "ReadOnly");
        Value(d.WriteOnlyDiff, "WriteOnly");
        Value(d.AllowEmptyValueDiff, "AllowEmptyValue");
        Value(d.XMLDiff, "XML");
        Value(d.DeprecatedDiff, "Deprecated");
        Value(d.MinDiff, "Min");
        Value(d.MaxDiff, "Max");
        Value(d.MultipleOfDiff, "MultipleOf");
        Value(d.MinLengthDiff, "MinLength");
        Value(d.MaxLengthDiff, "MaxLength");
        Value(d.PatternDiff, "Pattern");
        Value(d.MinItemsDiff, "MinItems");
        Value(d.MaxItemsDiff, "MaxItems");
        if (d.ItemsDiff is { Empty: false }) { Print("Items changed"); Indent().Schema(d.ItemsDiff); }
        if (d.RequiredDiff is { Empty: false })
        {
            Print("Required changed");
            var ind = Indent();
            foreach (var p in Sorted(d.RequiredDiff.Added)) ind.Print("New required property:", p);
            foreach (var p in Sorted(d.RequiredDiff.Deleted)) ind.Print("Deleted required property:", p);
        }
        Value(d.MinPropsDiff, "MinProps");
        Value(d.MaxPropsDiff, "MaxProps");
        if (d.PropertiesDiff is { Empty: false }) { Print("Properties changed"); Indent().Properties(d.PropertiesDiff); }
        if (d.AdditionalPropertiesDiff is { Empty: false }) { Print("AdditionalProperties changed"); Indent().Schema(d.AdditionalPropertiesDiff); }
        Message(d.DiscriminatorDiff, "Discriminator changed");
    }

    private void SubschemaList(SubschemasDiff d)
    {
        Cond(d.Added.Count > 0, "Schemas added:", Subschema.Join(d.Added));
        Cond(d.Deleted.Count > 0, "Schemas deleted:", Subschema.Join(d.Deleted));
        foreach (var m in d.Modified) { Print("Modified schema:", m.ToString()); Indent().Schema(m.Diff); }
    }

    private void Properties(SchemasDiff d)
    {
        foreach (var p in Sorted(d.Added)) Print("New property:", p);
        foreach (var p in Sorted(d.Deleted)) Print("Deleted property:", p);
        foreach (var (p, pd) in d.Modified) { Print("Modified property:", p); Indent().Schema(pd); }
    }

    private void Responses(ResponsesDiff d)
    {
        foreach (var s in Sorted(d.Added)) Print("New response:", s);
        foreach (var s in Sorted(d.Deleted)) Print("Deleted response:", s);
        foreach (var (s, rd) in d.Modified) { Print("Modified response:", s); Indent().Response(rd); }
    }

    private void Response(ResponseDiff d)
    {
        if (d.Empty) return;
        if (d.ExtensionsDiff is { Empty: false }) { Print("Extensions changed"); Indent().Extensions(d.ExtensionsDiff); }
        Value(d.DescriptionDiff, "Description");
        if (d.ContentDiff is { Empty: false }) { Print("Content changed"); Indent().Content(d.ContentDiff); }
        if (d.HeadersDiff is { Empty: false }) { Print("Headers changed"); Indent().Headers(d.HeadersDiff); }
    }

    private void RequestBody(RequestBodyDiff d)
    {
        if (d.Empty) return;
        if (d.ExtensionsDiff is { Empty: false }) { Print("Extensions changed"); Indent().Extensions(d.ExtensionsDiff); }
        Value(d.DescriptionDiff, "Description");
        if (d.ContentDiff is { Empty: false }) { Print("Content changed"); Indent().Content(d.ContentDiff); }
    }

    private void Content(ContentDiff d)
    {
        foreach (var n in Sorted(d.MediaTypeAdded)) Print("New media type:", n);
        foreach (var n in Sorted(d.MediaTypeDeleted)) Print("Deleted media type:", n);
        foreach (var (n, md) in d.MediaTypeModified) { Print("Modified media type:", n); Indent().MediaType(md); }
    }

    private void MediaType(MediaTypeDiff d)
    {
        if (d.Empty) return;
        if (d.NameDiff is { Empty: false }) Value(d.NameDiff.NameDiff, "Name");
        if (d.ExtensionsDiff is { Empty: false }) { Print("Extensions changed"); Indent().Extensions(d.ExtensionsDiff); }
        if (d.SchemaDiff is { Empty: false }) { Print("Schema changed"); Indent().Schema(d.SchemaDiff); }
        Value(d.ExampleDiff, "Example");
        if (d.ExamplesDiff is { Empty: false }) { Print("Examples changed"); Indent().Examples(d.ExamplesDiff); }
        Message(d.EncodingsDiff, "Encodings changed");
    }

    private void Headers(HeadersDiff d)
    {
        foreach (var h in Sorted(d.Added)) Print("New header:", h);
        foreach (var h in Sorted(d.Deleted)) Print("Deleted header:", h);
        foreach (var (h, hd) in d.Modified) { Print("Modified header:", h); Indent().Header(hd); }
    }

    private void Header(HeaderDiff d)
    {
        if (d.Empty) return;
        if (d.ExtensionsDiff is { Empty: false }) { Print("Extensions changed"); Indent().Extensions(d.ExtensionsDiff); }
        Value(d.DescriptionDiff, "Description");
        Value(d.DeprecatedDiff, "Deprecated");
        Value(d.RequiredDiff, "Required");
        Value(d.ExampleDiff, "Example");
        if (d.ExamplesDiff is { Empty: false }) { Print("Examples changed"); Indent().Examples(d.ExamplesDiff); }
        if (d.SchemaDiff is { Empty: false }) { Print("Schema changed"); Indent().Schema(d.SchemaDiff); }
        if (d.ContentDiff is { Empty: false }) { Print("Content changed"); Indent().Content(d.ContentDiff); }
    }

    private void SecurityRequirements(SecurityRequirementsDiff d)
    {
        foreach (var a in d.Added) Print("New security requirements:", a.ToString());
        foreach (var a in d.Deleted) Print("Deleted security requirements:", a.ToString());
        foreach (var m in d.Modified)
        {
            Print("Modified security requirements:", m.Base.SchemeNames());
            var ind = Indent();
            foreach (var (scheme, sd) in m.Scopes)
            {
                ind.Cond(sd.Added.Count > 0, "Scheme", scheme, "Added scopes:", sd.Added);
                ind.Cond(sd.Deleted.Count > 0, "Scheme", scheme, "Deleted scopes:", sd.Deleted);
            }
        }
    }
}
