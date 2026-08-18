using System.Text;
using System.Text.Json;
using AprimoExport.Configuration;

namespace AprimoExport.Export;

internal abstract record PathStep;
internal sealed record PropStep(string Name) : PathStep;
internal sealed record IndexStep(int Index) : PathStep;
internal sealed record AllStep : PathStep;
internal sealed record FilterStep(string Key, string Value) : PathStep;

/// <summary>
/// A parsed field mapping path.
///
/// <para>Grammar:</para>
/// <code>
///   path     := branch ('||' branch)*
///   branch   := segment ('.' segment)*
///   segment  := name selector*
///   selector := '[' ( index | '*' | key '=' value ) ']'
/// </code>
///
/// <para><c>||</c> separates fallback branches, tried left to right until one yields a
/// value. Useful where the API's shape is not pinned down — the OpenAPI spec types
/// <c>createdBy</c> and <c>modifiedBy</c> as bare objects with no properties, so
/// <c>createdBy.name || createdBy.userName || createdBy.id</c> survives whichever
/// shape the tenant actually returns.</para>
///
/// <para>Shorthand: <c>field:Brand</c> expands to
/// <c>fields.items[fieldName=Brand].localizedValues[*].value</c>, and
/// <c>field:Brand@1033</c> pins the language to
/// <c>localizedValues[languageId=1033].value</c>.</para>
///
/// <para>Examples against the Aprimo Record schema:</para>
/// <code>
///   id
///   masterFileLatestVersion.fileName
///   field:Alt Text                     -- field names may contain spaces
///   fields.items[fieldName=Abstract].localizedValues[0].value
///   fields.items[*].fieldName          -- every field name, joined by MultiValueSeparator
/// </code>
/// </summary>
public sealed class CompiledPath
{
    /// <summary>Language ID Aprimo uses for language-neutral values.</summary>
    public const string NeutralLanguageId = "00000000000000000000000000000000";

    private const string BranchSeparator = "||";

    /// <summary>Fallback branches, in evaluation order.</summary>
    internal IReadOnlyList<IReadOnlyList<PathStep>> Branches { get; }

    /// <summary>Number of fallback branches; 1 unless the path uses <c>||</c>.</summary>
    public int BranchCount => Branches.Count;

    public string Raw { get; }
    public string Expanded { get; }

    /// <summary>
    /// Metadata field names this path reads. Feeds the <c>select-record-fields</c>
    /// header so the API returns only the metadata actually mapped.
    /// </summary>
    public IReadOnlyList<string> ReferencedFieldNames { get; }

    /// <summary>True when any branch reads under <c>fields</c>.</summary>
    public bool TouchesFields { get; }

    /// <summary>
    /// True when a branch enumerates fields dynamically (e.g. <c>fields.items[*]</c>),
    /// which makes narrowing via <c>select-record-fields</c> unsafe.
    /// </summary>
    public bool WalksAllFields { get; }

    private CompiledPath(string raw, string expanded, IReadOnlyList<IReadOnlyList<PathStep>> branches)
    {
        Raw = raw;
        Expanded = expanded;
        Branches = branches;

        var names = new List<string>();
        var touches = false;
        var walksAll = false;

        foreach (var steps in branches)
        {
            if (steps.Count == 0 || steps[0] is not PropStep { Name: "fields" }) continue;

            touches = true;

            var fieldName = steps.OfType<FilterStep>()
                                 .FirstOrDefault(f => f.Key.Equals("fieldName", StringComparison.OrdinalIgnoreCase))
                                 ?.Value;

            if (string.IsNullOrEmpty(fieldName))
                walksAll = true;
            else if (!names.Contains(fieldName, StringComparer.OrdinalIgnoreCase))
                names.Add(fieldName);
        }

        ReferencedFieldNames = names;
        TouchesFields = touches;
        WalksAllFields = walksAll;
    }

    public static CompiledPath Compile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new FormatException("Field path cannot be empty.");

        var raw = path.Trim();

        var parts = raw.Split(
            BranchSeparator,
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
            throw new FormatException($"Field path '{raw}' contains no usable branch.");

        var branches = new List<IReadOnlyList<PathStep>>(parts.Length);
        var expanded = new List<string>(parts.Length);

        foreach (var part in parts)
        {
            var expandedBranch = ExpandShorthand(part);
            var steps = Parse(expandedBranch);

            if (steps.Count == 0)
                throw new FormatException($"Branch '{part}' in field path '{raw}' produced no steps.");

            branches.Add(steps);
            expanded.Add(expandedBranch);
        }

        return new CompiledPath(raw, string.Join(" || ", expanded), branches);
    }

    private static string ExpandShorthand(string path)
    {
        const string prefix = "field:";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return path;

        var spec = path.Substring(prefix.Length).Trim();
        if (spec.Length == 0)
            throw new FormatException("The 'field:' shorthand requires a field name, e.g. 'field:Brand'.");

        // field:Name@languageId pins a single language; otherwise take all languages.
        var at = spec.LastIndexOf('@');
        if (at > 0)
        {
            var name = spec.Substring(0, at).Trim();
            var lang = spec.Substring(at + 1).Trim();
            if (lang.Equals("neutral", StringComparison.OrdinalIgnoreCase))
                lang = NeutralLanguageId;
            return $"fields.items[fieldName={name}].localizedValues[languageId={lang}].value";
        }

        return $"fields.items[fieldName={spec}].localizedValues[*].value";
    }

    private static List<PathStep> Parse(string path)
    {
        var steps = new List<PathStep>();
        var i = 0;

        while (i < path.Length)
        {
            var start = i;
            while (i < path.Length && path[i] != '.' && path[i] != '[') i++;

            if (i > start)
                steps.Add(new PropStep(path.Substring(start, i - start).Trim()));

            while (i < path.Length && path[i] == '[')
            {
                var close = path.IndexOf(']', i);
                if (close < 0)
                    throw new FormatException($"Unclosed '[' in field path '{path}'.");

                steps.Add(ParseSelector(path.Substring(i + 1, close - i - 1), path));
                i = close + 1;
            }

            if (i < path.Length && path[i] == '.') i++;
        }

        return steps;
    }

    private static PathStep ParseSelector(string inner, string path)
    {
        inner = inner.Trim();

        if (inner == "*")
            return new AllStep();

        if (int.TryParse(inner, out var index))
        {
            if (index < 0) throw new FormatException($"Negative array index in field path '{path}'.");
            return new IndexStep(index);
        }

        var eq = inner.IndexOf('=');
        if (eq <= 0)
            throw new FormatException(
                $"Invalid selector '[{inner}]' in field path '{path}'. " +
                "Expected an index, '*', or 'key=value'.");

        var key = inner.Substring(0, eq).Trim();
        var value = inner.Substring(eq + 1).Trim().Trim('\'', '"');
        return new FilterStep(key, value);
    }

    public override string ToString() => Expanded;
}

/// <summary>
/// Projects a record's JSON onto the configured CSV columns.
/// Not thread-safe: it reuses internal buffers to stay allocation-light across
/// millions of rows. Use one instance per writer thread.
/// </summary>
public sealed class FieldExtractor
{
    private readonly FieldMapping[] _mappings;
    private readonly string _multiValueSeparator;

    private List<JsonElement> _current = new(8);
    private List<JsonElement> _scratch = new(8);
    private readonly StringBuilder _sb = new(256);

    private readonly HashSet<string> _separatorCollisions = new(StringComparer.Ordinal);
    private string _currentColumn = "";

    /// <summary>
    /// Columns where a multi-value cell contained a value that itself included
    /// <c>MultiValueSeparator</c>, making the joined cell ambiguous to split downstream.
    /// Worth surfacing rather than shipping quietly.
    /// </summary>
    public IReadOnlyCollection<string> ColumnsWithSeparatorInValue => _separatorCollisions;

    public string[] Headers { get; }

    public FieldExtractor(IEnumerable<FieldMapping> mappings, string multiValueSeparator)
    {
        _mappings = mappings.ToArray();
        _multiValueSeparator = multiValueSeparator;
        Headers = _mappings.Select(m => m.Column).ToArray();

        foreach (var m in _mappings)
        {
            try
            {
                m.Compiled ??= CompiledPath.Compile(m.Path);
            }
            catch (FormatException ex)
            {
                throw new FormatException($"Column '{m.Column}': {ex.Message}", ex);
            }
        }
    }

    public int ColumnCount => _mappings.Length;

    /// <summary>Writes one row's values into <paramref name="destination"/> (length must be <see cref="ColumnCount"/>).</summary>
    public void ExtractRow(JsonElement record, string[] destination)
    {
        if (destination.Length != _mappings.Length)
            throw new ArgumentException(
                $"Destination length {destination.Length} does not match column count {_mappings.Length}.",
                nameof(destination));

        for (var i = 0; i < _mappings.Length; i++)
        {
            var mapping = _mappings[i];
            _currentColumn = mapping.Column;
            destination[i] = Evaluate(record, mapping.Compiled!) ?? mapping.Default;
        }
    }

    /// <summary>Returns the joined value, or null when every branch resolved to nothing.</summary>
    public string? Evaluate(JsonElement root, CompiledPath path)
    {
        foreach (var branch in path.Branches)
        {
            var value = EvaluateBranch(root, branch);
            if (!string.IsNullOrEmpty(value)) return value;
        }

        return null;
    }

    private string? EvaluateBranch(JsonElement root, IReadOnlyList<PathStep> steps)
    {
        var current = _current;
        var scratch = _scratch;

        current.Clear();
        current.Add(root);

        foreach (var step in steps)
        {
            scratch.Clear();
            foreach (var element in current)
                Apply(step, element, scratch);

            (current, scratch) = (scratch, current);

            if (current.Count == 0) break;
        }

        // Keep the swapped buffers for the next call.
        _current = current;
        _scratch = scratch;

        if (current.Count == 0) return null;

        if (current.Count == 1)
            return Format(current[0]);

        // Multi-value field: a list field, or one carrying values in several languages.
        // Every value is preserved, joined by MultiValueSeparator.
        _sb.Clear();
        var wrote = false;
        var collision = false;

        foreach (var element in current)
        {
            var text = Format(element);
            if (string.IsNullOrEmpty(text)) continue;

            // A value containing the separator makes the joined cell ambiguous to split
            // downstream. Only checked when actually joining, so single values cost nothing.
            if (text.Contains(_multiValueSeparator, StringComparison.Ordinal)) collision = true;

            if (wrote) _sb.Append(_multiValueSeparator);
            _sb.Append(text);
            wrote = true;
        }

        if (collision) _separatorCollisions.Add(_currentColumn);

        return wrote ? _sb.ToString() : null;
    }

    private static void Apply(PathStep step, JsonElement element, List<JsonElement> output)
    {
        switch (step)
        {
            case PropStep prop:
                if (element.ValueKind == JsonValueKind.Object &&
                    element.TryGetProperty(prop.Name, out var child) &&
                    child.ValueKind != JsonValueKind.Null &&
                    child.ValueKind != JsonValueKind.Undefined)
                    output.Add(child);
                break;

            case IndexStep idx:
                if (element.ValueKind == JsonValueKind.Array &&
                    idx.Index < element.GetArrayLength())
                {
                    var item = element[idx.Index];
                    if (item.ValueKind != JsonValueKind.Null) output.Add(item);
                }
                break;

            case AllStep:
                if (element.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in element.EnumerateArray())
                        if (item.ValueKind != JsonValueKind.Null) output.Add(item);
                }
                else if (element.ValueKind == JsonValueKind.Object)
                {
                    foreach (var member in element.EnumerateObject())
                        if (member.Value.ValueKind != JsonValueKind.Null) output.Add(member.Value);
                }
                break;

            case FilterStep filter:
                if (element.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in element.EnumerateArray())
                        if (Matches(item, filter)) output.Add(item);
                }
                else if (element.ValueKind == JsonValueKind.Object && Matches(element, filter))
                {
                    output.Add(element);
                }
                break;
        }
    }

    private static bool Matches(JsonElement candidate, FilterStep filter)
    {
        if (candidate.ValueKind != JsonValueKind.Object) return false;
        if (!candidate.TryGetProperty(filter.Key, out var value)) return false;

        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };

        return text is not null && text.Equals(filter.Value, StringComparison.OrdinalIgnoreCase);
    }

    private static string Format(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? "",
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        // Objects/arrays at a leaf are emitted as raw JSON; the CSV writer quotes them.
        _ => element.GetRawText()
    };

    /// <summary>
    /// Field names to pass in <c>select-record-fields</c>, or null when any mapping
    /// enumerates fields dynamically and narrowing would drop data.
    /// </summary>
    public IReadOnlyList<string>? DeriveSelectRecordFields()
    {
        var names = new List<string>();

        foreach (var m in _mappings)
        {
            var compiled = m.Compiled!;
            if (!compiled.TouchesFields) continue;
            if (compiled.WalksAllFields) return null;

            foreach (var name in compiled.ReferencedFieldNames)
                if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
                    names.Add(name);
        }

        return names.Count > 0 ? names : null;
    }

    /// <summary>True when at least one column reads from <c>fields</c>.</summary>
    public bool RequiresFields => _mappings.Any(m => m.Compiled!.TouchesFields);

    /// <summary>Mappings in column order. Used by the --sample diagnostic.</summary>
    public IReadOnlyList<FieldMapping> Mappings => _mappings;
}
