namespace JIM.Web.Models.Api;

/// <summary>
/// Request parameters for querying logs.
/// </summary>
public class LogQueryRequest
{
    /// <summary>
    /// Filter by service name (web, worker, scheduler). Null for all services.
    /// </summary>
    public string? Service { get; set; }

    /// <summary>
    /// The date to retrieve logs for. Null defaults to today (UTC).
    /// </summary>
    public DateTime? Date { get; set; }

    /// <summary>
    /// Specific log levels to include (Verbose, Debug, Information, Warning, Error, Fatal).
    /// Null or empty returns all levels.
    /// </summary>
    public List<string>? Levels { get; set; }

    /// <summary>
    /// Text to search for in the log message (case-insensitive).
    /// </summary>
    public string? Search { get; set; }

    /// <summary>
    /// Maximum number of entries to return. Default is 500.
    /// </summary>
    public int Limit { get; set; } = 500;

    /// <summary>
    /// Number of entries to skip for pagination. Default is 0.
    /// </summary>
    public int Offset { get; set; } = 0;
}
