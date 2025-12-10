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
    /// <param name="path">The directory path to list. If null or empty, lists the allowed roots.</param>
    /// <returns>A result containing directories and files, or an error if the path is invalid.</returns>
    public FileSystemListResult ListDirectory(string? path)
    {
        // If no path specified, return the allowed roots
        if (string.IsNullOrWhiteSpace(path))
        {
            var rootDirs = AllowedRoots
                .Where(Directory.Exists)
                .Select(r => new FileSystemEntry(Path.GetFileName(r), r, true))
                .ToList();

            return new FileSystemListResult
            {
                Path = "/",
                Entries = rootDirs,
                IsRoot = true
            };
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

/// <summary>
/// Represents the result of a directory listing operation.
/// </summary>
public class FileSystemListResult
{
    /// <summary>
    /// The path that was listed.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// The entries (files and directories) in the directory.
    /// </summary>
    public List<FileSystemEntry> Entries { get; set; } = new();

    /// <summary>
    /// The parent directory path, if navigating up is allowed.
    /// </summary>
    public string? ParentPath { get; set; }

    /// <summary>
    /// Whether this is a root directory (cannot navigate up).
    /// </summary>
    public bool IsRoot { get; set; }

    /// <summary>
    /// Error message if the operation failed.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Whether access was denied due to security restrictions.
    /// </summary>
    public bool IsAccessDenied { get; set; }

    /// <summary>
    /// Whether the operation was successful.
    /// </summary>
    public bool Success => string.IsNullOrEmpty(Error);
}

/// <summary>
/// Represents a file or directory entry.
/// </summary>
public class FileSystemEntry
{
    /// <summary>
    /// The name of the file or directory.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// The full path to the file or directory.
    /// </summary>
    public string FullPath { get; set; }

    /// <summary>
    /// Whether this entry is a directory.
    /// </summary>
    public bool IsDirectory { get; set; }

    public FileSystemEntry(string name, string fullPath, bool isDirectory)
    {
        Name = name;
        FullPath = fullPath;
        IsDirectory = isDirectory;
    }
}
