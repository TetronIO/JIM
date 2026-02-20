using System.Text.Json.Serialization;

namespace JIM.Web.Models.Api;

/// <summary>
/// Represents a single log entry from a JIM service.
/// </summary>
public class LogEntryDto
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

/// <summary>
/// Represents a raw log entry from the Compact JSON format.
/// Used for deserialisation from log files.
/// </summary>
internal class CompactJsonLogEntry
{
    /// <summary>
    /// Timestamp in ISO 8601 format.
    /// </summary>
    [JsonPropertyName("@t")]
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Log level.
    /// </summary>
    [JsonPropertyName("@l")]
    public string? Level { get; set; }

    /// <summary>
    /// Rendered message.
    /// </summary>
    [JsonPropertyName("@m")]
    public string? Message { get; set; }

    /// <summary>
    /// Message template (if different from rendered message).
    /// </summary>
    [JsonPropertyName("@mt")]
    public string? MessageTemplate { get; set; }

    /// <summary>
    /// Exception details.
    /// </summary>
    [JsonPropertyName("@x")]
    public string? Exception { get; set; }

    /// <summary>
    /// Converts to a LogEntryDto.
    /// </summary>
    /// <param name="service">The service name (web, worker, scheduler).</param>
    /// <param name="additionalProperties">Any additional properties from the JSON.</param>
    /// <returns>A LogEntryDto instance.</returns>
    public LogEntryDto ToDto(string service, Dictionary<string, object>? additionalProperties = null)
    {
        var level = Level ?? "Information";
        return new LogEntryDto
        {
            Timestamp = Timestamp,
            Level = level,
            LevelShort = GetLevelShort(level),
            Message = Message ?? MessageTemplate ?? string.Empty,
            Exception = Exception,
            Service = service,
            Properties = additionalProperties
        };
    }

    private static string GetLevelShort(string level)
    {
        return level switch
        {
            "Verbose" => "VRB",
            "Debug" => "DBG",
            "Information" => "INF",
            "Warning" => "WRN",
            "Error" => "ERR",
            "Fatal" => "FTL",
            _ => level.Length >= 3 ? level[..3].ToUpperInvariant() : level.ToUpperInvariant()
        };
    }
}
