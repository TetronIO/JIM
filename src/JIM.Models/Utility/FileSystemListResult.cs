namespace JIM.Models.Utility;

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
