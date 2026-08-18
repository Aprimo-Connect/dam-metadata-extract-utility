using System.Globalization;
using System.Text;
using AprimoExport.Configuration;

namespace AprimoExport.Export;

/// <summary>
/// Buffered RFC 4180 CSV writer that rolls to a new file every
/// <c>MaxRecordsPerFile</c> rows. Each file repeats the header row so every part
/// stands alone.
///
/// <para><see cref="WriteRow"/> is synchronous by design: it only appends to an
/// in-memory buffer, and the export is gated by the API rate limiter, not by disk.
/// Call <see cref="FlushAsync"/> at page boundaries to make progress durable.</para>
/// </summary>
public sealed class CsvRollingWriter : IAsyncDisposable
{
    private readonly OutputConfig _config;
    private readonly long _maxRecordsPerFile;
    private readonly string[] _headers;
    private readonly Action<string> _log;
    private readonly string _newLine;
    private readonly Encoding _encoding;
    private readonly StringBuilder _lineBuffer = new(1024);
    private readonly StringBuilder _normalizeBuffer = new(256);
    private readonly List<string> _filePaths = new();
    private readonly HashSet<string> _columnsWithNewlines = new(StringComparer.Ordinal);

    private static readonly char[] NewlineChars = { '\r', '\n' };

    /// <summary>
    /// Columns where a value contained newlines. Under
    /// <see cref="NewlineHandling.Preserve"/> these are the cells that make a record span
    /// several physical lines; otherwise they are the cells that were rewritten.
    /// </summary>
    public IReadOnlyCollection<string> ColumnsWithNewlines => _columnsWithNewlines;

    private StreamWriter? _writer;
    private long _rowsInCurrentFile;
    private int _fileIndex;

    public long RowsWritten { get; private set; }
    public int FilesCreated => _filePaths.Count;
    public IReadOnlyList<string> FilePaths => _filePaths;
    public string? CurrentFilePath { get; private set; }

    /// <summary>Part number of the file currently open. Persisted so a resume continues past it.</summary>
    public int CurrentFileIndex => _fileIndex;

    /// <param name="startingFileIndex">
    /// First file number to use. On resume this continues past existing parts rather
    /// than overwriting them.
    /// </param>
    public CsvRollingWriter(
        OutputConfig config,
        long maxRecordsPerFile,
        string[] headers,
        Action<string> log,
        int startingFileIndex = 0)
    {
        _config = config;
        _maxRecordsPerFile = maxRecordsPerFile;
        _headers = headers;
        _log = log;
        _fileIndex = startingFileIndex;

        _newLine = config.NewLine.Equals("LF", StringComparison.OrdinalIgnoreCase) ? "\n" : "\r\n";
        _encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: config.WriteByteOrderMark);

        Directory.CreateDirectory(config.Directory);
    }

    /// <summary>
    /// Deletes or rejects pre-existing parts for this prefix. Call before writing so a
    /// re-run never silently blends new output with a previous export.
    /// </summary>
    public static void PrepareDirectory(OutputConfig config, Action<string> log)
    {
        Directory.CreateDirectory(config.Directory);

        var existing = Directory.GetFiles(config.Directory, config.FilePrefix + "_*.csv");
        if (existing.Length == 0) return;

        if (!config.Overwrite)
            throw new InvalidOperationException(
                $"{existing.Length} existing export file(s) matching '{config.FilePrefix}_*.csv' found in " +
                $"'{Path.GetFullPath(config.Directory)}'. Pass --overwrite (or set Output.Overwrite) to replace them, " +
                "or choose a different Output.FilePrefix / Output.Directory.");

        foreach (var file in existing) File.Delete(file);
        log($"Overwrite: deleted {existing.Length} existing file(s) matching '{config.FilePrefix}_*.csv'.");
    }

    /// <summary>Highest existing part number for this prefix, so a resume can continue past it.</summary>
    public static int FindHighestFileIndex(OutputConfig config)
    {
        if (!Directory.Exists(config.Directory)) return 0;

        var highest = 0;
        foreach (var file in Directory.GetFiles(config.Directory, config.FilePrefix + "_*.csv"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var underscore = name.LastIndexOf('_');
            if (underscore < 0) continue;
            if (int.TryParse(name.AsSpan(underscore + 1), out var n) && n > highest) highest = n;
        }

        return highest;
    }

    public void WriteRow(string[] values)
    {
        if (values.Length != _headers.Length)
            throw new ArgumentException(
                $"Row has {values.Length} values but there are {_headers.Length} columns.", nameof(values));

        if (_writer is null || (_maxRecordsPerFile > 0 && _rowsInCurrentFile >= _maxRecordsPerFile))
            StartNewFile();

        _lineBuffer.Clear();
        for (var i = 0; i < values.Length; i++)
        {
            if (i > 0) _lineBuffer.Append(_config.Delimiter);
            AppendEscaped(_lineBuffer, NormalizeNewlines(values[i], i));
        }
        _lineBuffer.Append(_newLine);

        _writer!.Write(_lineBuffer);

        _rowsInCurrentFile++;
        RowsWritten++;
    }

    private void StartNewFile()
    {
        CloseCurrentFile();

        _fileIndex++;
        var path = Path.Combine(_config.Directory, $"{_config.FilePrefix}_{_fileIndex:D4}.csv");

        var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            _config.WriteBufferBytes,
            FileOptions.SequentialScan);

        _writer = new StreamWriter(stream, _encoding, _config.WriteBufferBytes) { AutoFlush = false };
        CurrentFilePath = path;
        _filePaths.Add(path);
        _rowsInCurrentFile = 0;

        // Header on every part so each file is independently usable.
        _lineBuffer.Clear();
        for (var i = 0; i < _headers.Length; i++)
        {
            if (i > 0) _lineBuffer.Append(_config.Delimiter);
            AppendEscaped(_lineBuffer, _headers[i]);
        }
        _lineBuffer.Append(_newLine);
        _writer.Write(_lineBuffer);

        _log($"Writing part {_fileIndex}: {Path.GetFileName(path)}");
    }

    private void CloseCurrentFile()
    {
        if (_writer is null) return;

        _writer.Flush();
        _writer.Dispose();
        _writer = null;
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (_writer is not null)
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies <see cref="OutputConfig.NewlineInValue"/>. Aprimo delivers list fields as
    /// one newline-delimited string, so this is what turns nine lines in a cell into
    /// <c>a|b|c</c> on a single row.
    ///
    /// <para>Each part is trimmed because Aprimo's delimiter carries a trailing space
    /// (<c>"Indoor\n Individual"</c>), and empty parts are dropped so a trailing newline
    /// does not produce a dangling separator.</para>
    /// </summary>
    private string? NormalizeNewlines(string? value, int columnIndex)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (value.IndexOf('\n') < 0 && value.IndexOf('\r') < 0) return value;

        // Recorded before the policy check, so Preserve still reports which columns make
        // a record span multiple physical lines.
        if (columnIndex < _headers.Length) _columnsWithNewlines.Add(_headers[columnIndex]);

        if (_config.NewlineInValue == NewlineHandling.Preserve) return value;

        var joiner = _config.NewlineInValue == NewlineHandling.Space
            ? " "
            : _config.MultiValueSeparator;

        var parts = value.Split(NewlineChars, StringSplitOptions.RemoveEmptyEntries);

        _normalizeBuffer.Clear();
        var wrote = false;
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0) continue;
            if (wrote) _normalizeBuffer.Append(joiner);
            _normalizeBuffer.Append(trimmed);
            wrote = true;
        }

        return _normalizeBuffer.ToString();
    }

    private void AppendEscaped(StringBuilder builder, string? value)
    {
        value ??= "";

        if (_config.SanitizeFormulas && NeedsFormulaGuard(value))
            value = "'" + value;

        var mustQuote =
            value.Contains('"') ||
            value.Contains('\r') ||
            value.Contains('\n') ||
            value.Contains(_config.Delimiter, StringComparison.Ordinal) ||
            (value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1])));

        if (!mustQuote)
        {
            builder.Append(value);
            return;
        }

        builder.Append('"');
        foreach (var c in value)
        {
            if (c == '"') builder.Append('"');   // RFC 4180: double the quote
            builder.Append(c);
        }
        builder.Append('"');
    }

    /// <summary>
    /// CSV-injection guard. Numbers are exempt so genuine numeric data such as
    /// <c>-42</c> or <c>+1.5</c> is not corrupted into text.
    /// </summary>
    private static bool NeedsFormulaGuard(string value)
    {
        if (value.Length == 0) return false;

        var first = value[0];
        if (first is not ('=' or '+' or '-' or '@' or '\t' or '\r')) return false;

        return !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    }

    public async ValueTask DisposeAsync()
    {
        if (_writer is not null)
        {
            await _writer.FlushAsync().ConfigureAwait(false);
            await _writer.DisposeAsync().ConfigureAwait(false);
            _writer = null;
        }
    }
}
