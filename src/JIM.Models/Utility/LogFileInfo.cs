namespace JIM.Models.Utility;

/// <summary>
/// Represents information about a log file.
/// </summary>
public class LogFileInfo
{
    /// <summary>
    /// The file name without path.
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// The full file path.
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

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
}
