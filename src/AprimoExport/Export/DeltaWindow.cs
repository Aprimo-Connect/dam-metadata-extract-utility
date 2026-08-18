using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AprimoExport.Configuration;

namespace AprimoExport.Export;

/// <summary>
/// A half-open <c>[Since, Until)</c> time window over the delta property, plus the
/// search predicate that expresses it.
///
/// <para>Windows are chained rather than derived from the data: the next run starts
/// where this one ended. That is gap-free by construction, and immune to records being
/// edited mid-run — those simply land in the following window. Deriving the next start
/// from <c>max(ModifiedOn)</c> observed would instead lose anything stamped below that
/// maximum while the run was in flight.</para>
/// </summary>
public sealed class DeltaWindow
{
    public DateTimeOffset Since { get; }
    public DateTimeOffset Until { get; }

    /// <summary>Record property compared, e.g. <c>ModifiedOn</c>.</summary>
    public string Property { get; }

    /// <summary>True when <see cref="Since"/> came from the persisted high-water mark.</summary>
    public bool FromState { get; }

    /// <summary>How the window was requested, for logging.</summary>
    public string Origin { get; }

    public DeltaWindow(
        DateTimeOffset since,
        DateTimeOffset until,
        string property,
        bool fromState,
        string origin)
    {
        if (until <= since)
            throw new ArgumentException(
                $"Delta window is empty or inverted: {SearchExpressions.FormatInstant(since)} " +
                $"to {SearchExpressions.FormatInstant(until)}. " +
                "Check --since / --until, or use --reset-delta if a saved mark is in the future.");

        Since = since;
        Until = until;
        Property = property;
        FromState = fromState;
        Origin = origin;
    }

    public TimeSpan Duration => Until - Since;

    /// <summary>Half-open so adjacent windows neither overlap nor leave a gap.</summary>
    public string ToPredicate() =>
        $"{Property} >= {SearchExpressions.FormatInstant(Since)} AND " +
        $"{Property} < {SearchExpressions.FormatInstant(Until)}";

    public string Compose(string? baseExpression) =>
        SearchExpressions.And(baseExpression, ToPredicate());

    public string Describe() =>
        $"{SearchExpressions.FormatInstant(Since)} .. {SearchExpressions.FormatInstant(Until)} " +
        $"({Duration.TotalHours:F1}h, {Property}, {Origin})";
}

/// <summary>
/// Parses <c>--since</c> / <c>--until</c> specifications into a <see cref="DeltaWindow"/>.
///
/// <para>Accepted forms: <c>last</c> (resume from the saved mark), <c>yesterday</c>,
/// <c>today</c>, a relative span such as <c>1d</c> / <c>36h</c> / <c>90m</c>, a date
/// (<c>2026-08-05</c>), or a full instant (<c>2026-08-05T04:00:00Z</c>).</para>
///
/// <para>All boundaries are UTC, because the API's <c>ModifiedOn</c> is UTC — the schema
/// maps it from <c>ModifiedOnUtc</c>. A bare date therefore means a UTC day, not a local
/// one; pass an explicit instant if you need local-midnight boundaries.</para>
/// </summary>
public static class DeltaWindowParser
{
    public static DeltaWindow Create(
        string sinceSpec,
        string? untilSpec,
        DeltaConfig config,
        DeltaState state,
        DateTimeOffset now,
        Action<string> log)
    {
        var property = config.Property;
        var overlap = TimeSpan.FromMinutes(Math.Max(0, config.OverlapMinutes));

        // The upper bound defaults to "now", so the window closes at this run's start.
        var until = untilSpec is null ? now : ParseInstant(untilSpec, now, "--until", endOfDay: true);

        DateTimeOffset since;
        var fromState = false;
        var origin = sinceSpec;

        if (sinceSpec.Equals("last", StringComparison.OrdinalIgnoreCase) ||
            sinceSpec.Equals("resume", StringComparison.OrdinalIgnoreCase))
        {
            if (state.HighWaterMarkUtc is not { } mark)
                throw new InvalidOperationException(
                    "--since last was requested but no previous run is recorded for this tenant and query. " +
                    "Seed the first window explicitly, e.g. --since 1d or --since 2026-08-01, " +
                    "then later runs can use --since last.");

            since = mark;
            fromState = true;
            origin = $"resumed from saved mark, {overlap.TotalMinutes:F0}m overlap";
        }
        else if (sinceSpec.Equals("yesterday", StringComparison.OrdinalIgnoreCase))
        {
            var startOfToday = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
            since = startOfToday.AddDays(-1);
            // "yesterday" means the whole UTC day unless an explicit end was given.
            if (untilSpec is null) until = startOfToday;
            origin = "yesterday (UTC day)";
        }
        else if (sinceSpec.Equals("today", StringComparison.OrdinalIgnoreCase))
        {
            since = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
            origin = "today (UTC, so far)";
        }
        else if (TryParseSpan(sinceSpec, out var span))
        {
            since = now - span;
            origin = $"last {sinceSpec}";
        }
        else
        {
            since = ParseInstant(sinceSpec, now, "--since", endOfDay: false);

            // A bare date with no explicit end means that single UTC day.
            if (untilSpec is null && IsBareDate(sinceSpec))
            {
                until = since.AddDays(1);
                origin = $"{sinceSpec} (UTC day)";
            }
        }

        // Overlap re-reads a little of the previous window. Guards against the API
        // stamping ModifiedOn from a clock slightly behind ours, which would otherwise
        // drop records into a window already closed. Costs a few duplicate rows;
        // set Source.Delta.OverlapMinutes to 0 for exact adjacency.
        if (fromState && overlap > TimeSpan.Zero)
            since -= overlap;

        var window = new DeltaWindow(since, until, property, fromState, origin);

        if (window.Duration > TimeSpan.FromDays(config.WarnIfWindowExceedsDays) &&
            config.WarnIfWindowExceedsDays > 0)
            log($"Warning: the delta window spans {window.Duration.TotalDays:F1} days. " +
                "If this is unintended (a long gap since the last run, or a mistyped --since), " +
                "the export may be far larger than a normal daily delta.");

        return window;
    }

    private static bool IsBareDate(string spec) =>
        DateTime.TryParseExact(spec, new[] { "yyyy-MM-dd", "yyyy/MM/dd" },
            CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private static DateTimeOffset ParseInstant(string spec, DateTimeOffset now, string option, bool endOfDay)
    {
        if (spec.Equals("now", StringComparison.OrdinalIgnoreCase)) return now;

        if (IsBareDate(spec))
        {
            var date = DateTime.ParseExact(spec, new[] { "yyyy-MM-dd", "yyyy/MM/dd" },
                CultureInfo.InvariantCulture, DateTimeStyles.None);
            var midnight = new DateTimeOffset(date, TimeSpan.Zero);
            // For --until a bare date means the end of that day, so the day is included.
            return endOfDay ? midnight.AddDays(1) : midnight;
        }

        if (DateTimeOffset.TryParse(spec, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
            return parsed;

        if (TryParseSpan(spec, out var span)) return now - span;

        throw new ArgumentException(
            $"{option}: could not understand '{spec}'. Expected 'last', 'yesterday', 'today', 'now', " +
            "a span like 1d / 36h / 90m, a date like 2026-08-05, or an instant like 2026-08-05T04:00:00Z.");
    }

    private static bool TryParseSpan(string spec, out TimeSpan span)
    {
        span = default;
        if (spec.Length < 2) return false;

        var unit = char.ToLowerInvariant(spec[^1]);
        if (unit is not ('d' or 'h' or 'm')) return false;

        if (!double.TryParse(spec.AsSpan(0, spec.Length - 1), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var amount) || amount <= 0)
            return false;

        span = unit switch
        {
            'd' => TimeSpan.FromDays(amount),
            'h' => TimeSpan.FromHours(amount),
            _ => TimeSpan.FromMinutes(amount)
        };

        return true;
    }
}

/// <summary>Persisted delta position for one tenant + query lineage.</summary>
public sealed class DeltaState
{
    public DateTimeOffset? HighWaterMarkUtc { get; set; }
    public DateTimeOffset? LastRunUtc { get; set; }
    public long LastRunRows { get; set; }
    public long TotalRows { get; set; }
    public int RunCount { get; set; }

    [JsonIgnore]
    public string Key { get; set; } = "";
}

/// <summary>
/// Reads and writes delta state, keyed so that changing the tenant, delta property or
/// base query starts a fresh lineage instead of silently reusing an unrelated mark.
/// </summary>
public static class DeltaStateStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string ResolvePath(ExportConfig config) =>
        Path.IsPathRooted(config.Source.Delta.StateFile)
            ? config.Source.Delta.StateFile
            : Path.Combine(config.Output.Directory, config.Source.Delta.StateFile);

    public static string ComputeKey(ExportConfig config)
    {
        var material = string.Join("", new[]
        {
            config.Aprimo.ResolvedApiBaseUrl,
            config.Source.Mode.ToString(),
            config.Source.Delta.Property,
            config.Source.SearchExpression ?? "",
            config.Source.Filter ?? ""
        });

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..16];
    }

    public static DeltaState Load(ExportConfig config, Action<string> log)
    {
        var key = ComputeKey(config);
        var path = ResolvePath(config);

        if (!File.Exists(path)) return new DeltaState { Key = key };

        try
        {
            var all = JsonSerializer.Deserialize<Dictionary<string, DeltaState>>(
                          File.ReadAllText(path), Json)
                      ?? new Dictionary<string, DeltaState>();

            if (all.TryGetValue(key, out var state))
            {
                state.Key = key;
                return state;
            }

            if (all.Count > 0)
                log($"Delta state file has {all.Count} entr(ies) but none for this tenant and query " +
                    $"(key {key}). Treating this as a first run.");

            return new DeltaState { Key = key };
        }
        catch (Exception ex)
        {
            log($"Delta state at '{path}' could not be read ({ex.Message}); treating this as a first run.");
            return new DeltaState { Key = key };
        }
    }

    public static void Save(ExportConfig config, DeltaWindow window, long rowsExported, Action<string> log)
    {
        var key = ComputeKey(config);
        var path = ResolvePath(config);

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var all = File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, DeltaState>>(File.ReadAllText(path), Json)
                  ?? new Dictionary<string, DeltaState>()
                : new Dictionary<string, DeltaState>();

            all.TryGetValue(key, out var previous);

            all[key] = new DeltaState
            {
                // The next window starts where this one ended, not at max(ModifiedOn).
                HighWaterMarkUtc = window.Until,
                LastRunUtc = DateTimeOffset.UtcNow,
                LastRunRows = rowsExported,
                TotalRows = (previous?.TotalRows ?? 0) + rowsExported,
                RunCount = (previous?.RunCount ?? 0) + 1
            };

            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(all, Json));
            File.Move(temp, path, overwrite: true);

            log($"Delta mark advanced to {SearchExpressions.FormatInstant(window.Until)} " +
                $"({path}). Next run can use --since last.");
        }
        catch (Exception ex)
        {
            log($"Warning: could not save delta state ({ex.Message}). The export succeeded, but the next " +
                "--since last would repeat this window. Record the upper bound manually if that matters.");
        }
    }

    public static bool Reset(ExportConfig config, Action<string> log)
    {
        var path = ResolvePath(config);
        if (!File.Exists(path))
        {
            log($"No delta state to reset at '{path}'.");
            return false;
        }

        var key = ComputeKey(config);

        try
        {
            var all = JsonSerializer.Deserialize<Dictionary<string, DeltaState>>(
                          File.ReadAllText(path), Json)
                      ?? new Dictionary<string, DeltaState>();

            if (!all.Remove(key))
            {
                log($"No delta state for this tenant and query (key {key}); nothing reset.");
                return false;
            }

            if (all.Count == 0)
            {
                File.Delete(path);
                log($"Removed the last delta entry and deleted '{path}'.");
            }
            else
            {
                File.WriteAllText(path, JsonSerializer.Serialize(all, Json));
                log($"Reset delta state for key {key}; {all.Count} other entr(ies) kept.");
            }

            return true;
        }
        catch (Exception ex)
        {
            log($"Could not reset delta state ({ex.Message}).");
            return false;
        }
    }
}
