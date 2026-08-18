using System.Text.Json.Serialization;

namespace AprimoExport.Configuration;

/// <summary>
/// Root configuration, bound from appsettings.json then overlaid with
/// environment variables and command-line switches (see <see cref="ConfigLoader"/>).
/// </summary>
public sealed class ExportConfig
{
    public AprimoConfig Aprimo { get; set; } = new();
    public SourceConfig Source { get; set; } = new();
    public ThrottleConfig Throttle { get; set; } = new();
    public OutputConfig Output { get; set; } = new();
    public LimitsConfig Limits { get; set; } = new();

    /// <summary>Ordered CSV columns and where each one reads from in the record JSON.</summary>
    public List<FieldMapping> Fields { get; set; } = new();

    /// <summary>
    /// Path to an external field list, which <b>replaces</b> <see cref="Fields"/> when set.
    /// Accepts an Aprimo Data Exports schema XML file or a plain one-per-line list —
    /// see <see cref="FieldListLoader"/>. Relative paths resolve against the current
    /// directory, then the directory holding the executable.
    /// </summary>
    public string? FieldsFile { get; set; }

    public IEnumerable<string> Validate()
    {
        foreach (var e in Aprimo.Validate()) yield return e;
        foreach (var e in Source.Validate()) yield return e;
        foreach (var e in Throttle.Validate()) yield return e;
        foreach (var e in Output.Validate()) yield return e;
        foreach (var e in Limits.Validate()) yield return e;

        if (Fields.Count == 0)
            yield return "Fields: at least one column mapping is required.";

        foreach (var dupe in Fields.Select(f => f.Column)
                                   .Where(c => !string.IsNullOrWhiteSpace(c))
                                   .GroupBy(c => c, StringComparer.OrdinalIgnoreCase)
                                   .Where(g => g.Count() > 1)
                                   .Select(g => g.Key))
            yield return $"Fields: duplicate column name '{dupe}'.";

        for (var i = 0; i < Fields.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(Fields[i].Column))
                yield return $"Fields[{i}]: 'Column' is required.";
            if (string.IsNullOrWhiteSpace(Fields[i].Path))
                yield return $"Fields[{i}] ('{Fields[i].Column}'): 'Path' is required.";
        }

        // Keyset paging rewrites the search expression, so it needs Search mode.
        if (Source.Paging.Mode == PagingMode.Keyset && Source.Mode != SourceMode.Search)
            yield return "Source.Paging.Mode 'Keyset' requires Source.Mode 'Search' " +
                         "(it advances a watermark inside the search expression).";

        // POST /search/records rejects an empty searchExpression outright:
        // "A searchExpression must contain either an expression or subExpressions."
        // Catch it here rather than after a round trip.
        if (Source.Mode == SourceMode.SingleRecord && string.IsNullOrWhiteSpace(Source.RecordId))
            yield return "Source.Mode 'SingleRecord' requires a record ID — pass --record <id>.";

        if (Source.Mode == SourceMode.SingleRecord &&
            !string.IsNullOrWhiteSpace(Source.RecordId) &&
            SourceConfig.NormalizeRecordId(Source.RecordId!).Length == 0)
            yield return $"Source.RecordId '{Source.RecordId}' contains no usable characters.";

        // Keyset paging seeds its own open lower bound on the first page, so it does not
        // need one supplied.
        if (Source.Mode == SourceMode.Search &&
            Source.Paging.Mode != PagingMode.Keyset &&
            string.IsNullOrWhiteSpace(Source.SearchExpression))
            yield return
                "Source.Mode 'Search' requires a search expression, but none is set. " +
                $"POST /search/records rejects an empty one.{Environment.NewLine}" +
                $"      Pick one:{Environment.NewLine}" +
                $"        - add a delta window, which supplies the expression: --daily, or " +
                $"--since yesterday / 1d / <date>{Environment.NewLine}" +
                $"        - set Source.SearchExpression (or -e), e.g. \"ContentType = 'Asset'\"" +
                $"{Environment.NewLine}" +
                $"        - for an unfiltered full export of every record, use --mode Records, " +
                $"which calls GET /records and needs no expression";
    }
}

/// <summary>
/// Tenant, endpoints and OAuth 2.0 client-credentials settings.
/// </summary>
public sealed class AprimoConfig
{
    /// <summary>
    /// Tenant identifier — the subdomain of your Aprimo DAM URL. Used to derive
    /// both endpoints below when they are not set explicitly.
    /// </summary>
    public string Tenant { get; set; } = "";

    /// <summary>
    /// API base URL. Defaults to <c>https://{tenant}.dam.aprimo.com/api/core</c>.
    /// </summary>
    public string? ApiBaseUrl { get; set; }

    /// <summary>
    /// OAuth token endpoint. Defaults to <c>https://{tenant}.aprimo.com/login/connect/token</c>.
    /// Note this host has no <c>.dam</c> segment — it differs from the API host.
    /// </summary>
    public string? TokenUrl { get; set; }

    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";

    /// <summary>Space-delimited scopes. The DAM API scope is <c>api</c>.</summary>
    public string Scope { get; set; } = "api";

    /// <summary>
    /// How to present client credentials on the token request. <c>Basic</c> uses an
    /// Authorization header (RFC 6749 §2.3.1 preferred form); <c>Body</c> puts
    /// client_id/client_secret in the form body. Aprimo's IdentityServer accepts both.
    /// </summary>
    public ClientAuthStyle ClientAuthStyle { get; set; } = ClientAuthStyle.Basic;

    /// <summary>
    /// Renew the access token once it is within this many seconds of expiry, so a
    /// long-running export never presents a token that expires mid-flight.
    ///
    /// <para>Must comfortably exceed <see cref="SourceConfig.RequestTimeoutSeconds"/>,
    /// or a request that starts just inside the margin can outlive its own token. Check
    /// the Access Token Lifetime on the registration: for a 10-minute token, 180s renews
    /// at the 7-minute mark — clear of the 120s request timeout. An expiry that slips
    /// through is still recovered by the 401 retry, but that costs a wasted request.</para>
    /// </summary>
    public int RefreshSkewSeconds { get; set; } = 180;

    /// <summary>Value of the mandatory <c>API-VERSION</c> header.</summary>
    public string ApiVersion { get; set; } = "1";

    /// <summary>
    /// <c>languages</c> header: a language ID, a comma-separated list, or <c>*</c>
    /// for all languages. Null omits the header (tenant default applies).
    /// </summary>
    public string? Languages { get; set; } = "*";

    /// <summary>Extra form parameters for the token request, if your registration needs them.</summary>
    public Dictionary<string, string> AdditionalTokenParameters { get; set; } = new();

    public string ResolvedApiBaseUrl =>
        !string.IsNullOrWhiteSpace(ApiBaseUrl)
            ? ApiBaseUrl!.TrimEnd('/')
            : $"https://{Tenant}.dam.aprimo.com/api/core";

    public string ResolvedTokenUrl =>
        !string.IsNullOrWhiteSpace(TokenUrl)
            ? TokenUrl!
            : $"https://{Tenant}.aprimo.com/login/connect/token";

    private static bool LooksLikePlaceholder(string value) =>
        PlaceholderDetection.LooksLikePlaceholder(value);

    /// <summary>
    /// Explains a literally-copied placeholder and names every place it could have come
    /// from. The environment is listed first because it silently overrides the config
    /// file, which makes it the least obvious source.
    /// </summary>
    private static string PlaceholderMessage(string setting, string value, string envVar, string flag) =>
        $"Aprimo.{setting} is set to the placeholder {value} — a value from the documentation, " +
        $"not a real credential.{Environment.NewLine}" +
        $"      Check, in this order of precedence:{Environment.NewLine}" +
        $"        1. the {envVar} environment variable (this overrides the config file, " +
        $"so it wins silently){Environment.NewLine}" +
        $"           check:  [Environment]::GetEnvironmentVariable('{envVar}','User'){Environment.NewLine}" +
        $"           clear:  [Environment]::SetEnvironmentVariable('{envVar}',$null,'User')" +
        $"   (then open a new terminal){Environment.NewLine}" +
        $"        2. the {flag} command-line switch{Environment.NewLine}" +
        $"        3. Aprimo.{setting} in appsettings.json";

    public IEnumerable<string> Validate()
    {
        var haveExplicitUrls = !string.IsNullOrWhiteSpace(ApiBaseUrl) &&
                               !string.IsNullOrWhiteSpace(TokenUrl);

        if (string.IsNullOrWhiteSpace(Tenant) && !haveExplicitUrls)
            yield return "Aprimo.Tenant is required (or set both Aprimo.ApiBaseUrl and Aprimo.TokenUrl).";

        if (!Uri.TryCreate(ResolvedApiBaseUrl, UriKind.Absolute, out _))
            yield return $"Aprimo.ApiBaseUrl is not a valid absolute URL: '{ResolvedApiBaseUrl}'.";
        if (!Uri.TryCreate(ResolvedTokenUrl, UriKind.Absolute, out _))
            yield return $"Aprimo.TokenUrl is not a valid absolute URL: '{ResolvedTokenUrl}'.";

        if (string.IsNullOrWhiteSpace(ClientId))
            yield return "Aprimo.ClientId is required (set env APRIMO_CLIENT_ID to keep it out of the config file).";
        else if (LooksLikePlaceholder(ClientId))
            yield return PlaceholderMessage("ClientId", ClientId, "APRIMO_CLIENT_ID", "--client-id");

        if (string.IsNullOrWhiteSpace(ClientSecret))
            yield return "Aprimo.ClientSecret is required (set env APRIMO_CLIENT_SECRET to keep it out of the config file).";
        else if (LooksLikePlaceholder(ClientSecret))
            yield return PlaceholderMessage("ClientSecret", "(the value you supplied)",
                "APRIMO_CLIENT_SECRET", "--client-secret");

        if (!string.IsNullOrWhiteSpace(Tenant) && LooksLikePlaceholder(Tenant))
            yield return PlaceholderMessage("Tenant", Tenant, "APRIMO_TENANT", "--tenant");
        if (string.IsNullOrWhiteSpace(ApiVersion))
            yield return "Aprimo.ApiVersion is required (the API mandates the API-VERSION header).";
        if (RefreshSkewSeconds < 0)
            yield return "Aprimo.RefreshSkewSeconds must be >= 0.";
    }
}

public enum ClientAuthStyle { Basic, Body }

/// <summary>How the CSV writer treats newlines occurring inside a value.</summary>
public enum NewlineHandling
{
    /// <summary>
    /// Keep them, quoting the field per RFC 4180. Correct, but a record then spans
    /// several physical lines and needs a real CSV parser to read back.
    /// </summary>
    Preserve,

    /// <summary>Replace each run with a single space, giving one line per record.</summary>
    Space,

    /// <summary>
    /// Replace each run with <see cref="OutputConfig.MultiValueSeparator"/>, so an
    /// Aprimo list field reads as <c>Indoor|Individual</c> — one line per record, and the
    /// same shape as a genuinely multi-valued field.
    /// </summary>
    Separator
}

/// <summary>Detects documentation placeholders that were copied literally.</summary>
internal static class PlaceholderDetection
{
    /// <summary>
    /// Values that are unmistakably a copied example rather than a real credential.
    /// Deliberately an exact-match list, not a substring scan, so a genuine secret that
    /// happens to contain one of these words is never rejected.
    /// </summary>
    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        "your-client-id", "your-client-secret", "your-secret", "your-secret-here",
        "your-id", "your-tenant", "your-tenant-name", "tenant",
        "changeme", "change-me", "placeholder", "todo", "tbd",
        "xxx", "xxxx", "xxxxx", "none", "null", "example", "test-value"
    };

    public static bool LooksLikePlaceholder(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return false;

        // Angle-bracketed values such as <your-client-id> are always a template.
        if (trimmed.StartsWith('<') && trimmed.EndsWith('>')) return true;

        return Known.Contains(trimmed);
    }
}

public enum SourceMode
{
    /// <summary>POST /search/records with a search expression. Supports filtering and keyset paging.</summary>
    Search,

    /// <summary>GET /records with an optional filter expression.</summary>
    Records,

    /// <summary>
    /// GET /record/{recordId} for exactly one record. Intended for testing a mapping
    /// against a known record; needs no expression, filter or paging.
    /// </summary>
    SingleRecord
}

public sealed class SourceConfig
{
    public SourceMode Mode { get; set; } = SourceMode.Search;

    /// <summary>
    /// Search expression for <see cref="SourceMode.Search"/>, e.g.
    /// <c>ContentType = 'Asset' AND Status = 'Released'</c>. Empty matches everything.
    /// </summary>
    public string? SearchExpression { get; set; }

    /// <summary>Values for <c>?</c> positional placeholders in the expression, in order.</summary>
    public List<string> SearchParameters { get; set; } = new();

    /// <summary>Values for <c>@name</c> placeholders in the expression.</summary>
    public Dictionary<string, string> NamedSearchParameters { get; set; } = new();

    /// <summary>Treat <c>*</c> and <c>?</c> inside quoted strings as wildcards.</summary>
    public bool SupportWildcards { get; set; }

    /// <summary>Operator applied between terms with no explicit operator. AND or OR.</summary>
    public string DefaultLogicalOperator { get; set; } = "AND";

    /// <summary>Log the search for Aprimo analytics. Off — this is a bulk export, not user activity.</summary>
    public bool LogRequest { get; set; }

    /// <summary>Filter expression for <see cref="SourceMode.Records"/>.</summary>
    public string? Filter { get; set; }

    /// <summary>
    /// Record ID for <see cref="SourceMode.SingleRecord"/>. Accepts the hyphenated GUID
    /// form as well as Aprimo's own 32-character form; see <see cref="NormalizeRecordId"/>.
    /// </summary>
    public string? RecordId { get; set; }

    /// <summary>
    /// Strips hyphens, braces and whitespace from a record ID.
    ///
    /// <para>Aprimo returns and expects IDs as bare 32-character hex
    /// (<c>fa7eb97fe1ef4f5c8554b3aa011387ec</c>), but the same value is often displayed or
    /// copied in hyphenated GUID form. Passing the hyphenated form straight through would
    /// 404 for no visible reason.</para>
    /// </summary>
    public static string NormalizeRecordId(string recordId) =>
        new(recordId.Where(char.IsAsciiLetterOrDigit).ToArray());

    /// <summary>
    /// Sort expression; prefix with <c>-</c> for descending. A stable sort is required
    /// for correct paging — do not leave this empty on large exports.
    ///
    /// <para>Ascending by default. Descending (<c>-ModifiedOn</c>) is riskier for long
    /// exports: freshly modified records jump to page 1 and shift every offset behind
    /// them. Ignored in Keyset mode, which sorts by
    /// <see cref="PagingConfig.KeysetProperty"/>.</para>
    /// </summary>
    public string Sort { get; set; } = "ModifiedOn";

    /// <summary>
    /// <c>select-record</c> header: sub-resources to embed inline. Without these the
    /// corresponding record properties come back null.
    /// </summary>
    public string? SelectRecord { get; set; } = "fields,masterfilelatestversion,createdby,modifiedby";

    /// <summary>
    /// <c>select-record-fields</c> header: restricts which metadata fields are returned.
    /// Leave null and keep <see cref="AutoDeriveSelectRecordFields"/> on to have it
    /// derived from the configured column mappings.
    /// </summary>
    public string? SelectRecordFields { get; set; }

    /// <summary>
    /// Derive <c>select-record-fields</c> from the field names referenced by the
    /// column mappings. Cuts response size substantially on wide schemas.
    /// Disable if a mapping walks fields dynamically (e.g. <c>fields.items[*]</c>).
    /// </summary>
    public bool AutoDeriveSelectRecordFields { get; set; } = true;

    /// <summary><c>select-record-fieldgroups</c> header: restrict fields to these field groups.</summary>
    public string? SelectRecordFieldGroups { get; set; }

    /// <summary>Additional <c>select-*</c> cascade headers, e.g. "select-fileversion": "renditions".</summary>
    public Dictionary<string, string> AdditionalSelectHeaders { get; set; } = new();

    /// <summary>
    /// Abort on the first page if <c>fields</c> comes back null while a column mapping
    /// needs it — a clear signal that the tenant ignores <c>select-record</c> on
    /// collection endpoints and that per-record hydration would be required.
    /// </summary>
    public bool VerifyFieldsOnFirstPage { get; set; } = true;

    public PagingConfig Paging { get; set; } = new();
    public RetryConfig Retry { get; set; } = new();
    public DeltaConfig Delta { get; set; } = new();

    public int RequestTimeoutSeconds { get; set; } = 120;

    public IEnumerable<string> Validate()
    {
        if (string.Equals(DefaultLogicalOperator, "AND", StringComparison.OrdinalIgnoreCase) == false &&
            string.Equals(DefaultLogicalOperator, "OR", StringComparison.OrdinalIgnoreCase) == false)
            yield return $"Source.DefaultLogicalOperator must be AND or OR (got '{DefaultLogicalOperator}').";

        if (RequestTimeoutSeconds <= 0)
            yield return "Source.RequestTimeoutSeconds must be > 0.";

        foreach (var e in Paging.Validate()) yield return e;
        foreach (var e in Retry.Validate()) yield return e;
        foreach (var e in Delta.Validate()) yield return e;
    }
}

/// <summary>
/// Incremental ("changed records") export settings. Mirrors what the tenant's own Aprimo
/// Data Exports schema does — <c>schema.txt</c> declares a
/// <c>ChangedRecordsExportSource</c>.
/// </summary>
public sealed class DeltaConfig
{
    /// <summary>
    /// Record property compared. Must be a UTC timestamp the API can sort and filter on;
    /// <c>ModifiedOn</c> for "what changed", <c>CreatedOn</c> for "what is new".
    /// </summary>
    public string Property { get; set; } = "ModifiedOn";

    /// <summary>
    /// Minutes of deliberate re-read at the start of a resumed window.
    ///
    /// <para>Windows are chained (<c>since</c> = previous <c>until</c>), which is gap-free
    /// against our own clock. This guards the remaining risk: if the API stamps
    /// <c>ModifiedOn</c> from a clock slightly behind ours, a record edited just after a
    /// window closed could be stamped inside it and never appear. The cost is a few
    /// duplicate rows per run; the alternative is silent data loss. Set to 0 for exact
    /// adjacency when both clocks are known to be tight.</para>
    /// </summary>
    public int OverlapMinutes { get; set; } = 5;

    /// <summary>
    /// Where the high-water mark is stored. Relative paths resolve inside
    /// <c>Output.Directory</c>. Keyed by tenant + query, so changing either starts a
    /// fresh lineage rather than reusing an unrelated mark.
    /// </summary>
    public string StateFile { get; set; } = "delta-state.json";

    /// <summary>Warn when a window is longer than this many days. 0 disables the warning.</summary>
    public int WarnIfWindowExceedsDays { get; set; } = 7;

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(Property))
            yield return "Source.Delta.Property is required.";
        if (OverlapMinutes < 0)
            yield return "Source.Delta.OverlapMinutes must be >= 0.";
        if (string.IsNullOrWhiteSpace(StateFile))
            yield return "Source.Delta.StateFile is required.";
        if (WarnIfWindowExceedsDays < 0)
            yield return "Source.Delta.WarnIfWindowExceedsDays must be >= 0 (0 disables the warning).";
    }
}

public enum PagingMode
{
    /// <summary><c>skip</c> + <c>take</c>, advancing the offset. Simple; may hit a deep-paging ceiling.</summary>
    Offset,

    /// <summary><c>page</c> + <c>pageSize</c>, 1-based. Same ceiling caveat as Offset.</summary>
    PageNumber,

    /// <summary>
    /// Seek paging: sort ascending on a property and advance a watermark in the search
    /// expression each page, so <c>skip</c> stays 0. The scalable option for
    /// multi-million-row exports. Search mode only.
    /// </summary>
    Keyset
}

public sealed class PagingConfig
{
    public PagingMode Mode { get; set; } = PagingMode.Offset;

    /// <summary>Records per request. The API caps this at 1000 (RecordCollection.limit maximum).</summary>
    public int PageSize { get; set; } = 1000;

    /// <summary>Hard ceiling from the OpenAPI spec.</summary>
    public const int MaxApiPageSize = 1000;

    /// <summary>
    /// Record property used as the keyset watermark, and the sort key in Keyset mode
    /// (overriding <see cref="SourceConfig.Sort"/>).
    ///
    /// <para>Defaults to <c>CreatedOn</c> deliberately, even though the display sort
    /// defaults to <c>ModifiedOn</c>: a watermark must be immutable. If a record is
    /// edited mid-export its <c>ModifiedOn</c> moves past the watermark and the record
    /// is exported twice. <c>CreatedOn</c> never moves. Use <c>ModifiedOn</c> here only
    /// for a delta export against a quiet tenant.</para>
    /// </summary>
    public string KeysetProperty { get; set; } = "CreatedOn";

    /// <summary>
    /// Stop after this many pages. 0 = unlimited. A cheap safety net against a
    /// misconfigured loop; unlike Limits.MaxTotalRecords this bounds requests, not rows.
    /// </summary>
    public int MaxPages { get; set; }

    public IEnumerable<string> Validate()
    {
        if (PageSize <= 0)
            yield return "Source.Paging.PageSize must be > 0.";
        else if (PageSize > MaxApiPageSize)
            yield return $"Source.Paging.PageSize must be <= {MaxApiPageSize} (API limit).";

        if (Mode == PagingMode.Keyset && string.IsNullOrWhiteSpace(KeysetProperty))
            yield return "Source.Paging.KeysetProperty is required for Keyset paging.";

        if (MaxPages < 0)
            yield return "Source.Paging.MaxPages must be >= 0 (0 = unlimited).";
    }
}

public sealed class RetryConfig
{
    public int MaxAttempts { get; set; } = 5;
    public double InitialBackoffSeconds { get; set; } = 1.0;
    public double MaxBackoffSeconds { get; set; } = 60.0;
    public double BackoffMultiplier { get; set; } = 2.0;

    /// <summary>
    /// Honour a <c>Retry-After</c> header instead of the computed backoff. The spec
    /// documents no 429, but we handle it defensively.
    /// </summary>
    public bool RespectRetryAfter { get; set; } = true;

    public IEnumerable<string> Validate()
    {
        if (MaxAttempts < 1) yield return "Source.Retry.MaxAttempts must be >= 1.";
        if (InitialBackoffSeconds <= 0) yield return "Source.Retry.InitialBackoffSeconds must be > 0.";
        if (MaxBackoffSeconds < InitialBackoffSeconds) yield return "Source.Retry.MaxBackoffSeconds must be >= InitialBackoffSeconds.";
        if (BackoffMultiplier < 1) yield return "Source.Retry.BackoffMultiplier must be >= 1.";
    }
}

/// <summary>Client-side rate limiting. The spec documents no server limits, so this is the control.</summary>
public sealed class ThrottleConfig
{
    /// <summary>
    /// Sustained request rate. Fractional values allowed (0.5 = one request every two
    /// seconds). Zero or negative disables throttling entirely.
    /// </summary>
    public double RequestsPerSecond { get; set; } = 5.0;

    /// <summary>
    /// Token-bucket depth: how many requests may fire back-to-back after an idle
    /// period. 1 gives strictly even spacing.
    /// </summary>
    public int Burst { get; set; } = 1;

    /// <summary>Cap on in-flight requests. The page loop is sequential, so 1 is correct today.</summary>
    public int MaxConcurrentRequests { get; set; } = 1;

    // Token requests deliberately bypass this limiter: they use a separate HttpClient
    // that is never routed through it, so renewal can never be starved by the export.

    public IEnumerable<string> Validate()
    {
        if (Burst < 1) yield return "Throttle.Burst must be >= 1.";
        if (MaxConcurrentRequests < 1) yield return "Throttle.MaxConcurrentRequests must be >= 1.";
        if (double.IsNaN(RequestsPerSecond) || double.IsInfinity(RequestsPerSecond))
            yield return "Throttle.RequestsPerSecond must be a finite number.";
    }
}

public sealed class OutputConfig
{
    public string Directory { get; set; } = "./export";

    /// <summary>Base file name. Output lands as <c>{prefix}_0001.csv</c>, <c>_0002</c>, …</summary>
    public string FilePrefix { get; set; } = "aprimo-records";

    public string Delimiter { get; set; } = ",";

    /// <summary>Write a UTF-8 BOM. Excel needs it to detect UTF-8 on double-click.</summary>
    public bool WriteByteOrderMark { get; set; } = true;

    /// <summary>CRLF (RFC 4180) or LF.</summary>
    public string NewLine { get; set; } = "CRLF";

    /// <summary>Joins multiple values when one mapping resolves to several (e.g. list fields, all languages).</summary>
    public string MultiValueSeparator { get; set; } = "|";

    /// <summary>
    /// What to do about newlines inside a value.
    ///
    /// <para>This matters more than it looks. Aprimo returns list fields as a single
    /// newline-delimited string rather than as separate localized values, so
    /// <c>KeywordsAsText</c> arrives as nine lines in one cell. Preserving that is valid
    /// RFC 4180 — the writer quotes it — but it means one record spans many physical
    /// lines, which breaks any consumer that splits on newlines before parsing.</para>
    /// </summary>
    public NewlineHandling NewlineInValue { get; set; } = NewlineHandling.Separator;

    /// <summary>
    /// Prefix values starting with <c>= + - @</c> (or tab/CR) with a single quote so
    /// spreadsheets do not evaluate them as formulas. CSV injection defence.
    /// </summary>
    public bool SanitizeFormulas { get; set; } = true;

    /// <summary>Write file buffer size in bytes. Larger buffers help on multi-million-row runs.</summary>
    public int WriteBufferBytes { get; set; } = 1 << 20;

    /// <summary>Delete any pre-existing export files matching the prefix instead of refusing to start.</summary>
    public bool Overwrite { get; set; }

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(Directory))
            yield return "Output.Directory is required.";
        if (string.IsNullOrWhiteSpace(FilePrefix))
            yield return "Output.FilePrefix is required.";
        if (string.IsNullOrEmpty(Delimiter))
            yield return "Output.Delimiter is required.";
        if (Delimiter.Contains('"'))
            yield return "Output.Delimiter cannot contain a double quote.";
        if (!string.Equals(NewLine, "CRLF", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(NewLine, "LF", StringComparison.OrdinalIgnoreCase))
            yield return $"Output.NewLine must be CRLF or LF (got '{NewLine}').";
        if (WriteBufferBytes < 4096)
            yield return "Output.WriteBufferBytes must be >= 4096.";
    }
}

public sealed class LimitsConfig
{
    /// <summary>Rows per CSV file before rolling to the next. 0 = never roll.</summary>
    public long MaxRecordsPerFile { get; set; } = 1_000_000;

    /// <summary>Hard ceiling on total rows across all files. 0 = unlimited. This is the demo cap.</summary>
    public long MaxTotalRecords { get; set; }

    /// <summary>Write a checkpoint after each completed page so an interrupted run can resume.</summary>
    public bool EnableCheckpoint { get; set; } = true;

    public IEnumerable<string> Validate()
    {
        if (MaxRecordsPerFile < 0) yield return "Limits.MaxRecordsPerFile must be >= 0 (0 = never roll).";
        if (MaxTotalRecords < 0) yield return "Limits.MaxTotalRecords must be >= 0 (0 = unlimited).";
    }
}

public sealed class FieldMapping
{
    /// <summary>CSV column header.</summary>
    public string Column { get; set; } = "";

    /// <summary>
    /// Extraction path into the record JSON. Supports the <c>field:Name</c> shorthand.
    /// See <see cref="Export.FieldExtractor"/> for the full grammar.
    /// </summary>
    public string Path { get; set; } = "";

    /// <summary>Emitted when the path resolves to nothing.</summary>
    public string Default { get; set; } = "";

    [JsonIgnore]
    public Export.CompiledPath? Compiled { get; set; }
}
