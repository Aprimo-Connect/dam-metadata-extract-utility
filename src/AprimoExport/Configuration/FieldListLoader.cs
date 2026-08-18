using System.Xml.Linq;

namespace AprimoExport.Configuration;

/// <summary>
/// Builds the CSV column list from an external file, so the field set can be
/// maintained separately from the rest of the configuration.
///
/// <para>Two formats, detected automatically:</para>
/// <list type="number">
/// <item><b>Aprimo Data Exports schema XML</b> — the
/// <c>&lt;exportSourceConfiguration&gt;</c> file Aprimo produces. Read directly, so
/// there is no conversion step: <c>outputName</c> becomes the CSV header and
/// <c>fieldName</c> becomes the path.</item>
/// <item><b>Plain list</b> — one field per line. A bare line is a metadata field
/// name; <c>Column =&gt; path</c> gives an explicit mapping. <c>#</c> starts a comment.</item>
/// </list>
/// </summary>
public static class FieldListLoader
{
    private const string MappingSeparator = "=>";

    /// <summary>
    /// Record properties whose JSON name differs from the schema's propertyPath, or
    /// whose shape the OpenAPI spec leaves undefined.
    /// </summary>
    private static readonly Dictionary<string, string> PropertyPathMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ID"] = "id",
        ["CreatedOnUtc"] = "createdOn",
        ["ModifiedOnUtc"] = "modifiedOn",
        ["CreatedOn"] = "createdOn",
        ["ModifiedOn"] = "modifiedOn",
        ["ContentType"] = "contentType",
        ["Status"] = "status",
        ["Title"] = "title",
        ["Tag"] = "tag",
        ["TextContent"] = "textContent",
        ["AiInfluenced"] = "aiInfluenced",
        // Spec types these as bare objects with no properties, so try the likely shapes.
        ["CreatedBy"] = "createdBy.name || createdBy.fullName || createdBy.userName || createdBy.id",
        ["ModifiedBy"] = "modifiedBy.name || modifiedBy.fullName || modifiedBy.userName || modifiedBy.id"
    };

    /// <summary>
    /// Record property names that would be silently misread as metadata fields if
    /// written as a bare line in a plain list.
    /// </summary>
    private static readonly HashSet<string> KnownRecordProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "status", "contentType", "title", "tag", "textContent", "aiInfluenced",
        "hasImageOverlay", "createdOn", "modifiedOn", "createdBy", "modifiedBy"
    };

    public static List<FieldMapping> Load(string path, Action<string> log)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Field list file not found: '{Path.GetFullPath(path)}'.", path);

        // Strip a UTF-8 BOM, which would otherwise corrupt the first field name.
        var text = File.ReadAllText(path).TrimStart('﻿');

        if (text.TrimStart().StartsWith('<'))
        {
            log($"Reading Aprimo Data Exports schema from {Path.GetFullPath(path)}");
            return LoadAprimoSchema(text, path, log);
        }

        log($"Reading field list from {Path.GetFullPath(path)}");
        return LoadPlainList(text, path, log);
    }

    private static List<FieldMapping> LoadAprimoSchema(string xml, string path, Action<string> log)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new InvalidOperationException(
                $"'{Path.GetFullPath(path)}' starts with '<' but is not valid XML: {ex.Message}", ex);
        }

        var columns = document.Descendants("column").ToList();
        if (columns.Count == 0)
            throw new InvalidOperationException(
                $"'{Path.GetFullPath(path)}' contains no <column> elements. " +
                "Expected an Aprimo <exportSourceConfiguration> schema.");

        var schemaName = document.Descendants("schema").FirstOrDefault()?.Attribute("name")?.Value;
        if (schemaName is not null) log($"  Schema: {schemaName}");

        var mappings = new List<FieldMapping>(columns.Count);
        var fieldCount = 0;
        var propertyCount = 0;
        var guessed = new List<string>();
        var skipped = new List<string>();

        foreach (var column in columns)
        {
            var columnName = column.Attribute("columnName")?.Value;
            var outputName = column.Attribute("outputName")?.Value;
            var header = !string.IsNullOrWhiteSpace(outputName) ? outputName! : columnName;

            if (string.IsNullOrWhiteSpace(header))
            {
                skipped.Add("(unnamed column)");
                continue;
            }

            var fieldName = column.Attribute("fieldName")?.Value;
            var propertyPath = column.Attribute("propertyPath")?.Value;

            string mappedPath;

            if (!string.IsNullOrWhiteSpace(fieldName))
            {
                mappedPath = "field:" + fieldName;
                fieldCount++;
            }
            else if (!string.IsNullOrWhiteSpace(propertyPath))
            {
                if (PropertyPathMap.TryGetValue(propertyPath!, out var known))
                {
                    mappedPath = known;
                }
                else
                {
                    // Unknown property: camelCase it and flag the guess rather than fail.
                    mappedPath = ToCamelCase(propertyPath!);
                    guessed.Add($"{header} -> {mappedPath}");
                }
                propertyCount++;
            }
            else
            {
                skipped.Add(header!);
                continue;
            }

            mappings.Add(new FieldMapping { Column = header!, Path = mappedPath });
        }

        log($"  {mappings.Count} column(s): {fieldCount} metadata field(s), {propertyCount} record propert(ies).");

        if (guessed.Count > 0)
        {
            log($"  {guessed.Count} propertyPath value(s) were not recognised and were camel-cased as a guess. " +
                "Verify these with --sample:");
            foreach (var g in guessed.Take(10)) log($"    {g}");
            if (guessed.Count > 10) log($"    ... and {guessed.Count - 10} more");
        }

        if (skipped.Count > 0)
            log($"  Skipped {skipped.Count} column(s) with neither fieldName nor propertyPath: " +
                string.Join(", ", skipped.Take(5)) + (skipped.Count > 5 ? ", ..." : ""));

        return mappings;
    }

    /// <summary>
    /// Field names may be separated by newlines, commas, semicolons or tabs, mixed
    /// freely, so a list pasted from a spreadsheet cell or typed one-per-line both work.
    ///
    /// <para>Pipe is deliberately not a separator: <c>||</c> denotes fallback branches
    /// inside a path. Aprimo field names may contain spaces but not commas, so
    /// comma-splitting is safe.</para>
    /// </summary>
    private static readonly char[] Delimiters = { ',', ';', '\t' };

    private static List<FieldMapping> LoadPlainList(string text, string path, Action<string> log)
    {
        var mappings = new List<FieldMapping>();
        var lines = text.Split('\n');
        var explicitCount = 0;
        var ambiguous = new List<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            var lineNumber = i + 1;
            var line = StripComment(lines[i]).Trim();

            if (line.Length == 0) continue;

            // A line with '=>' is one explicit mapping. Never split it, since a path can
            // legitimately contain a comma inside a selector.
            var separator = line.IndexOf(MappingSeparator, StringComparison.Ordinal);
            if (separator >= 0)
            {
                var column = line.Substring(0, separator).Trim();
                var mappedPath = line.Substring(separator + MappingSeparator.Length).Trim();

                if (column.Length == 0 || mappedPath.Length == 0)
                    throw new InvalidOperationException(
                        $"{Path.GetFileName(path)} line {lineNumber}: expected 'Column {MappingSeparator} path', " +
                        $"got '{line}'.");

                mappings.Add(new FieldMapping { Column = column, Path = mappedPath });
                explicitCount++;
                continue;
            }

            foreach (var token in line.Split(Delimiters, StringSplitOptions.RemoveEmptyEntries))
            {
                var fieldName = token.Trim();
                if (fieldName.Length == 0) continue;

                if (KnownRecordProperties.Contains(fieldName))
                    ambiguous.Add($"line {lineNumber}: '{fieldName}'");

                mappings.Add(new FieldMapping
                {
                    Column = ToColumnName(fieldName),
                    Path = "field:" + fieldName
                });
            }
        }

        if (mappings.Count == 0)
            throw new InvalidOperationException(
                $"'{Path.GetFullPath(path)}' contained no field entries (only blanks and comments).");

        log($"  {mappings.Count} column(s): {mappings.Count - explicitCount} metadata field name(s), " +
            $"{explicitCount} explicit mapping(s).");

        if (ambiguous.Count > 0)
        {
            // Not necessarily wrong: a tenant can genuinely have a metadata field named
            // "Status" alongside the record's own status property, and an Aprimo export
            // schema may well treat it as a field. Just make the choice visible.
            log($"  Note: {ambiguous.Count} name(s) below also exist as Record properties. A bare name always");
            log("  means a metadata field, so that is what these read. To target the record property instead,");
            log($"  write it as 'Column {MappingSeparator} propertyName' (e.g. 'Status {MappingSeparator} status'):");
            foreach (var a in ambiguous) log($"    {a}");
        }

        return mappings;
    }

    /// <summary>
    /// Removes a trailing <c>#</c> or <c>//</c> comment.
    ///
    /// <para>A comment marker only counts at the start of the line or after whitespace.
    /// Field names really do contain '#' in practice — a set like <c>Photo#1</c> through
    /// <c>Photo#4</c> is common — so treating every '#' as a comment would silently
    /// truncate them all to <c>Photo</c> and collapse several columns into one.</para>
    /// </summary>
    private static string StripComment(string line)
    {
        for (var i = 0; i < line.Length; i++)
        {
            var isHash = line[i] == '#';
            var isSlashes = line[i] == '/' && i + 1 < line.Length && line[i + 1] == '/';

            if (!isHash && !isSlashes) continue;

            // Only a comment when it opens the line or follows whitespace.
            if (i == 0 || char.IsWhiteSpace(line[i - 1]))
                return line.Substring(0, i);
        }

        return line;
    }

    /// <summary>
    /// Derives a CSV header from a field name using Aprimo's own <c>outputName</c>
    /// convention: whitespace becomes an underscore and anything outside
    /// <c>[A-Za-z0-9_]</c> is dropped. Verified against a real 148-field export schema,
    /// reproducing every <c>outputName</c> exactly — including <c>Approved?</c> to
    /// <c>Approved</c> and <c>Photo#4</c> to <c>Photo4</c>.
    /// </summary>
    private static string ToColumnName(string fieldName)
    {
        var builder = new System.Text.StringBuilder(fieldName.Length);

        foreach (var c in fieldName)
        {
            if (char.IsWhiteSpace(c)) builder.Append('_');
            else if (char.IsAsciiLetterOrDigit(c) || c == '_') builder.Append(c);
            // Everything else is dropped, matching Aprimo.
        }

        // Never return an empty header; fall back to the raw name so validation can
        // report something recognisable.
        return builder.Length > 0 ? builder.ToString() : fieldName;
    }

    private static string ToCamelCase(string name) =>
        string.IsNullOrEmpty(name) || char.IsLower(name[0])
            ? name
            : char.ToLowerInvariant(name[0]) + name.Substring(1);
}
