using Asp.Versioning;
using JIM.Application;
using JIM.Application.Servers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Web.Controllers.Api;

/// <summary>
/// API controller for browsing the container file system.
/// Provides endpoints for listing directories and files within allowed paths.
/// </summary>
/// <remarks>
/// This controller enables administrators to browse files available for connector configuration,
/// such as CSV import/export files. Access is restricted to specific root directories for security.
/// </remarks>
[Route("api/v{version:apiVersion}/[controller]")]
[ApiController]
[ApiVersion("1.0")]
[Authorize(Roles = "Administrators")]
[Produces("application/json")]
public class FileSystemController(ILogger<FileSystemController> logger, JimApplication application) : ControllerBase
{
    private readonly ILogger<FileSystemController> _logger = logger;
    private readonly JimApplication _application = application;

    /// <summary>
    /// Lists the contents of a directory within allowed paths.
    /// </summary>
    /// <param name="path">The directory path to list. If not specified, returns the allowed root directories.</param>
    /// <returns>A list of files and directories in the specified path.</returns>
    /// <response code="200">Returns the directory listing.</response>
    /// <response code="403">The requested path is outside allowed directories.</response>
    /// <response code="404">The specified directory does not exist.</response>
    [HttpGet("list")]
    [ProducesResponseType(typeof(FileSystemListResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(FileSystemListResult), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(FileSystemListResult), StatusCodes.Status404NotFound)]
    public IActionResult ListDirectory([FromQuery] string? path = null)
    {
        _logger.LogDebug("FileSystem ListDirectory requested for path: {Path}", path ?? "(root)");

        var result = _application.FileSystem.ListDirectory(path);

        if (result.IsAccessDenied)
        {
            _logger.LogWarning("Access denied for path: {Path}", path);
            return StatusCode(StatusCodes.Status403Forbidden, result);
        }

        if (!result.Success)
        {
            _logger.LogWarning("Directory not found or error: {Path} - {Error}", path, result.Error);
            return NotFound(result);
        }

        return Ok(result);
    }

    /// <summary>
    /// Gets the list of allowed root directories that can be browsed.
    /// </summary>
    /// <returns>A list of allowed root directory paths.</returns>
    [HttpGet("roots")]
    [ProducesResponseType(typeof(IReadOnlyList<string>), StatusCodes.Status200OK)]
    public IActionResult GetAllowedRoots()
    {
        _logger.LogDebug("FileSystem GetAllowedRoots requested");
        var roots = _application.FileSystem.GetAllowedRoots();
        return Ok(roots);
    }

    /// <summary>
    /// Validates whether a given path is within the allowed directories.
    /// </summary>
    /// <param name="path">The path to validate.</param>
    /// <returns>True if the path is allowed, false otherwise.</returns>
    [HttpGet("validate")]
    [ProducesResponseType(typeof(PathValidationResult), StatusCodes.Status200OK)]
    public IActionResult ValidatePath([FromQuery] string path)
    {
        _logger.LogDebug("FileSystem ValidatePath requested for: {Path}", path);
        var isAllowed = _application.FileSystem.IsPathAllowed(path);
        return Ok(new PathValidationResult { Path = path, IsAllowed = isAllowed });
    }
}

/// <summary>
/// Result of a path validation request.
/// </summary>
public class PathValidationResult
{
    /// <summary>
    /// The path that was validated.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Whether the path is within allowed directories.
    /// </summary>
    public bool IsAllowed { get; set; }
}
