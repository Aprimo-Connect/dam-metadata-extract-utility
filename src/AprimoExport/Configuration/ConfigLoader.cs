using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AprimoExport.Configuration;

/// <summary>
/// Builds an <see cref="ExportConfig"/> from appsettings.json, then environment
/// variables, then command-line switches — later sources win.
/// </summary>
public static class ConfigLoader
{
    public const string DefaultConfigFileName = "appsettings.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    /// <summary>
    /// Finds the config file: an explicit <c>--config</c> path, then the current
    /// directory, then the directory holding the executable. The last of those matters
    /// because <c>dotnet run</c> keeps the caller's working directory — without it,
    /// running from the repo root would silently ignore the shipped config. Returns
    /// null when no file exists anywhere.
    /// </summary>
    private static string? ResolveConfigPath(string? explicitPath)
    {
        if (explicitPath is not null)
        {
            // An explicitly named file that does not exist is an error, not a fallback.
            if (!File.Exists(explicitPath))
                throw new FileNotFoundException(
                    $"Config file not found: '{Path.GetFullPath(explicitPath)}'.", explicitPath);

            return explicitPath;
        }

        if (File.Exists(DefaultConfigFileName))
            return DefaultConfigFileName;

        var besideExecutable = Path.Combine(AppContext.BaseDirectory, DefaultConfigFileName);
        return File.Exists(besideExecutable) ? besideExecutable : null;
    }

    public static ExportConfig Load(CommandLineArgs args, Action<string> log)
    {
        var configPath = ResolveConfigPath(args.ConfigPath);
        var config = LoadFile(configPath, log);
        ApplyEnvironment(config, log);
        ApplyCommandLine(config, args, log);
        // Credential-store queries never touch columns, so skip parsing the field list
        // for them — otherwise `--list-credentials` buries its answer under the
        // column-loading chatter. --save-credentials still needs it, because it goes
        // through full config validation.
        if (!args.ListCredentials && !args.ClearCredentials)
            ApplyFieldsFile(config, configPath, log);

        return config;
    }

    /// <summary>
    /// Replaces the inline <c>Fields</c> list with an external file when one is
    /// configured. Runs last so <c>--fields-file</c> wins over the config file.
    /// </summary>
    private static void ApplyFieldsFile(ExportConfig config, string? configPath, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(config.FieldsFile)) return;

        var path = ResolveDataFilePath(config.FieldsFile!, configPath);
        var loaded = FieldListLoader.Load(path, log);

        if (config.Fields.Count > 0)
            log($"  Replacing the {config.Fields.Count} inline Fields entr(ies) with {loaded.Count} " +
                "from the field list file.");

        config.Fields = loaded;
    }

    /// <summary>
    /// Resolves a data file referenced from config: as given, then beside the config
    /// file, then beside the executable. Sitting next to the config is the intuitive
    /// place to keep it, and beside the executable is where the published layout puts it
    /// — while the working directory may be anywhere.
    /// </summary>
    private static string ResolveDataFilePath(string path, string? configPath)
    {
        if (File.Exists(path)) return path;
        if (Path.IsPathRooted(path)) return path;

        if (configPath is not null)
        {
            var configDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath));
            if (configDirectory is not null)
            {
                var besideConfig = Path.Combine(configDirectory, path);
                if (File.Exists(besideConfig)) return besideConfig;
            }
        }

        var besideExecutable = Path.Combine(AppContext.BaseDirectory, path);
        if (File.Exists(besideExecutable)) return besideExecutable;

        // Let the loader raise a consistent, path-qualified error.
        return path;
    }

    private static ExportConfig LoadFile(string? path, Action<string> log)
    {
        if (path is null)
        {
            log($"No {DefaultConfigFileName} found in {Directory.GetCurrentDirectory()} " +
                $"or next to the executable ({AppContext.BaseDirectory}) — " +
                "using defaults plus environment variables and command-line switches.");
            return new ExportConfig();
        }

        try
        {
            var config = JsonSerializer.Deserialize<ExportConfig>(File.ReadAllText(path), JsonOptions)
                         ?? new ExportConfig();
            log($"Loaded config from {Path.GetFullPath(path)}");
            return config;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Config file '{Path.GetFullPath(path)}' is not valid JSON: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Credentials belong in the environment, not in a file that might be committed.
    /// </summary>
    private static void ApplyEnvironment(ExportConfig config, Action<string> log)
    {
        var applied = new List<string>();

        Set("APRIMO_TENANT", v => { config.Aprimo.Tenant = v; applied.Add("Tenant"); });
        Set("APRIMO_CLIENT_ID", v => { config.Aprimo.ClientId = v; applied.Add("ClientId"); });
        Set("APRIMO_CLIENT_SECRET", v => { config.Aprimo.ClientSecret = v; applied.Add("ClientSecret"); });
        Set("APRIMO_API_BASE_URL", v => { config.Aprimo.ApiBaseUrl = v; applied.Add("ApiBaseUrl"); });
        Set("APRIMO_TOKEN_URL", v => { config.Aprimo.TokenUrl = v; applied.Add("TokenUrl"); });
        Set("APRIMO_SCOPE", v => { config.Aprimo.Scope = v; applied.Add("Scope"); });

        if (applied.Count > 0)
            log($"Applied from environment: {string.Join(", ", applied)}");

        // Flag placeholders at the point they enter, rather than leaving the puzzle to a
        // later invalid_client. The environment is the least visible source because it
        // silently outranks the config file.
        WarnIfPlaceholder("APRIMO_CLIENT_ID", config.Aprimo.ClientId);
        WarnIfPlaceholder("APRIMO_TENANT", config.Aprimo.Tenant);
        if (!string.IsNullOrWhiteSpace(config.Aprimo.ClientSecret) &&
            PlaceholderDetection.LooksLikePlaceholder(config.Aprimo.ClientSecret))
            log("Warning: the client secret is a documentation placeholder, not a real value.");

        void WarnIfPlaceholder(string envVar, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (!PlaceholderDetection.LooksLikePlaceholder(value)) return;

            log($"Warning: '{value}' is a documentation placeholder, not a real value. " +
                $"If it came from {envVar}, that overrides appsettings.json — clear it with " +
                $"[Environment]::SetEnvironmentVariable('{envVar}',$null,'User') and open a new terminal.");
        }

        static void Set(string name, Action<string> assign)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value)) assign(value.Trim());
        }
    }

    private static void ApplyCommandLine(ExportConfig config, CommandLineArgs args, Action<string> log)
    {
        var applied = new List<string>();

        if (args.Tenant is { } tenant) { config.Aprimo.Tenant = tenant; applied.Add("Tenant"); }
        if (args.ClientId is { } id) { config.Aprimo.ClientId = id; applied.Add("ClientId"); }
        if (args.ClientSecret is { } secret) { config.Aprimo.ClientSecret = secret; applied.Add("ClientSecret"); }
        if (args.Languages is { } lang) { config.Aprimo.Languages = lang; applied.Add("Languages"); }
        if (args.ClientAuthStyle is { } authStyle) { config.Aprimo.ClientAuthStyle = authStyle; applied.Add("ClientAuthStyle"); }

        if (args.RequestsPerSecond is { } rps) { config.Throttle.RequestsPerSecond = rps; applied.Add("RequestsPerSecond"); }
        if (args.Burst is { } burst) { config.Throttle.Burst = burst; applied.Add("Burst"); }

        if (args.PageSize is { } pageSize) { config.Source.Paging.PageSize = pageSize; applied.Add("PageSize"); }
        if (args.PagingMode is { } pagingMode) { config.Source.Paging.Mode = pagingMode; applied.Add("PagingMode"); }
        if (args.KeysetProperty is { } keyset) { config.Source.Paging.KeysetProperty = keyset; applied.Add("KeysetProperty"); }
        if (args.MaxPages is { } maxPages) { config.Source.Paging.MaxPages = maxPages; applied.Add("MaxPages"); }

        if (args.SourceMode is { } mode) { config.Source.Mode = mode; applied.Add("SourceMode"); }

        // --record implies SingleRecord mode, unless a mode was named explicitly.
        if (args.RecordId is { } recordId)
        {
            config.Source.RecordId = recordId;
            if (args.SourceMode is null) config.Source.Mode = SourceMode.SingleRecord;
            applied.Add("RecordId");
        }
        if (args.Expression is { } expression) { config.Source.SearchExpression = expression; applied.Add("SearchExpression"); }
        if (args.Filter is { } filter) { config.Source.Filter = filter; applied.Add("Filter"); }
        if (args.Sort is { } sort) { config.Source.Sort = sort; applied.Add("Sort"); }
        if (args.SelectRecord is { } select) { config.Source.SelectRecord = select; applied.Add("SelectRecord"); }

        if (args.MaxTotalRecords is { } maxRows) { config.Limits.MaxTotalRecords = maxRows; applied.Add("MaxTotalRecords"); }
        if (args.MaxRecordsPerFile is { } perFile) { config.Limits.MaxRecordsPerFile = perFile; applied.Add("MaxRecordsPerFile"); }

        if (args.FieldsFile is { } fieldsFile) { config.FieldsFile = fieldsFile; applied.Add("FieldsFile"); }
        if (args.OutputDirectory is { } dir) { config.Output.Directory = dir; applied.Add("OutputDirectory"); }
        if (args.FilePrefix is { } prefix) { config.Output.FilePrefix = prefix; applied.Add("FilePrefix"); }
        if (args.Delimiter is { } delimiter) { config.Output.Delimiter = delimiter; applied.Add("Delimiter"); }
        if (args.Overwrite) { config.Output.Overwrite = true; applied.Add("Overwrite"); }

        if (applied.Count > 0)
            log($"Applied from command line: {string.Join(", ", applied)}");
    }
}

/// <summary>Parsed command-line switches. Null means "not specified — keep the configured value".</summary>
public sealed class CommandLineArgs
{
    public string? ConfigPath { get; private set; }
    public string? Tenant { get; private set; }
    public string? ClientId { get; private set; }
    public string? ClientSecret { get; private set; }
    public string? Languages { get; private set; }
    public ClientAuthStyle? ClientAuthStyle { get; private set; }

    public double? RequestsPerSecond { get; private set; }
    public int? Burst { get; private set; }

    public int? PageSize { get; private set; }
    public PagingMode? PagingMode { get; private set; }
    public string? KeysetProperty { get; private set; }
    public int? MaxPages { get; private set; }

    public SourceMode? SourceMode { get; private set; }
    public string? Expression { get; private set; }
    public string? Filter { get; private set; }

    /// <summary>Export or inspect exactly one record by ID. Implies SourceMode.SingleRecord.</summary>
    public string? RecordId { get; private set; }
    public string? Sort { get; private set; }

    /// <summary>Start of the delta window: last | yesterday | today | 1d | 2026-08-05 | instant.</summary>
    public string? Since { get; private set; }

    /// <summary>Optional end of the delta window. Defaults to the run's start time.</summary>
    public string? Until { get; private set; }

    /// <summary>Forget the saved high-water mark for this tenant and query.</summary>
    public bool ResetDelta { get; private set; }

    public string? SelectRecord { get; private set; }

    public long? MaxTotalRecords { get; private set; }
    public long? MaxRecordsPerFile { get; private set; }

    public string? FieldsFile { get; private set; }
    public string? OutputDirectory { get; private set; }
    public string? FilePrefix { get; private set; }
    public string? Delimiter { get; private set; }

    public bool Overwrite { get; private set; }
    public bool Resume { get; private set; }

    /// <summary>Prompt for client ID and secret even if they are already configured.</summary>
    public bool PromptCredentials { get; private set; }

    /// <summary>Prompt, verify against the token endpoint, then store DPAPI-encrypted.</summary>
    public bool SaveCredentials { get; private set; }

    /// <summary>Remove saved credentials for this tenant, or all of them with --all.</summary>
    public bool ClearCredentials { get; private set; }

    /// <summary>Widens --clear-credentials to every tenant.</summary>
    public bool AllTenants { get; private set; }

    /// <summary>List tenants that have saved credentials.</summary>
    public bool ListCredentials { get; private set; }

    public bool ValidateOnly { get; private set; }

    /// <summary>Number of records to fetch for the --sample diagnostic; null when not requested.</summary>
    public int? SampleSize { get; private set; }

    public bool ShowHelp { get; private set; }
    public bool Verbose { get; private set; }

    public static CommandLineArgs Parse(string[] argv)
    {
        var args = new CommandLineArgs();

        for (var i = 0; i < argv.Length; i++)
        {
            var arg = argv[i];

            switch (arg.ToLowerInvariant())
            {
                case "-h" or "--help" or "/?":
                    args.ShowHelp = true;
                    break;

                case "--overwrite": args.Overwrite = true; break;
                case "--resume": args.Resume = true; break;
                case "--prompt-credentials" or "--login": args.PromptCredentials = true; break;
                case "--save-credentials": args.SaveCredentials = true; break;
                case "--clear-credentials": args.ClearCredentials = true; break;
                case "--list-credentials": args.ListCredentials = true; break;
                case "--all": args.AllTenants = true; break;
                case "--validate-only": args.ValidateOnly = true; break;

                case "--sample":
                    // The count is optional: "--sample" alone means 3.
                    if (i + 1 < argv.Length &&
                        int.TryParse(argv[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
                    {
                        i++;
                        if (size < 1)
                            throw new ArgumentException("Option '--sample' expects a count of 1 or more.");
                        args.SampleSize = size;
                    }
                    else
                    {
                        args.SampleSize = 3;
                    }
                    break;

                case "--verbose" or "-v": args.Verbose = true; break;

                case "--config" or "-c": args.ConfigPath = Next(argv, ref i, arg); break;
                case "--tenant": args.Tenant = Next(argv, ref i, arg); break;
                case "--client-id": args.ClientId = Next(argv, ref i, arg); break;
                case "--client-secret": args.ClientSecret = Next(argv, ref i, arg); break;
                case "--languages": args.Languages = Next(argv, ref i, arg); break;
                case "--client-auth-style":
                    args.ClientAuthStyle = ParseEnum<ClientAuthStyle>(Next(argv, ref i, arg), arg);
                    break;

                case "--rps": args.RequestsPerSecond = ParseDouble(Next(argv, ref i, arg), arg); break;
                case "--burst": args.Burst = ParseInt(Next(argv, ref i, arg), arg); break;

                case "--page-size": args.PageSize = ParseInt(Next(argv, ref i, arg), arg); break;
                case "--paging": args.PagingMode = ParseEnum<PagingMode>(Next(argv, ref i, arg), arg); break;
                case "--keyset-property": args.KeysetProperty = Next(argv, ref i, arg); break;
                case "--max-pages": args.MaxPages = ParseInt(Next(argv, ref i, arg), arg); break;

                case "--mode": args.SourceMode = ParseEnum<SourceMode>(Next(argv, ref i, arg), arg); break;
                case "--expression" or "-e": args.Expression = Next(argv, ref i, arg); break;
                case "--filter": args.Filter = Next(argv, ref i, arg); break;
                case "--record" or "--record-id": args.RecordId = Next(argv, ref i, arg); break;
                case "--sort": args.Sort = Next(argv, ref i, arg); break;
                case "--since": args.Since = Next(argv, ref i, arg); break;
                case "--until": args.Until = Next(argv, ref i, arg); break;
                case "--reset-delta": args.ResetDelta = true; break;
                case "--daily":
                    // Shorthand for the standing daily job: resume where the last run ended.
                    args.Since = "last";
                    break;
                case "--select-record": args.SelectRecord = Next(argv, ref i, arg); break;

                case "--max-rows": args.MaxTotalRecords = ParseLong(Next(argv, ref i, arg), arg); break;
                case "--max-per-file": args.MaxRecordsPerFile = ParseLong(Next(argv, ref i, arg), arg); break;

                case "--fields-file" or "-f": args.FieldsFile = Next(argv, ref i, arg); break;
                case "--out" or "-o": args.OutputDirectory = Next(argv, ref i, arg); break;
                case "--prefix": args.FilePrefix = Next(argv, ref i, arg); break;
                case "--delimiter": args.Delimiter = ParseDelimiter(Next(argv, ref i, arg)); break;

                default:
                    throw new ArgumentException($"Unknown option '{arg}'. Run with --help for usage.");
            }
        }

        return args;
    }

    private static string Next(string[] argv, ref int i, string option)
    {
        if (i + 1 >= argv.Length)
            throw new ArgumentException($"Option '{option}' requires a value.");
        return argv[++i];
    }

    private static int ParseInt(string value, string option) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : throw new ArgumentException($"Option '{option}' expects an integer, got '{value}'.");

    private static long ParseLong(string value, string option) =>
        long.TryParse(value.Replace("_", "").Replace(",", ""),
                      NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : throw new ArgumentException($"Option '{option}' expects an integer, got '{value}'.");

    private static double ParseDouble(string value, string option) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            ? n
            : throw new ArgumentException($"Option '{option}' expects a number, got '{value}'.");

    private static TEnum ParseEnum<TEnum>(string value, string option) where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed)
            ? parsed
            : throw new ArgumentException(
                $"Option '{option}' expects one of: {string.Join(", ", Enum.GetNames<TEnum>())}. Got '{value}'.");

    private static string ParseDelimiter(string value) => value.ToLowerInvariant() switch
    {
        "tab" or "\\t" => "\t",
        "pipe" => "|",
        "semicolon" => ";",
        "comma" => ",",
        _ => value
    };

    public static string Usage => """
        aprimo-export — export Aprimo DAM record metadata to CSV

        USAGE
          aprimo-export [options]

        CONFIG
          -c, --config <path>        Config file (default: appsettings.json)
              --tenant <name>        Aprimo tenant (subdomain of your DAM URL)
              --client-id <id>       OAuth client ID      (prefer env APRIMO_CLIENT_ID)
              --client-secret <s>    OAuth client secret  (prefer env APRIMO_CLIENT_SECRET)
              --prompt-credentials   Type the ID and secret at a prompt (secret masked,
                                     never saved). Also aliased as --login. Prompting
                                     happens automatically when they are missing.
              --save-credentials     Prompt, verify against the token endpoint, then store
                                     encrypted with Windows DPAPI under %LOCALAPPDATA% so
                                     later runs need no credentials at all. Only your
                                     Windows account on this machine can decrypt it.
              --list-credentials     List tenants with saved credentials (never secrets)
              --clear-credentials    Remove saved credentials for this tenant (--all for
                                     every tenant)
              --languages <v>        'languages' header: an ID, a comma-separated list, or *
              --client-auth-style <s>  Basic (Authorization header, default) or Body
                                     (client_id/client_secret in the form). Try Body if
                                     you get invalid_client with credentials you trust.

        WHAT TO EXPORT
              --mode <m>             Search (POST /search/records), Records (GET /records),
                                     or SingleRecord (GET /record/{id})
              --record <id>          Target exactly one record by ID, via GET /record/{id}.
                                     Implies --mode SingleRecord and needs no expression or
                                     paging. Accepts the hyphenated GUID form. Combine with
                                     --sample to inspect it, or run alone to write a 1-row
                                     CSV. The quickest way to test a field mapping.
          -e, --expression <expr>    Search expression, e.g. "ContentType = 'Asset'"
              --filter <expr>        Filter expression (Records mode)
              --sort <field>         Sort field; prefix with - for descending
              --select-record <v>    select-record header (default: fields,masterfilelatestversion)

        INCREMENTAL (DELTA) EXPORT
              --daily                Shorthand for --since last: export everything changed
                                     since the previous successful run. The standing
                                     daily job.
              --since <spec>         Start of the window: last | yesterday | today | a span
                                     like 1d / 36h / 90m | a UTC date like 2026-08-05 | a
                                     full instant like 2026-08-05T04:00:00Z
              --until <spec>         End of the window. Defaults to this run's start time.
              --reset-delta          Forget the saved mark for this tenant and query

              All boundaries are UTC because the API's ModifiedOn is UTC. Windows are
              half-open [since, until) and chained, so consecutive runs neither overlap
              nor leave gaps. The mark advances only after a run that completes without
              cancellation and without hitting the row cap.

        RATE LIMITING
              --rps <n>              Requests per second; fractional allowed, 0 = unlimited
              --burst <n>            Token-bucket depth (1 = strictly even spacing)

        PAGING
              --page-size <n>        Records per request, max 1000 (API limit)
              --paging <m>           Offset | PageNumber | Keyset
              --keyset-property <p>  Watermark property for Keyset paging (default: CreatedOn)
              --max-pages <n>        Stop after n pages (0 = unlimited)

        LIMITS
              --max-rows <n>         Total row cap across all files (0 = unlimited)
              --max-per-file <n>     Rows per CSV before rolling to a new file (0 = never)

        COLUMNS
          -f, --fields-file <path>   Read the column list from a file, replacing the
                                     Fields list in the config. Field names separated by
                                     newlines, commas, semicolons or tabs. '#' comments.
                                     'Column => path' for an explicit mapping. An Aprimo
                                     Data Exports schema XML file is also accepted.

        OUTPUT
          -o, --out <dir>            Output directory
              --prefix <name>        File name prefix (files land as prefix_0001.csv)
              --delimiter <d>        comma | tab | semicolon | pipe, or a literal string
              --overwrite            Replace existing export files with this prefix

        RUN CONTROL
              --resume               Continue from the checkpoint in the output directory
              --validate-only        Check config and authentication, then exit
              --sample [n]           Fetch n records (default 3) and report which language
                                     IDs the tenant returns and which columns resolve.
                                     One request; writes no CSV. Run this first.
          -v, --verbose              Verbose logging
          -h, --help                 Show this help

        EXAMPLES
          # 500-row demo at 2 requests/second
          aprimo-export --max-rows 500 --rps 2

          # Full export, 250k rows per file, keyset paging for depth
          aprimo-export --paging Keyset --page-size 1000 --max-per-file 250000 --rps 8

          # Released assets only
          aprimo-export -e "ContentType = 'Asset' AND Status = 'Released'"
        """;
}
