using System.Globalization;

namespace AprimoExport.Export;

/// <summary>
/// Helpers for building Aprimo search expressions. Centralised so the delta window and
/// keyset paging cannot drift apart on how they format a value — both append a
/// timestamp comparison to the same expression.
/// </summary>
public static class SearchExpressions
{
    /// <summary>
    /// Format used for date-time comparisons. The spec's own example shows dates
    /// unquoted (<c>CreatedOn &gt;= 2025-01-01</c>), so instants are emitted bare in
    /// round-trip UTC form rather than quoted.
    /// </summary>
    public const string InstantFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";

    public static string FormatInstant(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString(InstantFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// Formats a literal for an expression: date-like values bare, everything else
    /// single-quoted with embedded quotes doubled.
    /// </summary>
    public static string FormatValue(string value)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var parsed))
            return FormatInstant(parsed);

        return "'" + value.Replace("'", "''") + "'";
    }

    /// <summary>
    /// ANDs a predicate onto an existing expression, parenthesising the left side so its
    /// own OR/NOT operators keep their meaning. An empty left side yields the predicate
    /// alone, which matters now that the default query is "all content types".
    /// </summary>
    public static string And(string? left, string predicate)
    {
        if (string.IsNullOrWhiteSpace(predicate)) return left ?? "";
        if (string.IsNullOrWhiteSpace(left)) return predicate;
        return $"({left}) AND {predicate}";
    }
}
