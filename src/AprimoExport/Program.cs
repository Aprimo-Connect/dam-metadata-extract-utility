using System.Diagnostics;
using System.Net;
using AprimoExport.Auth;
using AprimoExport.Configuration;
using AprimoExport.Export;
using AprimoExport.Http;

namespace AprimoExport;

public static class Program
{
    private const int ExitOk = 0;
    private const int ExitError = 1;
    private const int ExitInvalidConfig = 2;
    private const int ExitCancelled = 3;

    public static async Task<int> Main(string[] argv)
    {
        var verbose = false;
        void Log(string message) =>
            Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
        void Trace(string message) { if (verbose) Log(message); }

        CommandLineArgs args;
        try
        {
            args = CommandLineArgs.Parse(argv);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitInvalidConfig;
        }

        if (args.ShowHelp)
        {
            Console.WriteLine(CommandLineArgs.Usage);
            return ExitOk;
        }

        verbose = args.Verbose;

        ExportConfig config;
        try
        {
            config = ConfigLoader.Load(args, Log);
        }
        catch (Exception ex)
        {
            Log("Configuration error: " + ex.Message);
            return ExitInvalidConfig;
        }

        // Credential-store management runs before anything else and then exits: these
        // commands are about the store, not about exporting.
        if (args.ListCredentials)
        {
            var tenants = CredentialStore.ListTenants(Log);
            Log($"Credential store: {CredentialStore.DefaultStorePath}");
            if (tenants.Count == 0)
                Log("No saved credentials. Run --save-credentials to add some.");
            else
                foreach (var t in tenants) Console.WriteLine("  " + t);
            return ExitOk;
        }

        if (args.ClearCredentials)
        {
            var target = args.AllTenants ? null : config.Aprimo.Tenant;
            return CredentialStore.Clear(target, Log) ? ExitOk : ExitError;
        }

        // Fill gaps from the encrypted store, unless we are about to overwrite it.
        if (!args.SaveCredentials)
            CredentialStore.TryFill(config.Aprimo, Log);

        // Then offer interactive entry, so missing credentials become a prompt rather
        // than an error — but only when a real console is attached.
        CredentialPrompt.EnsureCredentials(config.Aprimo, args.PromptCredentials || args.SaveCredentials, Log);

        if (args.ResetDelta && !DeltaStateStore.Reset(config, Log) && args.Since is null)
            return ExitOk;

        // Resolve the delta window BEFORE validation: it supplies the search expression,
        // and validation rejects a Search-mode run that has none. Keyset paging later
        // appends to whatever we produce here.
        DeltaWindow? deltaWindow = null;
        if (args.Since is { } sinceSpec)
        {
            try
            {
                var state = DeltaStateStore.Load(config, Log);
                deltaWindow = DeltaWindowParser.Create(
                    sinceSpec, args.Until, config.Source.Delta, state, DateTimeOffset.UtcNow, Log);

                config.Source.SearchExpression = deltaWindow.Compose(config.Source.SearchExpression);

                if (state.RunCount > 0)
                    Log($"Previous delta runs: {state.RunCount}, {state.TotalRows:N0} rows total, " +
                        $"last {state.LastRunUtc:u} ({state.LastRunRows:N0} rows).");
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                Log("Delta window could not be resolved: " + ex.Message);
                return ExitInvalidConfig;
            }
        }

        // --sample is a diagnostic: it must not demand that you first construct a query.
        // GET /records needs no expression, so use that when nothing supplied one.
        if (args.SampleSize is not null &&
            config.Source.Mode == SourceMode.Search &&
            args.RecordId is null &&
            string.IsNullOrWhiteSpace(config.Source.SearchExpression))
        {
            config.Source.Mode = SourceMode.Records;
            Log("Sampling via GET /records, because POST /search/records requires an expression and " +
                "none is set. Add --since or -e to sample the exact query the export will run.");
        }

        var errors = config.Validate().ToList();
        if (errors.Count > 0)
        {
            Log($"Configuration is invalid ({errors.Count} problem(s)):");
            foreach (var error in errors) Console.Error.WriteLine("  - " + error);
            Console.Error.WriteLine();
            Console.Error.WriteLine("Run with --help for available options.");
            return ExitInvalidConfig;
        }

        // Ctrl+C flushes and checkpoints rather than killing the process mid-write.
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            if (cts.IsCancellationRequested) return; // Second Ctrl+C: let the runtime terminate.
            e.Cancel = true;
            Log("Ctrl+C received — stopping after the current page. Press Ctrl+C again to abort immediately.");
            cts.Cancel();
        };

        PrintPlan(config, args, Log);

        if (deltaWindow is not null)
            Log($"Delta window     : {deltaWindow.Describe()}");

        using var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = Math.Max(4, config.Throttle.MaxConcurrentRequests * 2)
        };

        using var apiHttp = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(config.Source.RequestTimeoutSeconds)
        };
        apiHttp.DefaultRequestHeaders.UserAgent.ParseAdd("aprimo-export/1.0");

        // Separate client for the token host: different endpoint, different timeout profile.
        using var authHttp = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        authHttp.DefaultRequestHeaders.UserAgent.ParseAdd("aprimo-export/1.0");

        using var tokenProvider = new OAuthTokenProvider(authHttp, config.Aprimo, config.Source.Retry, Log);
        using var limiter = new RateLimiter(
            config.Throttle.RequestsPerSecond,
            config.Throttle.Burst,
            config.Throttle.MaxConcurrentRequests);

        var client = new ApiClient(apiHttp, tokenProvider, limiter, config, Trace);

        try
        {
            if (args.SaveCredentials)
            {
                if (!CredentialStore.IsSupported)
                {
                    Log("--save-credentials needs Windows DPAPI, which is not available here. " +
                        "Use APRIMO_CLIENT_ID / APRIMO_CLIENT_SECRET instead.");
                    return ExitError;
                }

                // Verify before storing, so a mistyped secret is never persisted.
                Log("Verifying the credentials against the token endpoint before saving…");
                await tokenProvider.GetAccessTokenAsync(cts.Token).ConfigureAwait(false);
                Log("Verified.");

                CredentialStore.Save(
                    config.Aprimo.Tenant, config.Aprimo.ClientId, config.Aprimo.ClientSecret, Log);

                Log("Later runs need no credentials — just 'aprimo-export --sample 3'.");
                return ExitOk;
            }

            if (args.ValidateOnly)
            {
                Log("Validating configuration and authentication…");
                var token = await tokenProvider.GetAccessTokenAsync(cts.Token).ConfigureAwait(false);
                Log($"Authentication succeeded (token length {token.Length}, " +
                    $"expires {tokenProvider.ExpiresAt:u}).");
                Log("Configuration is valid. Exiting because --validate-only was given.");
                return ExitOk;
            }

            if (args.SampleSize is { } sampleSize)
            {
                Log($"Fetching {sampleSize} record(s) for inspection — no CSV will be written.");
                var inspector = new SampleInspector(config, client, Log);
                var found = await inspector.RunAsync(sampleSize, cts.Token).ConfigureAwait(false);
                return found ? ExitOk : ExitError;
            }

            var runner = new ExportRunner(config, client, Log);
            var result = await runner.RunAsync(args.Resume, cts.Token).ConfigureAwait(false);

            PrintSummary(result, config, tokenProvider, Log);

            // Advance the delta mark only when the window was genuinely exhausted.
            // A cancelled run, or one cut short by the row cap, has not covered its
            // window — moving the mark would silently skip the remainder.
            if (deltaWindow is not null)
            {
                if (result.Cancelled)
                    Log("Delta mark NOT advanced: the run was cancelled, so the window is incomplete. " +
                        "Re-run with the same --since to finish it.");
                else if (result.HitTotalCap)
                    Log($"Delta mark NOT advanced: the run stopped at the {config.Limits.MaxTotalRecords:N0}-row " +
                        "cap, so the window is incomplete. Raise or remove --max-rows for a real delta run.");
                else
                    DeltaStateStore.Save(config, deltaWindow, result.RowsWritten, Log);
            }

            return result.Cancelled ? ExitCancelled : ExitOk;
        }
        catch (AuthenticationFailedException ex)
        {
            Log("Authentication failed.");
            Console.Error.WriteLine(ex.Message);
            return ExitError;
        }
        catch (ApiException ex)
        {
            Log("The API request failed.");
            Console.Error.WriteLine(ex.Message);
            return ExitError;
        }
        catch (FormatException ex)
        {
            Log("A field mapping path is invalid: " + ex.Message);
            return ExitInvalidConfig;
        }
        catch (InvalidOperationException ex)
        {
            Log(ex.Message);
            return ExitError;
        }
        catch (OperationCanceledException)
        {
            Log("Cancelled.");
            return ExitCancelled;
        }
        catch (Exception ex)
        {
            Log("Unexpected error: " + ex);
            return ExitError;
        }
    }

    private static void PrintPlan(ExportConfig config, CommandLineArgs args, Action<string> log)
    {
        var throttle = config.Throttle;
        var paging = config.Source.Paging;
        var limits = config.Limits;

        log($"Tenant endpoint : {config.Aprimo.ResolvedApiBaseUrl}");
        log($"Token endpoint  : {config.Aprimo.ResolvedTokenUrl}");
        log($"Source          : {config.Source.Mode} " +
            (config.Source.Mode switch
            {
                SourceMode.Search => $"expression=\"{config.Source.SearchExpression ?? "(all records)"}\"",
                SourceMode.SingleRecord =>
                    $"record {SourceConfig.NormalizeRecordId(config.Source.RecordId ?? "")}" +
                    $" (GET /record/{{id}})",
                _ => $"filter=\"{config.Source.Filter ?? "(none)"}\""
            }));
        log($"Paging          : {paging.Mode}, page size {paging.PageSize}" +
            (paging.Mode == PagingMode.Keyset ? $", watermark {paging.KeysetProperty}" : "") +
            $", sort {(paging.Mode == PagingMode.Keyset ? paging.KeysetProperty : config.Source.Sort)}");
        log($"Rate limit      : " +
            (throttle.RequestsPerSecond > 0
                ? $"{throttle.RequestsPerSecond:0.##} req/s, burst {throttle.Burst}, " +
                  $"max {throttle.MaxConcurrentRequests} concurrent"
                : "disabled (no client-side throttling)"));
        log($"Row limits      : " +
            (limits.MaxTotalRecords > 0 ? $"total cap {limits.MaxTotalRecords:N0}" : "no total cap") +
            ", " +
            (limits.MaxRecordsPerFile > 0 ? $"{limits.MaxRecordsPerFile:N0} rows/file" : "single file"));
        log($"Output          : {Path.GetFullPath(config.Output.Directory)}\\{config.Output.FilePrefix}_NNNN.csv");
        log($"Columns         : {config.Fields.Count} — {string.Join(", ", config.Fields.Take(8).Select(f => f.Column))}" +
            (config.Fields.Count > 8 ? ", …" : ""));

        if (limits.MaxTotalRecords > 0)
            log("Note: a total row cap is in effect — this run will not export the full data set.");

        if (paging.Mode != PagingMode.Keyset && limits.MaxTotalRecords == 0)
            log($"Note: {paging.Mode} paging can hit a deep-paging ceiling on large sets. " +
                "If the export stops early or errors past a certain depth, switch to --paging Keyset.");

        if (args.Resume)
            log("Resume requested — will continue from the checkpoint if one matches this configuration.");
    }

    private static void PrintSummary(
        ExportResult result,
        ExportConfig config,
        OAuthTokenProvider tokens,
        Action<string> log)
    {
        var seconds = Math.Max(result.Elapsed.TotalSeconds, 0.001);

        Console.Error.WriteLine();
        log("──────── Export summary ────────");
        log($"Rows written    : {result.RowsWritten:N0}");
        log($"Files created   : {result.FilesCreated}");
        log($"Elapsed         : {result.Elapsed:hh\\:mm\\:ss}");
        log($"Throughput      : {result.RowsWritten / seconds:F0} rows/s, " +
            $"{result.RequestCount / seconds:F2} req/s effective");
        log($"API requests    : {result.RequestCount:N0} ({result.RetryCount:N0} retried)");
        log($"Payload         : {FormatBytes(result.BytesReceived)}");
        log($"Token requests  : {tokens.TokenRequestCount}");

        if (result.ApproximateTotalAvailable is { } total)
            log($"Server total    : ~{total:N0} (approximate per the API spec)");

        if (result.HitTotalCap)
            log($"Stopped early   : total cap of {config.Limits.MaxTotalRecords:N0} rows reached.");

        if (result.Cancelled)
            log("Stopped early   : cancelled by user. Re-run with --resume to continue.");

        if (result.FilePaths.Count > 0)
        {
            log("Output files:");
            foreach (var path in result.FilePaths.Take(10))
            {
                var info = new FileInfo(path);
                Console.Error.WriteLine($"  {path}  ({FormatBytes(info.Exists ? info.Length : 0)})");
            }
            if (result.FilePaths.Count > 10)
                Console.Error.WriteLine($"  … and {result.FilePaths.Count - 10} more");
        }
        else
        {
            log("No rows matched — no files were written.");
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }
}
