using System.Text.Json;
using System.Text.RegularExpressions;
using JIM.Models.Core;
using Serilog;

namespace JIM.Application.Services;

/// <summary>
/// Service for reading and parsing log files from JIM services.
/// </summary>
public class LogReaderService
{
    private static readonly string[] ServicePrefixes = ["jim.web", "jim.worker", "jim.scheduler"];
    private static readonly Dictionary<string, int> LogLevelPriority = new()
    {
        ["Verbose"] = 0,
        ["Debug"] = 1,
        ["Information"] = 2,
        ["Warning"] = 3,
        ["Error"] = 4,
        ["Fatal"] = 5
    };

    private readonly string _logPath;

    /// <summary>
    /// Initialises a new instance of LogReaderService using the configured log path.
    /// </summary>
    public LogReaderService()
    {
        _logPath = Environment.GetEnvironmentVariable(Constants.Config.LogPath)
            ?? throw new InvalidOperationException($"{Constants.Config.LogPath} environment variable not set");
    }

    /// <summary>
    /// Initialises a new instance of LogReaderService with a specific log path.
    /// </summary>
    /// <param name="logPath">The path to the log directory.</param>
    public LogReaderService(string logPath)
    {
        _logPath = logPath ?? throw new ArgumentNullException(nameof(logPath));
    }

    /// <summary>
    /// Gets the configured log path.
    /// </summary>
    public string LogPath => _logPath;

    /// <summary>
    /// Gets all available log files.
    /// </summary>
    /// <returns>A list of log file information.</returns>
    public Task<List<LogFileInfo>> GetLogFilesAsync()
    {
        var files = new List<LogFileInfo>();

        if (!Directory.Exists(_logPath))
        {
            Log.Warning("Log directory does not exist: {LogPath}", _logPath);
            return Task.FromResult(files);
        }

        foreach (var filePath in Directory.GetFiles(_logPath, "*.log"))
        {
            var fileName = Path.GetFileName(filePath);
            var (service, date) = ParseLogFileName(fileName);

            if (service == null || date == null)
                continue;

            var fileInfo = new FileInfo(filePath);
            files.Add(new LogFileInfo
            {
                FileName = fileName,
                FilePath = filePath,
                Service = service,
                Date = date.Value,
                SizeBytes = fileInfo.Length
            });
        }

        return Task.FromResult(files.OrderByDescending(f => f.Date).ThenBy(f => f.Service).ToList());
    }

    /// <summary>
    /// Gets log entries matching the specified criteria.
    /// </summary>
    /// <param name="service">Filter by service name (web, worker, scheduler). Null for all.</param>
    /// <param name="date">The date to retrieve logs for. Null for today.</param>
    /// <param name="levels">Specific log levels to include. Null or empty for all.</param>
    /// <param name="search">Text to search for in messages. Null for no filter.</param>
    /// <param name="limit">Maximum entries to return.</param>
    /// <param name="offset">Number of entries to skip.</param>
    /// <returns>A list of log entries.</returns>
    public async Task<List<LogEntry>> GetLogEntriesAsync(
        string? service = null,
        DateTime? date = null,
        IEnumerable<string>? levels = null,
        string? search = null,
        int limit = 500,
        int offset = 0)
    {
        var targetDate = date?.Date ?? DateTime.UtcNow.Date;
        var entries = new List<LogEntry>();

        // Get all log files for the target date
        var logFiles = await GetLogFilesAsync();
        var relevantFiles = logFiles
            .Where(f => f.Date.Date == targetDate)
            .Where(f => service == null || f.Service.Equals(service, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var file in relevantFiles)
        {
            var fileEntries = await ReadLogFileAsync(file.FilePath, file.Service);
            entries.AddRange(fileEntries);
        }

        // Apply filters
        var filtered = entries.AsEnumerable();

        var levelList = levels?.ToList();
        if (levelList != null && levelList.Count > 0)
        {
            var levelSet = new HashSet<string>(levelList, StringComparer.OrdinalIgnoreCase);
            filtered = filtered.Where(e => levelSet.Contains(e.Level));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            filtered = filtered.Where(e =>
                e.Message.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (e.Exception?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        // Sort by timestamp descending (newest first) and apply pagination
        return filtered
            .OrderByDescending(e => e.Timestamp)
            .Skip(offset)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// Gets the available log levels in order of severity.
    /// </summary>
    /// <returns>A list of log level names.</returns>
    public static List<string> GetLogLevels()
    {
        return ["Verbose", "Debug", "Information", "Warning", "Error", "Fatal"];
    }

    /// <summary>
    /// Gets the available service names.
    /// </summary>
    /// <returns>A list of service names.</returns>
    public static List<string> GetServices()
    {
        return ["web", "worker", "scheduler"];
    }

    private async Task<List<LogEntry>> ReadLogFileAsync(string filePath, string service)
    {
        var entries = new List<LogEntry>();

        try
        {
            // Use FileShare.ReadWrite to allow reading while the file is being written to
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var entry = ParseLogLine(line, service);
                if (entry != null)
                {
                    entries.Add(entry);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to read log file: {FilePath}", filePath);
        }

        return entries;
    }

    private static LogEntry? ParseLogLine(string line, string service)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            var timestamp = root.TryGetProperty("@t", out var t) && t.TryGetDateTime(out var dt)
                ? dt
                : DateTime.UtcNow;

            var level = root.TryGetProperty("@l", out var l)
                ? l.GetString() ?? "Information"
                : "Information";

            var message = root.TryGetProperty("@m", out var m)
                ? m.GetString() ?? string.Empty
                : root.TryGetProperty("@mt", out var mt)
                    ? mt.GetString() ?? string.Empty
                    : string.Empty;

            var exception = root.TryGetProperty("@x", out var x)
                ? x.GetString()
                : null;

            // Extract additional properties (excluding standard Serilog properties)
            var properties = new Dictionary<string, object>();
            foreach (var prop in root.EnumerateObject())
            {
                if (!prop.Name.StartsWith('@'))
                {
                    properties[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
                        JsonValueKind.Number => prop.Value.TryGetInt64(out var i) ? i : prop.Value.GetDouble(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => prop.Value.ToString()
                    };
                }
            }

            return new LogEntry
            {
                Timestamp = timestamp,
                Level = level,
                LevelShort = GetLevelShort(level),
                Message = message,
                Exception = exception,
                Service = service,
                Properties = properties.Count > 0 ? properties : null
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Not valid JSON or unexpected JSON structure - might be a legacy plain text log line
            return ParsePlainTextLogLine(line, service);
        }
    }

    private static LogEntry? ParsePlainTextLogLine(string line, string service)
    {
        // Pattern: 2026-01-03 11:36:55.735 +00:00 [INF] Message
        var match = Regex.Match(line, @"^(\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}\.\d{3})\s+[+-]\d{2}:\d{2}\s+\[(\w{3})\]\s+(.*)$");
        if (!match.Success)
            return null;

        if (!DateTime.TryParse(match.Groups[1].Value, out var timestamp))
            return null;

        var levelShort = match.Groups[2].Value;
        var level = GetLevelFromShort(levelShort);
        var message = match.Groups[3].Value;

        return new LogEntry
        {
            Timestamp = DateTime.SpecifyKind(timestamp, DateTimeKind.Utc),
            Level = level,
            LevelShort = levelShort,
            Message = message,
            Exception = null,
            Service = service,
            Properties = null
        };
    }

    private static (string? Service, DateTime? Date) ParseLogFileName(string fileName)
    {
        // Pattern: jim.web.20260103.log or jim.web.20260103_001.log (rolled)
        foreach (var prefix in ServicePrefixes)
        {
            if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var match = Regex.Match(fileName, $@"^{Regex.Escape(prefix)}\.(\d{{8}})(?:_\d+)?\.log$", RegexOptions.IgnoreCase);
            if (!match.Success)
                continue;

            var dateStr = match.Groups[1].Value;
            if (DateTime.TryParseExact(dateStr, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var date))
            {
                var service = prefix.Replace("jim.", string.Empty);
                return (service, date);
            }
        }

        return (null, null);
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

    private static string GetLevelFromShort(string levelShort)
    {
        return levelShort.ToUpperInvariant() switch
        {
            "VRB" => "Verbose",
            "DBG" => "Debug",
            "INF" => "Information",
            "WRN" => "Warning",
            "ERR" => "Error",
            "FTL" => "Fatal",
            _ => "Information"
        };
    }
}

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
