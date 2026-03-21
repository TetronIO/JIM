namespace JIM.Models.Utility;

/// <summary>
/// Represents a parsed log entry.
/// </summary>
public class LogEntry
{
    /// <summary>
    /// The timestamp when the log entry was created (UTC).
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// The log level (Verbose, Debug, Information, Warning, Error, Fatal).
    /// </summary>
    public string Level { get; set; } = string.Empty;

    /// <summary>
    /// The short log level abbreviation (VRB, DBG, INF, WRN, ERR, FTL).
    /// </summary>
    public string LevelShort { get; set; } = string.Empty;

    /// <summary>
    /// The rendered log message.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// The exception details, if any.
    /// </summary>
    public string? Exception { get; set; }

    /// <summary>
    /// The service that generated the log entry (web, worker, scheduler).
    /// </summary>
    public string Service { get; set; } = string.Empty;

    /// <summary>
    /// Additional structured properties from the log entry.
    /// </summary>
    public Dictionary<string, object>? Properties { get; set; }
}
