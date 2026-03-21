namespace JIM.Models.Utility;

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
