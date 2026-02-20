namespace JIM.Web.Models.Api;

/// <summary>
/// Represents a log file available for viewing.
/// </summary>
public class LogFileDto
{
    /// <summary>
    /// The file name without path.
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// The service that generated the log file (web, worker, scheduler).
    /// </summary>
    public string Service { get; set; } = string.Empty;

    /// <summary>
    /// The date of the log file.
    /// </summary>
    public DateTime Date { get; set; }

    /// <summary>
    /// The file size in bytes.
    /// </summary>
    public long SizeBytes { get; set; }

    /// <summary>
    /// Human-readable file size.
    /// </summary>
    public string SizeFormatted { get; set; } = string.Empty;
}
