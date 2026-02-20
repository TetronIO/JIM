namespace JIM.Models.Exceptions;

/// <summary>
/// Represents an error that occurred while parsing a CSV file.
/// Provides user-friendly context about the error and suggestions for resolution.
/// </summary>
public class CsvParsingException : OperationalException
{
    /// <summary>
    /// The row number where the error occurred (1-based, excluding header).
    /// </summary>
    public int? RowNumber { get; }

    /// <summary>
    /// The raw content of the problematic row, if available.
    /// </summary>
    public string? RawRow { get; }

    /// <summary>
    /// The column name or index that caused the error, if applicable.
    /// </summary>
    public string? ColumnInfo { get; }

    /// <summary>
    /// A user-friendly suggestion for how to fix the issue.
    /// </summary>
    public string? Suggestion { get; }

    public CsvParsingException(string message) : base(message) { }

    public CsvParsingException(string message, Exception innerException) : base(message, innerException) { }

    public CsvParsingException(string message, int? rowNumber, string? rawRow, string? columnInfo, string? suggestion, Exception? innerException = null)
        : base(message, innerException)
    {
        RowNumber = rowNumber;
        RawRow = rawRow;
        ColumnInfo = columnInfo;
        Suggestion = suggestion;
    }

    /// <summary>
    /// Creates a formatted error message including all available context.
    /// </summary>
    public string GetDetailedMessage()
    {
        var parts = new List<string> { Message };

        if (RowNumber.HasValue)
            parts.Add($"Row: {RowNumber}");

        if (!string.IsNullOrEmpty(ColumnInfo))
            parts.Add($"Column: {ColumnInfo}");

        if (!string.IsNullOrEmpty(RawRow))
        {
            // Truncate very long rows
            var displayRow = RawRow.Length > 200 ? RawRow[..200] + "..." : RawRow;
            parts.Add($"Raw data: {displayRow}");
        }

        if (!string.IsNullOrEmpty(Suggestion))
            parts.Add($"Suggestion: {Suggestion}");

        return string.Join(Environment.NewLine, parts);
    }
}
