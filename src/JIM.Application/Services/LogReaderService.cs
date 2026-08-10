// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using JIM.Models.Core;
using JIM.Models.Utility;
using Serilog;

namespace JIM.Application.Services;

/// <summary>
/// Service for reading and parsing log files from JIM services.
/// </summary>
public class LogReaderService
{
    private static readonly string[] ServicePrefixes = ["jim.web", "jim.worker", "jim.scheduler", "jim.database"];
    private static readonly Dictionary<string, int> LogLevelPriority = new()
    {
        ["Verbose"] = 0,
        ["Debug"] = 1,
        ["Information"] = 2,
        ["Warning"] = 3,
        ["Error"] = 4,
        ["Fatal"] = 5
    };

    /// <summary>
    /// Maps PostgreSQL error_severity values to Serilog log level names.
    /// </summary>
    private static readonly Dictionary<string, string> PostgresSeverityMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DEBUG5"] = "Verbose",
        ["DEBUG4"] = "Verbose",
        ["DEBUG3"] = "Verbose",
        ["DEBUG2"] = "Verbose",
        ["DEBUG1"] = "Verbose",
        ["INFO"] = "Debug",
        ["NOTICE"] = "Information",
        ["LOG"] = "Information",
        ["WARNING"] = "Warning",
        ["ERROR"] = "Error",
        ["FATAL"] = "Fatal",
        ["PANIC"] = "Fatal"
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

        // Scan for both .log (Serilog) and .json (PostgreSQL jsonlog) files.
        // Also scan subdirectories (e.g. /var/log/jim/database/) for PostgreSQL logs
        // which are written to a separate subdirectory for filesystem permission isolation.
        var logFiles = Directory.GetFiles(_logPath, "*.log")
            .Concat(Directory.GetFiles(_logPath, "*.json"))
            .Concat(Directory.GetDirectories(_logPath)
                .SelectMany(dir => Directory.GetFiles(dir, "*.log")
                    .Concat(Directory.GetFiles(dir, "*.json"))));
        foreach (var filePath in logFiles)
        {
            var fileName = Path.GetFileName(filePath);
            var (service, date) = ParseLogFileName(fileName);

            if (service == null || date == null)
                continue;

            // PostgreSQL jsonlog writes structured content to .json files. The .log file
            // in the same directory only contains a brief stderr redirect notice and should
            // be skipped to avoid parsing warnings.
            if (service == "database" && filePath.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
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
        var filtered = await GetFilteredLogEntriesAsync(service, date, levels, search);
        return filtered
            .Skip(offset)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// The largest window <see cref="GetLogEntriesRangeAsync"/> will return, bounding the cost of a single read.
    /// It matches the Metaverse Object header range read's cap and the same derivation: the virtualised Logs grid
    /// asks for however many rows its viewport needs plus overscan, and a cap it can actually reach truncates the
    /// window silently, rendering the shortfall as blank rows. 500 puts it out of reach of any real viewport (about
    /// 474 rows at Chrome's minimum 25% zoom on a 4320px-tall display at the grid's 36px dense row height); see
    /// MetaverseRepository.MaxHeaderWindowSize for the full arithmetic.
    /// </summary>
    public const int MaximumLogEntryWindowSize = 500;

    /// <summary>
    /// Gets one window of log entries matching the specified criteria, addressed by absolute offset and count, for
    /// the virtualised (infinite-scroll) Logs list. Entries are ordered newest first, matching
    /// <see cref="GetLogEntriesAsync"/>.
    /// </summary>
    /// <param name="service">Filter by service name (web, worker, scheduler, database). Null for all.</param>
    /// <param name="date">The date to retrieve logs for. Null for today.</param>
    /// <param name="levels">Specific log levels to include. Null or empty for all.</param>
    /// <param name="search">Text to search for in messages and exceptions. Null for no filter.</param>
    /// <param name="startIndex">The zero-based index of the first entry wanted; negative values are read as zero.</param>
    /// <param name="count">How many entries are wanted; clamped to <see cref="MaximumLogEntryWindowSize"/>.</param>
    /// <param name="includeTotalCount">Whether to report the total match count alongside the window. Unlike a
    /// database-backed range read the count costs nothing extra here (the match set is already in memory), but the
    /// null contract is honoured so the caller's null-versus-zero semantics hold: null means "not counted", never
    /// "no matches".</param>
    /// <returns>The requested window and, when asked for, the total match count.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="count"/> is less than one.</exception>
    public async Task<RangeResultSet<LogEntry>> GetLogEntriesRangeAsync(
        string? service = null,
        DateTime? date = null,
        IEnumerable<string>? levels = null,
        string? search = null,
        int startIndex = 0,
        int count = MaximumLogEntryWindowSize,
        bool includeTotalCount = true)
    {
        if (count < 1)
            throw new ArgumentOutOfRangeException(nameof(count), "count must be a positive number");

        if (startIndex < 0)
            startIndex = 0;

        if (count > MaximumLogEntryWindowSize)
            count = MaximumLogEntryWindowSize;

        var filtered = await GetFilteredLogEntriesAsync(service, date, levels, search);

        return new RangeResultSet<LogEntry>
        {
            Results = filtered.Skip(startIndex).Take(count).ToList(),
            TotalResults = includeTotalCount ? filtered.Count : null
        };
    }

    /// <summary>
    /// Shared core for the limit/offset and range reads: parses the target date's log files (reusing prior parses
    /// where a file has not changed), applies the service, level and search filters, and returns the match set
    /// sorted newest first. Callers own input validation and windowing; this method assumes sane values.
    /// </summary>
    private async Task<List<LogEntry>> GetFilteredLogEntriesAsync(
        string? service,
        DateTime? date,
        IEnumerable<string>? levels,
        string? search)
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
            var fileEntries = await ReadLogFileCachedAsync(file, targetDate);
            entries.AddRange(fileEntries);
        }

        // A virtualised scroll issues one read per window, so parses for other dates must not accumulate in this
        // singleton for the 31-day retention span; keep only the date being read.
        EvictCachedParsesForOtherDates(targetDate);

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

        // Sort by timestamp descending (newest first)
        return filtered
            .OrderByDescending(e => e.Timestamp)
            .ToList();
    }

    /// <summary>
    /// A log file's parsed entries alongside the size and write time they were parsed at, so an unchanged file's
    /// parse can be reused across the many window reads a virtualised scroll issues. Log files are append-only, so
    /// a changed length or write time means new content and forces a fresh parse; the entry list is never mutated
    /// after creation, making the cached instance safe to share across concurrent readers.
    /// </summary>
    private sealed record CachedLogFileParse(long Length, DateTime LastWriteTimeUtc, DateTime FileDate, List<LogEntry> Entries);

    private readonly ConcurrentDictionary<string, CachedLogFileParse> _parsedFileCache = new();

    /// <summary>
    /// Reads a log file's entries, reusing the previous parse when the file has not changed since. Without this,
    /// every scroll window of the virtualised Logs list would re-parse the whole day's files; with it, only the
    /// file actively being written to (whose size changes) is ever re-parsed.
    /// </summary>
    private async Task<List<LogEntry>> ReadLogFileCachedAsync(LogFileInfo file, DateTime fileDate)
    {
        long length;
        DateTime lastWriteTimeUtc;
        try
        {
            var fileInfo = new FileInfo(file.FilePath);
            length = fileInfo.Length;
            lastWriteTimeUtc = fileInfo.LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file vanished or became unreadable between listing and reading (log rotation, permissions);
            // fall through to the uncached read, which handles and logs its own failures.
            return await ReadLogFileAsync(file.FilePath, file.Service);
        }

        if (_parsedFileCache.TryGetValue(file.FilePath, out var cached) &&
            cached.Length == length &&
            cached.LastWriteTimeUtc == lastWriteTimeUtc)
        {
            return cached.Entries;
        }

        var parsed = await ReadLogFileAsync(file.FilePath, file.Service);
        _parsedFileCache[file.FilePath] = new CachedLogFileParse(length, lastWriteTimeUtc, fileDate, parsed);
        return parsed;
    }

    /// <summary>
    /// Drops cached parses for every date other than the one just read, bounding the cache to roughly one day's
    /// entries. Readers alternating between dates fall back to re-parsing, which is the pre-cache behaviour.
    /// </summary>
    private void EvictCachedParsesForOtherDates(DateTime targetDate)
    {
        foreach (var stale in _parsedFileCache.Where(kvp => kvp.Value.FileDate != targetDate).Select(kvp => kvp.Key).ToList())
            _parsedFileCache.TryRemove(stale, out _);
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
        return ["web", "worker", "scheduler", "database"];
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
        if (service == "database")
            return ParsePostgresJsonLogLine(line, service);

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

    /// <summary>
    /// Parses a PostgreSQL jsonlog format line into a LogEntry.
    /// PostgreSQL jsonlog uses different field names from Serilog Compact JSON:
    /// "timestamp", "error_severity", "message" instead of "@t", "@l", "@m".
    /// </summary>
    private static LogEntry? ParsePostgresJsonLogLine(string line, string service)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            // Parse PostgreSQL timestamp (format: "2026-02-21 14:30:00.123 UTC")
            var timestamp = DateTime.UtcNow;
            if (root.TryGetProperty("timestamp", out var t))
            {
                var timestampStr = t.GetString();
                if (timestampStr != null)
                {
                    // PostgreSQL appends " UTC" which DateTime.TryParse may not handle correctly.
                    // Strip the timezone suffix and parse as UTC.
                    var normalised = timestampStr.TrimEnd();
                    if (normalised.EndsWith(" UTC", StringComparison.OrdinalIgnoreCase))
                        normalised = normalised[..^4];

                    if (DateTime.TryParse(normalised,
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out var parsed))
                    {
                        timestamp = parsed;
                    }
                }
            }

            // Map PostgreSQL error_severity to Serilog level
            var severityStr = root.TryGetProperty("error_severity", out var s)
                ? s.GetString()
                : null;
            var level = MapPostgresSeverity(severityStr);

            // Extract the message
            var message = root.TryGetProperty("message", out var m)
                ? m.GetString() ?? string.Empty
                : string.Empty;

            // Extract useful PostgreSQL metadata as properties
            var properties = new Dictionary<string, object>();
            var pgPropertyNames = new[] { "user", "dbname", "pid", "remote_host", "application_name", "statement", "backend_type", "query_id", "detail", "hint", "context", "state_code" };

            foreach (var propName in pgPropertyNames)
            {
                if (!root.TryGetProperty(propName, out var prop))
                    continue;

                switch (prop.ValueKind)
                {
                    case JsonValueKind.String:
                        var strVal = prop.GetString();
                        if (!string.IsNullOrEmpty(strVal))
                            properties[propName] = strVal;
                        break;
                    case JsonValueKind.Number:
                        if (prop.TryGetInt64(out var longVal) && longVal != 0)
                            properties[propName] = longVal;
                        break;
                }
            }

            return new LogEntry
            {
                Timestamp = timestamp,
                Level = level,
                LevelShort = GetLevelShort(level),
                Message = message,
                Exception = null,
                Service = service,
                Properties = properties.Count > 0 ? properties : null
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            Log.Warning("Failed to parse PostgreSQL jsonlog line: {Error}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Maps a PostgreSQL error_severity value to a Serilog log level name.
    /// </summary>
    private static string MapPostgresSeverity(string? severity)
    {
        if (severity != null && PostgresSeverityMap.TryGetValue(severity, out var level))
            return level;
        return "Information";
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
        // Also: jim.database.20260103.json (PostgreSQL jsonlog replaces .log with .json)
        foreach (var prefix in ServicePrefixes)
        {
            if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var match = Regex.Match(fileName, $@"^{Regex.Escape(prefix)}\.(\d{{8}})(?:_\d+)?\.(?:log|json)$", RegexOptions.IgnoreCase);
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

