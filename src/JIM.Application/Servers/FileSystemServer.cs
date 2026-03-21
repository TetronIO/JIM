using JIM.Models.Utility;

namespace JIM.Application.Servers;

/// <summary>
/// Provides file system browsing capabilities for connector configuration.
/// Restricts access to allowed root directories for security.
/// </summary>
public class FileSystemServer
{
    private JimApplication Application { get; }

    /// <summary>
    /// The root directories that users are allowed to browse.
    /// </summary>
    private static readonly string[] AllowedRoots = { "/var/connector-files" };

    internal FileSystemServer(JimApplication application)
    {
        Application = application;
    }

    /// <summary>
    /// Lists the contents of a directory within the allowed root paths.
    /// </summary>
    /// <param name="path">The directory path to list. If null or empty, lists the default root.</param>
    /// <returns>A result containing directories and files, or an error if the path is invalid.</returns>
    public FileSystemListResult ListDirectory(string? path)
    {
        // If no path specified, start at the primary allowed root
        if (string.IsNullOrWhiteSpace(path))
        {
            var defaultRoot = AllowedRoots.FirstOrDefault(Directory.Exists);
            if (defaultRoot == null)
            {
                return new FileSystemListResult
                {
                    Path = "/var/connector-files",
                    Error = "No accessible directories found. Ensure /var/connector-files is mounted.",
                    Entries = new List<FileSystemEntry>()
                };
            }
            path = defaultRoot;
        }

        // Validate the path is within allowed roots
        var normalisedPath = Path.GetFullPath(path);
        if (!IsPathAllowed(normalisedPath))
        {
            return new FileSystemListResult
            {
                Path = path,
                Error = "Access denied: Path is outside allowed directories.",
                IsAccessDenied = true
            };
        }

        // Check if directory exists
        if (!Directory.Exists(normalisedPath))
        {
            return new FileSystemListResult
            {
                Path = path,
                Error = $"Directory not found: {path}"
            };
        }

        try
        {
            var entries = new List<FileSystemEntry>();

            // Add directories
            foreach (var dir in Directory.GetDirectories(normalisedPath).OrderBy(d => d))
            {
                entries.Add(new FileSystemEntry(Path.GetFileName(dir), dir, true));
            }

            // Add files
            foreach (var file in Directory.GetFiles(normalisedPath).OrderBy(f => f))
            {
                entries.Add(new FileSystemEntry(Path.GetFileName(file), file, false));
            }

            // Determine parent path (if not at root)
            string? parentPath = null;
            var parentDir = Directory.GetParent(normalisedPath);
            if (parentDir != null && IsPathAllowed(parentDir.FullName))
            {
                parentPath = parentDir.FullName;
            }

            return new FileSystemListResult
            {
                Path = normalisedPath,
                Entries = entries,
                ParentPath = parentPath,
                IsRoot = AllowedRoots.Any(r => Path.GetFullPath(r).Equals(normalisedPath, StringComparison.OrdinalIgnoreCase))
            };
        }
        catch (UnauthorizedAccessException)
        {
            return new FileSystemListResult
            {
                Path = path,
                Error = "Access denied: Insufficient permissions to read directory.",
                IsAccessDenied = true
            };
        }
        catch (Exception ex)
        {
            return new FileSystemListResult
            {
                Path = path,
                Error = $"Error reading directory: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Gets the allowed root directories that exist on the file system.
    /// </summary>
    public IReadOnlyList<string> GetAllowedRoots()
    {
        return AllowedRoots.Where(Directory.Exists).ToList();
    }

    /// <summary>
    /// Validates that a path is within the allowed root directories.
    /// </summary>
    /// <param name="path">The path to validate.</param>
    /// <returns>True if the path is allowed, false otherwise.</returns>
    public bool IsPathAllowed(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var normalisedPath = Path.GetFullPath(path);
        return AllowedRoots.Any(root =>
            Directory.Exists(root) &&
            normalisedPath.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase));
    }
}

