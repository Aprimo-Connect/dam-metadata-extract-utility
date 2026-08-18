using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AprimoExport.Configuration;
using AprimoExport.Http;

namespace AprimoExport.Export;

/// <summary>Resume point, written after every completed page.</summary>
public sealed class Checkpoint
{
    public int Version { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; }
    public long RowsWritten { get; set; }
    public int LastFileIndex { get; set; }
    public PagingState Paging { get; set; } = new();

    /// <summary>Guards against resuming with settings that would produce an inconsistent file set.</summary>
    public string ConfigFingerprint { get; set; } = "";
}

public sealed class ExportResult
{
    public long RowsWritten { get; init; }
    public int FilesCreated { get; init; }
    public IReadOnlyList<string> FilePaths { get; init; } = Array.Empty<string>();
    public long RequestCount { get; init; }
    public long RetryCount { get; init; }
    public long BytesReceived { get; init; }
    public TimeSpan Elapsed { get; init; }
    public bool HitTotalCap { get; init; }
    public bool Cancelled { get; init; }
    public int? ApproximateTotalAvailable { get; init; }
}

public sealed class ExportRunner
{
    private const string CheckpointFileName = ".aprimo-export-checkpoint.json";

    private static readonly JsonSerializerOptions CheckpointJson = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ExportConfig _config;
    private readonly ApiClient _client;
    private readonly Action<string> _log;

    public ExportRunner(ExportConfig config, ApiClient client, Action<string> log)
    {
        _config = config;
        _client = client;
        _log = log;
    }

    public async Task<ExportResult> RunAsync(bool resume, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        var extractor = new FieldExtractor(_config.Fields, _config.Output.MultiValueSeparator);
        var selectFields = ResolveSelectRecordFields(extractor);

        var checkpointPath = Path.Combine(_config.Output.Directory, CheckpointFileName);
        var fingerprint = ComputeFingerprint();

        Checkpoint? restored = null;
        if (resume)
        {
            restored = LoadCheckpoint(checkpointPath, fingerprint);
            if (restored is null)
                _log("Resume requested but no usable checkpoint found — starting a fresh export.");
        }

        if (restored is null)
            CsvRollingWriter.PrepareDirectory(_config.Output, _log);

        var startingFileIndex = restored is not null
            ? Math.Max(restored.LastFileIndex, CsvRollingWriter.FindHighestFileIndex(_config.Output))
            : 0;

        var reader = new PagedRecordReader(_client, _config, selectFields, _log, restored?.Paging);

        await using var writer = new CsvRollingWriter(
            _config.Output, _config.Limits.MaxRecordsPerFile, extractor.Headers, _log, startingFileIndex);

        var rowsWritten = restored?.RowsWritten ?? 0;
        var cap = _config.Limits.MaxTotalRecords;
        var hitCap = false;
        var cancelled = false;
        var verifiedFields = false;

        var row = new string[extractor.ColumnCount];
        var lastProgressReport = stopwatch.Elapsed;
        var rowsAtLastReport = rowsWritten;

        if (restored is not null)
            _log($"Resuming from checkpoint: {rowsWritten:N0} rows already exported, " +
                 $"{restored.Paging.PagesFetched} page(s) fetched. New rows continue in part {startingFileIndex + 1}.");

        try
        {
            while (reader.HasMore)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var remaining = cap > 0 ? cap - rowsWritten : long.MaxValue;
                if (remaining <= 0)
                {
                    hitCap = true;
                    break;
                }

                // Do not ask for more than the cap allows on the final page.
                var takeLimit = remaining >= _config.Source.Paging.PageSize
                    ? _config.Source.Paging.PageSize
                    : (int)remaining;

                using var page = await reader.NextPageAsync(takeLimit, cancellationToken).ConfigureAwait(false);
                if (page is null) break;

                if (!verifiedFields)
                {
                    VerifyFieldsPresent(page, extractor);
                    VerifyLanguageMatches(page, extractor);
                    verifiedFields = true;
                }

                foreach (var record in page.Records)
                {
                    if (cap > 0 && rowsWritten >= cap)
                    {
                        hitCap = true;
                        break;
                    }

                    extractor.ExtractRow(record, row);
                    writer.WriteRow(row);
                    rowsWritten++;
                }

                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);

                if (_config.Limits.EnableCheckpoint)
                    SaveCheckpoint(checkpointPath, new Checkpoint
                    {
                        UpdatedAt = DateTimeOffset.UtcNow,
                        RowsWritten = rowsWritten,
                        LastFileIndex = writer.CurrentFileIndex,
                        Paging = reader.State,
                        ConfigFingerprint = fingerprint
                    });

                if (page.RawCount != page.Records.Count)
                    _log($"Page {page.PageNumber}: skipped {page.RawCount - page.Records.Count} " +
                         "record(s) already seen at the keyset watermark.");

                ReportProgress(
                    stopwatch, ref lastProgressReport, ref rowsAtLastReport,
                    rowsWritten, page, reader.LastTotalCount, cap, force: false);

                if (hitCap) break;
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            _log("Cancellation requested — flushing what has been written so far.");
        }

        await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);

        if (hitCap)
            _log($"Reached the configured total cap of {cap:N0} rows — stopping.");

        if (writer.ColumnsWithNewlines.Count > 0)
        {
            var affected = writer.ColumnsWithNewlines.OrderBy(c => c, StringComparer.Ordinal).ToList();
            var policy = _config.Output.NewlineInValue;

            _log($"{affected.Count} column(s) held newline-delimited values " +
                 $"(Aprimo's shape for list fields): " +
                 string.Join(", ", affected.Take(10)) + (affected.Count > 10 ? ", …" : ""));

            _log(policy switch
            {
                NewlineHandling.Separator =>
                    $"  Rewritten to single-line, joined by '{_config.Output.MultiValueSeparator}' " +
                    "(Output.NewlineInValue = Separator).",
                NewlineHandling.Space =>
                    "  Newlines replaced with a space (Output.NewlineInValue = Space).",
                _ =>
                    "  Kept as-is and RFC 4180 quoted, so those records span several physical lines. " +
                    "Readers must use a real CSV parser, not line splitting. " +
                    "Set Output.NewlineInValue to Separator for one line per record."
            });
        }

        if (extractor.ColumnsWithSeparatorInValue.Count > 0)
        {
            var affected = extractor.ColumnsWithSeparatorInValue.OrderBy(c => c, StringComparer.Ordinal).ToList();
            _log($"Warning: {affected.Count} multi-value column(s) contained a value that itself includes " +
                 $"'{_config.Output.MultiValueSeparator}', so those cells cannot be split reliably: " +
                 string.Join(", ", affected.Take(10)) + (affected.Count > 10 ? ", …" : ""));
            _log("  Change Output.MultiValueSeparator to a character the data does not use, or read those " +
                 "columns as a single value.");
        }

        return new ExportResult
        {
            RowsWritten = rowsWritten,
            FilesCreated = writer.FilesCreated,
            FilePaths = writer.FilePaths,
            RequestCount = _client.RequestCount,
            RetryCount = _client.RetryCount,
            BytesReceived = _client.BytesReceived,
            Elapsed = stopwatch.Elapsed,
            HitTotalCap = hitCap,
            Cancelled = cancelled,
            ApproximateTotalAvailable = reader.LastTotalCount
        };
    }

    private IReadOnlyList<string>? ResolveSelectRecordFields(FieldExtractor extractor)
    {
        if (!string.IsNullOrWhiteSpace(_config.Source.SelectRecordFields))
        {
            _log($"Using explicit select-record-fields: {_config.Source.SelectRecordFields}");
            return null; // The reader forwards the explicit header value verbatim.
        }

        if (!_config.Source.AutoDeriveSelectRecordFields) return null;

        var derived = extractor.DeriveSelectRecordFields();

        if (derived is null)
        {
            if (extractor.RequiresFields)
                _log("Not narrowing select-record-fields: a mapping enumerates fields dynamically " +
                     "(e.g. fields.items[*]), so all fields must be returned.");
            return null;
        }

        // Header size guard: a very wide schema can push select-record-fields past what
        // servers and proxies accept (often 8-16 KB for the whole header block), which
        // surfaces as an opaque 431 or 400. Returning all fields is the safer failure.
        var headerLength = string.Join(",", derived).Length;
        const int headerLimit = 6000;

        if (headerLength > headerLimit)
        {
            _log($"Not narrowing select-record-fields: {derived.Count} field names would make a " +
                 $"{headerLength}-char header, over the {headerLimit}-char safety limit. " +
                 "Requesting all fields instead. Set Source.SelectRecordFields explicitly to override.");
            return null;
        }

        _log($"Narrowing select-record-fields to {derived.Count} mapped field(s) ({headerLength} chars).");
        return derived;
    }

    /// <summary>
    /// The spec declares <c>select-record</c> only on the single-record endpoint, though
    /// the collection examples do include <c>fields</c>. If a tenant disagrees, fail on
    /// page one with a clear explanation rather than writing a file full of blanks.
    /// </summary>
    private void VerifyFieldsPresent(RecordPage page, FieldExtractor extractor)
    {
        if (!_config.Source.VerifyFieldsOnFirstPage) return;
        if (!extractor.RequiresFields) return;
        if (page.Records.Count == 0) return;

        foreach (var record in page.Records)
        {
            if (record.ValueKind != JsonValueKind.Object) continue;
            if (record.TryGetProperty("fields", out var fields) && fields.ValueKind != JsonValueKind.Null)
                return; // At least one record carries metadata — good.
        }

        throw new ApiException(
            $"None of the {page.Records.Count} records on page 1 returned a 'fields' object, but " +
            "column mappings read metadata fields.\n" +
            $"  The 'select-record: {_config.Source.SelectRecord}' header appears to have been ignored on this " +
            "collection endpoint, which means metadata would need a per-record GET /record/{id} call.\n" +
            "  Options: verify Source.SelectRecord names the sub-resources you need; confirm the registration " +
            "can read metadata; or set Source.VerifyFieldsOnFirstPage=false to export the non-field columns anyway.");
    }

    /// <summary>
    /// Guards against a wrong <c>Aprimo.Languages</c> ID.
    ///
    /// <para>Aprimo publishes no endpoint or table mapping language names to IDs, so the
    /// value has to be copied from observed data. Getting it wrong is the worst kind of
    /// error here: every localized field resolves to nothing and the export completes
    /// happily with blank metadata columns. So if a specific ID is configured and the
    /// first page contains localized values but none under that ID, stop.</para>
    /// </summary>
    private void VerifyLanguageMatches(RecordPage page, FieldExtractor extractor)
    {
        var configured = _config.Aprimo.Languages;

        // "*", empty, or a wildcard list means "whatever the tenant has" — nothing to check.
        if (string.IsNullOrWhiteSpace(configured) || configured.Contains('*')) return;
        if (!extractor.RequiresFields || page.Records.Count == 0) return;

        var wanted = configured
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (wanted.Count == 0) return;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var totalLocalizedValues = 0;

        foreach (var record in page.Records)
        {
            if (record.ValueKind != JsonValueKind.Object) continue;
            if (!record.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object) continue;
            if (!fields.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) continue;

            foreach (var field in items.EnumerateArray())
            {
                if (field.ValueKind != JsonValueKind.Object) continue;
                if (!field.TryGetProperty("localizedValues", out var values) ||
                    values.ValueKind != JsonValueKind.Array) continue;

                foreach (var localized in values.EnumerateArray())
                {
                    if (localized.ValueKind != JsonValueKind.Object) continue;
                    totalLocalizedValues++;

                    if (localized.TryGetProperty("languageId", out var id) &&
                        id.ValueKind == JsonValueKind.String &&
                        id.GetString() is { Length: > 0 } text)
                        seen.Add(text);
                }
            }
        }

        if (totalLocalizedValues == 0)
        {
            throw new ApiException(
                $"Page 1 returned no localized field values at all with languages='{configured}'.\n" +
                "  Either that language ID does not exist in this tenant, or no mapped field has a value in it.\n" +
                "  Aprimo exposes no endpoint listing language IDs, so confirm the value with --sample, which\n" +
                "  prints every language ID the tenant actually returns alongside a sample value.\n" +
                "  Set Aprimo.Languages to '*' to export all languages instead.");
        }

        if (seen.Overlaps(wanted)) return;

        throw new ApiException(
            $"Page 1 returned {totalLocalizedValues} localized value(s), but none under " +
            $"languages='{configured}'.\n" +
            $"  Language IDs actually present: {string.Join(", ", seen.Take(10))}" +
            (seen.Count > 10 ? ", …" : "") + "\n" +
            "  The configured ID is almost certainly wrong — every localized column would have been blank.\n" +
            "  Run --sample to see which ID carries English values, or set Aprimo.Languages to '*'.");
    }

    private void ReportProgress(
        Stopwatch stopwatch,
        ref TimeSpan lastReport,
        ref long rowsAtLastReport,
        long rowsWritten,
        RecordPage page,
        int? totalCount,
        long cap,
        bool force)
    {
        var elapsed = stopwatch.Elapsed;
        if (!force && (elapsed - lastReport).TotalSeconds < 5) return;

        var interval = (elapsed - lastReport).TotalSeconds;
        var recentRate = interval > 0 ? (rowsWritten - rowsAtLastReport) / interval : 0;
        var overallRate = elapsed.TotalSeconds > 0 ? rowsWritten / elapsed.TotalSeconds : 0;

        var target = cap > 0
            ? (totalCount.HasValue ? Math.Min(cap, totalCount.Value) : cap)
            : totalCount ?? 0;

        var eta = "";
        if (target > 0 && overallRate > 0 && rowsWritten < target)
        {
            var seconds = (target - rowsWritten) / overallRate;
            eta = $", ETA ~{TimeSpan.FromSeconds(seconds):hh\\:mm\\:ss}";
        }

        var of = target > 0 ? $" of ~{target:N0}" : "";

        _log($"Page {page.PageNumber}: {rowsWritten:N0} rows{of} " +
             $"({recentRate:F0}/s recent, {overallRate:F0}/s avg){eta}");

        lastReport = elapsed;
        rowsAtLastReport = rowsWritten;
    }

    private Checkpoint? LoadCheckpoint(string path, string fingerprint)
    {
        if (!File.Exists(path)) return null;

        try
        {
            var checkpoint = JsonSerializer.Deserialize<Checkpoint>(File.ReadAllText(path), CheckpointJson);
            if (checkpoint is null) return null;

            if (!string.Equals(checkpoint.ConfigFingerprint, fingerprint, StringComparison.Ordinal))
            {
                _log("Checkpoint ignored: the source query, paging settings or column mappings have changed " +
                     "since it was written. Resuming would mix incompatible output.");
                return null;
            }

            return checkpoint;
        }
        catch (Exception ex)
        {
            _log($"Checkpoint at '{path}' could not be read ({ex.Message}) — starting fresh.");
            return null;
        }
    }

    private void SaveCheckpoint(string path, Checkpoint checkpoint)
    {
        try
        {
            // Write-then-replace so a crash mid-write cannot corrupt the checkpoint.
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(checkpoint, CheckpointJson));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _log($"Warning: could not write checkpoint ({ex.Message}). The export continues, " +
                 "but it will not be resumable.");
        }
    }

    /// <summary>
    /// Hashes the settings that determine record order and column layout. A resume is
    /// only safe when these are unchanged.
    /// </summary>
    private string ComputeFingerprint()
    {
        var source = _config.Source;

        var material = string.Join("", new[]
        {
            _config.Aprimo.ResolvedApiBaseUrl,
            source.Mode.ToString(),
            source.SearchExpression ?? "",
            source.Filter ?? "",
            source.Sort,
            source.Paging.Mode.ToString(),
            source.Paging.PageSize.ToString(),
            source.Paging.KeysetProperty,
            string.Join(",", source.SearchParameters),
            string.Join(",", source.NamedSearchParameters.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")),
            string.Join(",", _config.Fields.Select(f => $"{f.Column}={f.Path}"))
        });

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).Substring(0, 16);
    }
}
