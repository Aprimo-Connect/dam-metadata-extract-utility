using System.Globalization;
using System.Text;
using System.Text.Json;
using AprimoExport.Configuration;
using AprimoExport.Http;

namespace AprimoExport.Export;

/// <summary>One page of records. Owns the parsed document; dispose after processing.</summary>
public sealed class RecordPage : IDisposable
{
    private readonly JsonDocument _document;

    /// <summary>Records in this page, already de-duplicated for keyset paging.</summary>
    public IReadOnlyList<JsonElement> Records { get; }

    /// <summary>Records the API returned, before keyset de-duplication.</summary>
    public int RawCount { get; }

    /// <summary>Server-reported total. Approximate per the spec — ETA only, never a loop bound.</summary>
    public int? TotalCount { get; }

    public int PageNumber { get; }

    internal RecordPage(
        JsonDocument document,
        IReadOnlyList<JsonElement> records,
        int rawCount,
        int? totalCount,
        int pageNumber)
    {
        _document = document;
        Records = records;
        RawCount = rawCount;
        TotalCount = totalCount;
        PageNumber = pageNumber;
    }

    public void Dispose() => _document.Dispose();
}

/// <summary>Serialisable paging position, persisted to the checkpoint file.</summary>
public sealed class PagingState
{
    public int PagesFetched { get; set; }
    public int NextSkip { get; set; }
    public int NextPageNumber { get; set; } = 1;
    public string? Watermark { get; set; }
    public List<string> IdsAtWatermark { get; set; } = new();
}

/// <summary>
/// Reads records page by page from <c>POST /search/records</c> or <c>GET /records</c>.
///
/// <para>Three paging strategies:</para>
/// <list type="bullet">
/// <item><b>Offset</b> — <c>skip</c>/<c>take</c>. Simple, but Elasticsearch-backed
/// search typically refuses or degrades past a deep-paging ceiling.</item>
/// <item><b>PageNumber</b> — <c>page</c>/<c>pageSize</c>. Same ceiling caveat.</item>
/// <item><b>Keyset</b> — sorts ascending on a watermark property and narrows the
/// search expression each page, so <c>skip</c> stays 0. The option that scales to
/// millions of rows.</item>
/// </list>
/// </summary>
public sealed class PagedRecordReader
{
    private readonly ApiClient _client;
    private readonly ExportConfig _config;
    private readonly Action<string> _log;
    private readonly IReadOnlyList<string>? _selectRecordFields;

    private readonly PagingState _state;
    private readonly HashSet<string> _idsAtWatermark;
    private string? _watermark;

    public bool HasMore { get; private set; } = true;
    public int? LastTotalCount { get; private set; }
    public PagingState State => Snapshot();

    public PagedRecordReader(
        ApiClient client,
        ExportConfig config,
        IReadOnlyList<string>? selectRecordFields,
        Action<string> log,
        PagingState? resumeFrom = null)
    {
        _client = client;
        _config = config;
        _selectRecordFields = selectRecordFields;
        _log = log;

        _state = resumeFrom ?? new PagingState();
        _watermark = _state.Watermark;
        _idsAtWatermark = new HashSet<string>(_state.IdsAtWatermark, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Fetches the next page, or null when exhausted.
    /// </summary>
    /// <param name="takeLimit">
    /// Upper bound on this page's size. The runner passes the remaining row budget so
    /// the final request under a total cap does not over-fetch.
    /// </param>
    public async Task<RecordPage?> NextPageAsync(int takeLimit, CancellationToken cancellationToken)
    {
        if (!HasMore) return null;

        var paging = _config.Source.Paging;

        if (paging.MaxPages > 0 && _state.PagesFetched >= paging.MaxPages)
        {
            _log($"Stopping: reached Source.Paging.MaxPages ({paging.MaxPages}).");
            HasMore = false;
            return null;
        }

        var take = Math.Min(paging.PageSize, Math.Max(1, takeLimit));
        var pageNumber = _state.PagesFetched + 1;

        var document = await _client
            .SendJsonAsync(() => BuildRequest(take), $"page {pageNumber}", cancellationToken)
            .ConfigureAwait(false);

        var keepDocument = false;
        try
        {
            var root = document.RootElement;

            // GET /record/{id} returns the Record object itself, not a collection with
            // an "items" array, so it needs its own unwrapping.
            if (_config.Source.Mode == SourceMode.SingleRecord)
            {
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out _))
                    throw new ApiException(
                        $"GET /record/{{id}} did not return a record object. Received keys: {DescribeKeys(root)}.");

                LastTotalCount = 1;
                HasMore = false;
                _state.PagesFetched = pageNumber;

                keepDocument = true;
                return new RecordPage(document, new[] { root }, 1, 1, pageNumber);
            }

            if (root.TryGetProperty("totalCount", out var totalElement) &&
                totalElement.ValueKind == JsonValueKind.Number &&
                totalElement.TryGetInt32(out var total))
                LastTotalCount = total;

            if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                throw new ApiException(
                    $"Page {pageNumber}: response has no 'items' array. " +
                    $"Received keys: {DescribeKeys(root)}.");

            var rawCount = items.GetArrayLength();
            var records = new List<JsonElement>(rawCount);

            foreach (var item in items.EnumerateArray())
            {
                if (paging.Mode == PagingMode.Keyset && IsDuplicateAtWatermark(item)) continue;
                records.Add(item);
            }

            _state.PagesFetched = pageNumber;

            // Advance paging position.
            switch (paging.Mode)
            {
                case PagingMode.Offset:
                    _state.NextSkip += rawCount;
                    if (rawCount < take) HasMore = false;
                    break;

                case PagingMode.PageNumber:
                    _state.NextPageNumber++;
                    if (rawCount < take) HasMore = false;
                    break;

                case PagingMode.Keyset:
                    if (rawCount == 0)
                    {
                        HasMore = false;
                    }
                    else
                    {
                        AdvanceWatermark(items, paging.KeysetProperty);

                        if (records.Count == 0)
                            throw new ApiException(
                                $"Keyset paging stalled on page {pageNumber}: all {rawCount} records were " +
                                $"duplicates already seen at {paging.KeysetProperty} = '{_watermark}'. " +
                                $"More than {take} records share that value, so the watermark cannot advance. " +
                                "Increase Source.Paging.PageSize, choose a higher-cardinality " +
                                "Source.Paging.KeysetProperty (e.g. ModifiedOn), or switch to Offset paging.");

                        // A short page means the tail; nothing left beyond the watermark.
                        if (rawCount < take) HasMore = false;
                    }
                    break;
            }

            keepDocument = true;
            return new RecordPage(document, records, rawCount, LastTotalCount, pageNumber);
        }
        finally
        {
            if (!keepDocument) document.Dispose();
        }
    }

    private HttpRequestMessage BuildRequest(int take)
    {
        var baseUrl = _config.Aprimo.ResolvedApiBaseUrl;
        var source = _config.Source;
        var paging = source.Paging;

        // One record by ID: no paging, sort or expression applies.
        if (source.Mode == SourceMode.SingleRecord)
        {
            var id = SourceConfig.NormalizeRecordId(source.RecordId!);
            var single = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/record/{id}");
            ApplySelectHeaders(single);
            return single;
        }

        var query = new List<string>();

        switch (paging.Mode)
        {
            case PagingMode.PageNumber:
                query.Add($"page={_state.NextPageNumber.ToString(CultureInfo.InvariantCulture)}");
                query.Add($"pageSize={take.ToString(CultureInfo.InvariantCulture)}");
                break;

            case PagingMode.Offset:
                query.Add($"skip={_state.NextSkip.ToString(CultureInfo.InvariantCulture)}");
                query.Add($"take={take.ToString(CultureInfo.InvariantCulture)}");
                break;

            case PagingMode.Keyset:
                // Watermark lives in the expression, so the offset always stays 0.
                query.Add("skip=0");
                query.Add($"take={take.ToString(CultureInfo.InvariantCulture)}");
                break;
        }

        var sort = ResolveSort();
        if (!string.IsNullOrWhiteSpace(sort))
            query.Add("sort=" + Uri.EscapeDataString(sort));

        HttpRequestMessage request;

        if (source.Mode == SourceMode.Search)
        {
            var url = $"{baseUrl}/search/records?{string.Join("&", query)}";
            request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(BuildSearchBody(), Encoding.UTF8, "application/json")
            };
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(source.Filter))
                query.Add("filter=" + Uri.EscapeDataString(source.Filter));

            var url = $"{baseUrl}/records?{string.Join("&", query)}";
            request = new HttpRequestMessage(HttpMethod.Get, url);
        }

        ApplySelectHeaders(request);
        return request;
    }

    private string ResolveSort()
    {
        var paging = _config.Source.Paging;

        // Keyset correctness depends on ascending order by the watermark property.
        if (paging.Mode == PagingMode.Keyset)
            return paging.KeysetProperty;

        return _config.Source.Sort;
    }

    private void ApplySelectHeaders(HttpRequestMessage request)
    {
        var source = _config.Source;

        if (!string.IsNullOrWhiteSpace(source.SelectRecord))
            request.Headers.TryAddWithoutValidation("select-record", source.SelectRecord);

        var fields = source.SelectRecordFields;
        if (string.IsNullOrWhiteSpace(fields) && _selectRecordFields is { Count: > 0 })
            fields = string.Join(",", _selectRecordFields);

        if (!string.IsNullOrWhiteSpace(fields))
            request.Headers.TryAddWithoutValidation("select-record-fields", fields);

        if (!string.IsNullOrWhiteSpace(source.SelectRecordFieldGroups))
            request.Headers.TryAddWithoutValidation("select-record-fieldgroups", source.SelectRecordFieldGroups);

        foreach (var kvp in source.AdditionalSelectHeaders)
            request.Headers.TryAddWithoutValidation(kvp.Key, kvp.Value);
    }

    private string BuildSearchBody()
    {
        var source = _config.Source;

        var expression = ComposeExpression(source.SearchExpression);

        using var stream = new MemoryStream(512);
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();

            // The API rejects a searchExpression carrying no expression:
            // "A searchExpression must contain either an expression or subExpressions."
            // So omit the object entirely rather than sending an empty one. Config
            // validation should already have caught this; belt and braces.
            if (!string.IsNullOrWhiteSpace(expression))
            {
                writer.WriteStartObject("searchExpression");
                writer.WriteString("expression", expression);

                if (source.SearchParameters.Count > 0)
                {
                    writer.WriteStartArray("parameters");
                    foreach (var p in source.SearchParameters) writer.WriteStringValue(p);
                    writer.WriteEndArray();
                }

                if (source.NamedSearchParameters.Count > 0)
                {
                    writer.WriteStartObject("namedParameters");
                    foreach (var kvp in source.NamedSearchParameters)
                        writer.WriteString(kvp.Key, kvp.Value);
                    writer.WriteEndObject();
                }

                if (source.SupportWildcards)
                    writer.WriteBoolean("supportWildcards", true);

                writer.WriteString("defaultLogicalOperator",
                    source.DefaultLogicalOperator.ToUpperInvariant());

                writer.WriteEndObject(); // searchExpression
            }

            writer.WriteBoolean("logRequest", source.LogRequest);

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Lower bound used to seed keyset paging when nothing else filters. Any real record
    /// post-dates it, so it matches everything while still being a valid expression —
    /// the API rejects an empty <c>searchExpression</c>.
    /// </summary>
    private static readonly DateTimeOffset KeysetEpoch = new(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Adds the keyset watermark predicate to the configured expression.</summary>
    private string? ComposeExpression(string? baseExpression)
    {
        if (_config.Source.Paging.Mode != PagingMode.Keyset) return baseExpression;

        var property = _config.Source.Paging.KeysetProperty;

        if (_watermark is null)
        {
            // First page: no watermark yet. Seed an open lower bound if nothing else
            // filters, so a full keyset backfill needs no hand-written expression.
            return string.IsNullOrWhiteSpace(baseExpression)
                ? $"{property} >= {SearchExpressions.FormatInstant(KeysetEpoch)}"
                : baseExpression;
        }

        var predicate = $"{property} >= {SearchExpressions.FormatValue(_watermark)}";

        // Composed via the shared helper so keyset and the delta window cannot drift on
        // parenthesising or value formatting — both append to the same expression.
        return SearchExpressions.And(baseExpression, predicate);
    }

    private bool IsDuplicateAtWatermark(JsonElement record)
    {
        if (_watermark is null) return false;
        var id = ReadId(record);
        return id is not null && _idsAtWatermark.Contains(id);
    }

    /// <summary>
    /// Moves the watermark to the highest value in this page and records the IDs sitting
    /// exactly on it, so the next page (fetched with <c>&gt;=</c>) can drop the overlap.
    /// </summary>
    private void AdvanceWatermark(JsonElement items, string property)
    {
        var propertyName = ToCamelCase(property);
        string? highest = null;

        foreach (var item in items.EnumerateArray())
        {
            var value = ReadProperty(item, propertyName);
            if (value is null) continue;
            if (highest is null || string.CompareOrdinal(value, highest) > 0) highest = value;
        }

        if (highest is null)
            throw new ApiException(
                $"Keyset paging requires '{property}' on every record, but property '{propertyName}' " +
                "was not present in the response. Check Source.Paging.KeysetProperty, or switch to Offset paging.");

        if (_watermark is not null && string.CompareOrdinal(highest, _watermark) == 0)
        {
            // Still on the same value: keep accumulating the tie set.
            AddIdsAtValue(items, propertyName, highest);
        }
        else
        {
            _watermark = highest;
            _idsAtWatermark.Clear();
            AddIdsAtValue(items, propertyName, highest);
        }
    }

    private void AddIdsAtValue(JsonElement items, string propertyName, string value)
    {
        foreach (var item in items.EnumerateArray())
        {
            if (!string.Equals(ReadProperty(item, propertyName), value, StringComparison.Ordinal)) continue;
            var id = ReadId(item);
            if (id is not null) _idsAtWatermark.Add(id);
        }
    }

    private static string? ReadId(JsonElement record) => ReadProperty(record, "id");

    private static string? ReadProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(propertyName, out var value)) return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    /// <summary>
    /// Search expressions use PascalCase property names (<c>CreatedOn</c>) while JSON
    /// responses use camelCase (<c>createdOn</c>).
    /// </summary>
    private static string ToCamelCase(string name) =>
        string.IsNullOrEmpty(name) || char.IsLower(name[0])
            ? name
            : char.ToLowerInvariant(name[0]) + name.Substring(1);

    private static string DescribeKeys(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return root.ValueKind.ToString();
        var keys = root.EnumerateObject().Select(p => p.Name).Take(12).ToArray();
        return keys.Length == 0 ? "(none)" : string.Join(", ", keys);
    }

    private PagingState Snapshot() => new()
    {
        PagesFetched = _state.PagesFetched,
        NextSkip = _state.NextSkip,
        NextPageNumber = _state.NextPageNumber,
        Watermark = _watermark,
        IdsAtWatermark = _idsAtWatermark.ToList()
    };
}
