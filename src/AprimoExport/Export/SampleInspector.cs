using System.Text.Json;
using AprimoExport.Configuration;
using AprimoExport.Http;

namespace AprimoExport.Export;

/// <summary>
/// Single-request diagnostic. Fetches a handful of records and reports:
/// <list type="bullet">
/// <item>which language IDs the tenant actually returns, with a sample value each —
/// the practical way to find the English (US English) ID, since the
/// <c>languages</c> header takes IDs and the API exposes no languages endpoint;</item>
/// <item>which configured columns resolved to a value and which came back empty,
/// so a wrong <c>fieldName</c> is caught before a multi-hour export;</item>
/// <item>whether the <c>select-record</c> headers were honoured.</item>
/// </list>
/// The report goes to stdout so it can be redirected; progress logging stays on stderr.
/// </summary>
public sealed class SampleInspector
{
    private readonly ExportConfig _config;
    private readonly ApiClient _client;
    private readonly Action<string> _log;

    public SampleInspector(ExportConfig config, ApiClient client, Action<string> log)
    {
        _config = config;
        _client = client;
        _log = log;
    }

    public async Task<bool> RunAsync(int sampleSize, CancellationToken cancellationToken)
    {
        var extractor = new FieldExtractor(_config.Fields, _config.Output.MultiValueSeparator);
        var selectFields = extractor.DeriveSelectRecordFields();

        if (selectFields is { Count: > 0 })
        {
            var headerLength = string.Join(",", selectFields).Length;
            _log($"select-record-fields would carry {selectFields.Count} field name(s), {headerLength} chars.");
        }

        var reader = new PagedRecordReader(_client, _config, selectFields, _log, null);

        using var page = await reader.NextPageAsync(sampleSize, cancellationToken).ConfigureAwait(false);

        if (page is null || page.Records.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("No records returned. Nothing to inspect.");
            Console.WriteLine($"  Search expression : {_config.Source.SearchExpression ?? "(all records)"}");
            Console.WriteLine("  Either the expression matches nothing, or the registration cannot read these records.");
            return false;
        }

        Console.WriteLine();
        Console.WriteLine("════════ Sample inspection ════════");
        Console.WriteLine($"Records returned   : {page.Records.Count}");
        if (page.TotalCount is { } total)
            Console.WriteLine($"Approximate total  : {total:N0}  (approximate per the API spec)");
        Console.WriteLine($"languages header   : {_config.Aprimo.Languages ?? "(not sent)"}");
        Console.WriteLine($"select-record      : {_config.Source.SelectRecord ?? "(not sent)"}");

        ReportSubResources(page);
        ReportLanguages(page, _config.Aprimo.Languages);
        ReportColumns(page, extractor);

        return true;
    }

    /// <summary>Confirms the <c>select-record</c> header was honoured for each sub-resource.</summary>
    private static void ReportSubResources(RecordPage page)
    {
        string[] watched =
        {
            "fields", "masterFileLatestVersion", "createdBy", "modifiedBy",
            "classifications", "files", "permissions", "locks"
        };

        Console.WriteLine();
        Console.WriteLine("── Sub-resources present ──");

        foreach (var name in watched)
        {
            var populated = page.Records.Count(r =>
                r.ValueKind == JsonValueKind.Object &&
                r.TryGetProperty(name, out var v) &&
                v.ValueKind != JsonValueKind.Null &&
                v.ValueKind != JsonValueKind.Undefined);

            var mark = populated > 0 ? "yes" : "NULL";
            Console.WriteLine($"  {name,-26} {mark,-5} ({populated}/{page.Records.Count} records)");
        }
    }

    /// <summary>
    /// Distinct <c>languageId</c> values across every field's localized values, with a
    /// sample value so you can tell which GUID is English.
    /// </summary>
    private static void ReportLanguages(RecordPage page, string? configured)
    {
        var seen = new Dictionary<string, (int Count, string Sample, string Field)>(StringComparer.OrdinalIgnoreCase);

        foreach (var record in page.Records)
        {
            if (record.ValueKind != JsonValueKind.Object) continue;
            if (!record.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object) continue;
            if (!fields.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) continue;

            foreach (var field in items.EnumerateArray())
            {
                if (field.ValueKind != JsonValueKind.Object) continue;

                var fieldName = field.TryGetProperty("fieldName", out var fn) && fn.ValueKind == JsonValueKind.String
                    ? fn.GetString() ?? "?"
                    : "?";

                if (!field.TryGetProperty("localizedValues", out var values) ||
                    values.ValueKind != JsonValueKind.Array) continue;

                foreach (var localized in values.EnumerateArray())
                {
                    if (localized.ValueKind != JsonValueKind.Object) continue;
                    if (!localized.TryGetProperty("languageId", out var idElement) ||
                        idElement.ValueKind != JsonValueKind.String) continue;

                    var id = idElement.GetString();
                    if (string.IsNullOrEmpty(id)) continue;

                    var sample = localized.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String
                        ? v.GetString() ?? ""
                        : "";

                    if (seen.TryGetValue(id, out var existing))
                    {
                        seen[id] = (existing.Count + 1,
                                    string.IsNullOrEmpty(existing.Sample) ? sample : existing.Sample,
                                    string.IsNullOrEmpty(existing.Sample) ? fieldName : existing.Field);
                    }
                    else
                    {
                        seen[id] = (1, sample, fieldName);
                    }
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine("── Language IDs seen ──");

        if (seen.Count == 0)
        {
            Console.WriteLine("  none — no localized field values in this sample.");
            return;
        }

        var wanted = string.IsNullOrWhiteSpace(configured) || configured.Contains('*')
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : configured.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (id, info) in seen.OrderByDescending(kvp => kvp.Value.Count))
        {
            var tags = new List<string>();
            if (id.Equals(CompiledPath.NeutralLanguageId, StringComparison.OrdinalIgnoreCase))
                tags.Add("language-neutral");
            if (wanted.Contains(id))
                tags.Add("MATCHES Aprimo.Languages");

            var suffix = tags.Count > 0 ? "  <- " + string.Join(", ", tags) : "";
            Console.WriteLine($"  {id}  x{info.Count,-5}{suffix}");
            Console.WriteLine($"      e.g. {info.Field} = \"{Truncate(info.Sample, 60)}\"");
        }

        // A configured ID that never appears means every localized column would be blank.
        if (wanted.Count > 0 && !seen.Keys.Any(wanted.Contains))
        {
            Console.WriteLine();
            Console.WriteLine($"  WARNING: Aprimo.Languages is '{configured}', which does NOT appear above.");
            Console.WriteLine("  An export with this setting would leave every localized column blank.");
            Console.WriteLine("  Copy one of the IDs listed above, or use '*'.");
        }
        else if (wanted.Count == 0 && seen.Count > 1)
        {
            Console.WriteLine();
            Console.WriteLine("  More than one language is present and Aprimo.Languages is '*', so every");
            Console.WriteLine("  locale's value is joined into one cell. For an English-only export, copy the");
            Console.WriteLine("  GUID whose sample value reads as English into Aprimo.Languages.");
        }
    }

    /// <summary>Per-column resolution, so a wrong field name surfaces now rather than mid-export.</summary>
    private void ReportColumns(RecordPage page, FieldExtractor extractor)
    {
        Console.WriteLine();
        Console.WriteLine($"── Column resolution across {page.Records.Count} record(s) ──");

        var empty = new List<string>();
        var resolved = 0;

        foreach (var mapping in extractor.Mappings)
        {
            string? firstValue = null;
            var hits = 0;

            foreach (var record in page.Records)
            {
                var value = extractor.Evaluate(record, mapping.Compiled!);
                if (string.IsNullOrEmpty(value)) continue;
                hits++;
                firstValue ??= value;
            }

            if (hits > 0)
            {
                resolved++;
                Console.WriteLine($"  {mapping.Column,-38} {hits}/{page.Records.Count}  {Truncate(firstValue!, 48)}");
            }
            else
            {
                empty.Add(mapping.Column);
            }
        }

        Console.WriteLine();
        Console.WriteLine($"  {resolved}/{extractor.ColumnCount} columns produced a value in at least one record.");

        if (empty.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"── {empty.Count} column(s) empty in this sample ──");
            Console.WriteLine("  Genuinely blank on these records, or the field name is wrong.");
            Console.WriteLine("  Cross-check against GET /fielddefinitions before assuming the former.");
            foreach (var chunk in empty.Chunk(4))
                Console.WriteLine("    " + string.Join(", ", chunk));
        }
    }

    private static string Truncate(string value, int max)
    {
        var single = value.Replace('\r', ' ').Replace('\n', ' ');
        return single.Length <= max ? single : single.Substring(0, max - 1) + "…";
    }
}
