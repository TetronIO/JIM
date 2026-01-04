using Asp.Versioning;
using JIM.Application.Services;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Web.Controllers.Api;

/// <summary>
/// API controller for viewing JIM service logs.
/// </summary>
/// <remarks>
/// Provides read-only access to log files from all JIM services (Web, Worker, Scheduler).
/// Logs are read from the configured log directory and can be filtered by service,
/// date, log level, and search text.
/// </remarks>
[Route("api/v{version:apiVersion}/[controller]")]
[ApiController]
[ApiVersion("1.0")]
[Authorize(Roles = "Administrator")]
[Produces("application/json")]
public class LogsController(ILogger<LogsController> logger, LogReaderService logReaderService) : ControllerBase
{
    private readonly ILogger<LogsController> _logger = logger;
    private readonly LogReaderService _logReaderService = logReaderService;

    /// <summary>
    /// Gets all available log files.
    /// </summary>
    /// <remarks>
    /// Returns information about all log files in the log directory, including
    /// file name, service, date, and size. Files are sorted by date descending.
    /// </remarks>
    /// <returns>A list of available log files.</returns>
    [HttpGet("files", Name = "GetLogFiles")]
    [ProducesResponseType(typeof(IEnumerable<LogFileDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetLogFilesAsync()
    {
        _logger.LogTrace("Requested log files list");

        var files = await _logReaderService.GetLogFilesAsync();
        var dtos = files.Select(f => new LogFileDto
        {
            FileName = f.FileName,
            Service = f.Service,
            Date = f.Date,
            SizeBytes = f.SizeBytes,
            SizeFormatted = FormatFileSize(f.SizeBytes)
        });

        return Ok(dtos);
    }

    /// <summary>
    /// Gets log entries with optional filtering.
    /// </summary>
    /// <remarks>
    /// Returns log entries matching the specified criteria. Results are sorted by
    /// timestamp descending (newest first) and limited to prevent excessive data transfer.
    /// </remarks>
    /// <param name="request">The query parameters for filtering logs.</param>
    /// <returns>A list of log entries matching the criteria.</returns>
    [HttpGet(Name = "GetLogEntries")]
    [ProducesResponseType(typeof(IEnumerable<LogEntryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetLogEntriesAsync([FromQuery] LogQueryRequest request)
    {
        _logger.LogTrace("Requested log entries (Service: {Service}, Date: {Date}, MinLevel: {MinLevel}, Search: {Search})",
            request.Service, request.Date, request.MinLevel, request.Search);

        // Clamp limit to reasonable bounds
        var limit = Math.Clamp(request.Limit, 1, 5000);
        var offset = Math.Max(0, request.Offset);

        var entries = await _logReaderService.GetLogEntriesAsync(
            service: request.Service,
            date: request.Date,
            minLevel: request.MinLevel,
            search: request.Search,
            limit: limit,
            offset: offset);

        var dtos = entries.Select(e => new LogEntryDto
        {
            Timestamp = e.Timestamp,
            Level = e.Level,
            LevelShort = e.LevelShort,
            Message = e.Message,
            Exception = e.Exception,
            Service = e.Service,
            Properties = e.Properties
        });

        return Ok(dtos);
    }

    /// <summary>
    /// Gets the available log levels.
    /// </summary>
    /// <remarks>
    /// Returns the list of log levels in order of severity, from Verbose to Fatal.
    /// </remarks>
    /// <returns>A list of log level names.</returns>
    [HttpGet("levels", Name = "GetLogLevels")]
    [ProducesResponseType(typeof(IEnumerable<string>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public IActionResult GetLogLevels()
    {
        _logger.LogTrace("Requested log levels");
        return Ok(LogReaderService.GetLogLevels());
    }

    /// <summary>
    /// Gets the available service names.
    /// </summary>
    /// <remarks>
    /// Returns the list of JIM services that generate logs.
    /// </remarks>
    /// <returns>A list of service names.</returns>
    [HttpGet("services", Name = "GetLogServices")]
    [ProducesResponseType(typeof(IEnumerable<string>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public IActionResult GetLogServices()
    {
        _logger.LogTrace("Requested log services");
        return Ok(LogReaderService.GetServices());
    }

    private static string FormatFileSize(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB"];
        var order = 0;
        var size = (double)bytes;

        while (size >= 1024 && order < suffixes.Length - 1)
        {
            order++;
            size /= 1024;
        }

        return $"{size:0.##} {suffixes[order]}";
    }
}
