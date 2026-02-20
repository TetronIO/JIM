namespace JIM.Models.Staging.DTOs;

/// <summary>
/// Contains the results of a hierarchy refresh operation, including details about
/// what changed, what was added, what was removed, and any warnings.
/// </summary>
public class HierarchyRefreshResult
{
    /// <summary>
    /// Whether the hierarchy refresh completed successfully.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Error message if the refresh failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Total number of partitions after refresh.
    /// </summary>
    public int TotalPartitions { get; set; }

    /// <summary>
    /// Total number of containers after refresh.
    /// </summary>
    public int TotalContainers { get; set; }

    /// <summary>
    /// Partitions that were added (new to the hierarchy).
    /// </summary>
    public List<HierarchyChangeItem> AddedPartitions { get; set; } = new();

    /// <summary>
    /// Partitions that were removed from the hierarchy.
    /// </summary>
    public List<HierarchyChangeItem> RemovedPartitions { get; set; } = new();

    /// <summary>
    /// Partitions that were renamed (ExternalId matched, Name changed).
    /// </summary>
    public List<HierarchyRenameItem> RenamedPartitions { get; set; } = new();

    /// <summary>
    /// Containers that were added (new to the hierarchy).
    /// </summary>
    public List<HierarchyChangeItem> AddedContainers { get; set; } = new();

    /// <summary>
    /// Containers that were removed from the hierarchy.
    /// </summary>
    public List<HierarchyChangeItem> RemovedContainers { get; set; } = new();

    /// <summary>
    /// Containers that were renamed (ExternalId matched, Name changed).
    /// </summary>
    public List<HierarchyRenameItem> RenamedContainers { get; set; } = new();

    /// <summary>
    /// Containers that moved to a different parent (ExternalId matched, parent changed).
    /// </summary>
    public List<HierarchyMoveItem> MovedContainers { get; set; } = new();

    /// <summary>
    /// Whether there were any changes to the hierarchy.
    /// </summary>
    public bool HasChanges => AddedPartitions.Count > 0 ||
                              RemovedPartitions.Count > 0 ||
                              RenamedPartitions.Count > 0 ||
                              AddedContainers.Count > 0 ||
                              RemovedContainers.Count > 0 ||
                              RenamedContainers.Count > 0 ||
                              MovedContainers.Count > 0;

    /// <summary>
    /// Whether any selected items were removed (potential data loss warning).
    /// </summary>
    public bool HasSelectedItemsRemoved => RemovedPartitions.Any(p => p.WasSelected) ||
                                           RemovedContainers.Any(c => c.WasSelected);

    /// <summary>
    /// Gets a brief summary of changes for display.
    /// </summary>
    public string GetSummary()
    {
        var parts = new List<string>();

        var added = AddedPartitions.Count + AddedContainers.Count;
        var removed = RemovedPartitions.Count + RemovedContainers.Count;
        var updated = RenamedPartitions.Count + RenamedContainers.Count + MovedContainers.Count;

        if (added > 0) parts.Add($"{added} added");
        if (removed > 0) parts.Add($"{removed} removed");
        if (updated > 0) parts.Add($"{updated} updated");

        return parts.Count > 0 ? string.Join(", ", parts) : "No changes";
    }

    /// <summary>
    /// Creates a successful result with no changes.
    /// </summary>
    public static HierarchyRefreshResult NoChanges(int partitionCount, int containerCount)
    {
        return new HierarchyRefreshResult
        {
            Success = true,
            TotalPartitions = partitionCount,
            TotalContainers = containerCount
        };
    }

    /// <summary>
    /// Creates a failed result with an error message.
    /// </summary>
    public static HierarchyRefreshResult Failed(string errorMessage)
    {
        return new HierarchyRefreshResult
        {
            Success = false,
            ErrorMessage = errorMessage
        };
    }
}

/// <summary>
/// Represents a partition or container that was added or removed.
/// </summary>
public class HierarchyChangeItem
{
    /// <summary>
    /// The external identifier (e.g., DN for LDAP).
    /// </summary>
    public string ExternalId { get; set; } = null!;

    /// <summary>
    /// The human-readable name.
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// Whether this item was selected before removal (only relevant for removed items).
    /// </summary>
    public bool WasSelected { get; set; }

    /// <summary>
    /// The type of item (Partition or Container).
    /// </summary>
    public HierarchyItemType ItemType { get; set; }
}

/// <summary>
/// Represents a partition or container that was renamed.
/// </summary>
public class HierarchyRenameItem
{
    /// <summary>
    /// The external identifier (e.g., DN for LDAP).
    /// </summary>
    public string ExternalId { get; set; } = null!;

    /// <summary>
    /// The previous name before the rename.
    /// </summary>
    public string OldName { get; set; } = null!;

    /// <summary>
    /// The new name after the rename.
    /// </summary>
    public string NewName { get; set; } = null!;

    /// <summary>
    /// The type of item (Partition or Container).
    /// </summary>
    public HierarchyItemType ItemType { get; set; }
}

/// <summary>
/// Represents a container that moved to a different parent.
/// </summary>
public class HierarchyMoveItem
{
    /// <summary>
    /// The external identifier (e.g., DN for LDAP).
    /// </summary>
    public string ExternalId { get; set; } = null!;

    /// <summary>
    /// The human-readable name.
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// The external ID of the previous parent container (null if was at partition root).
    /// </summary>
    public string? OldParentExternalId { get; set; }

    /// <summary>
    /// The external ID of the new parent container (null if now at partition root).
    /// </summary>
    public string? NewParentExternalId { get; set; }
}

/// <summary>
/// The type of hierarchy item.
/// </summary>
public enum HierarchyItemType
{
    /// <summary>
    /// A partition (top-level container grouping).
    /// </summary>
    Partition,

    /// <summary>
    /// A container (organisational unit or folder).
    /// </summary>
    Container
}
