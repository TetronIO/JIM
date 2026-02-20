using JIM.Models.Staging.DTOs;

namespace JIM.Web.Models.Api;

/// <summary>
/// API response for hierarchy refresh operation.
/// </summary>
public class HierarchyRefreshResultDto
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
    /// Brief summary of changes (e.g., "2 added, 1 removed, 3 updated").
    /// </summary>
    public string Summary { get; set; } = null!;

    /// <summary>
    /// Whether there were any changes to the hierarchy.
    /// </summary>
    public bool HasChanges { get; set; }

    /// <summary>
    /// Whether any selected items were removed (potential data loss warning).
    /// </summary>
    public bool HasSelectedItemsRemoved { get; set; }

    /// <summary>
    /// Partitions that were added (new to the hierarchy).
    /// </summary>
    public List<HierarchyChangeItemDto> AddedPartitions { get; set; } = new();

    /// <summary>
    /// Partitions that were removed from the hierarchy.
    /// </summary>
    public List<HierarchyChangeItemDto> RemovedPartitions { get; set; } = new();

    /// <summary>
    /// Partitions that were renamed (ExternalId matched, Name changed).
    /// </summary>
    public List<HierarchyRenameItemDto> RenamedPartitions { get; set; } = new();

    /// <summary>
    /// Containers that were added (new to the hierarchy).
    /// </summary>
    public List<HierarchyChangeItemDto> AddedContainers { get; set; } = new();

    /// <summary>
    /// Containers that were removed from the hierarchy.
    /// </summary>
    public List<HierarchyChangeItemDto> RemovedContainers { get; set; } = new();

    /// <summary>
    /// Containers that were renamed (ExternalId matched, Name changed).
    /// </summary>
    public List<HierarchyRenameItemDto> RenamedContainers { get; set; } = new();

    /// <summary>
    /// Containers that moved to a different parent (ExternalId matched, parent changed).
    /// </summary>
    public List<HierarchyMoveItemDto> MovedContainers { get; set; } = new();

    /// <summary>
    /// Creates an API DTO from the domain model.
    /// </summary>
    public static HierarchyRefreshResultDto FromModel(HierarchyRefreshResult result)
    {
        return new HierarchyRefreshResultDto
        {
            Success = result.Success,
            ErrorMessage = result.ErrorMessage,
            TotalPartitions = result.TotalPartitions,
            TotalContainers = result.TotalContainers,
            Summary = result.GetSummary(),
            HasChanges = result.HasChanges,
            HasSelectedItemsRemoved = result.HasSelectedItemsRemoved,
            AddedPartitions = result.AddedPartitions.Select(HierarchyChangeItemDto.FromModel).ToList(),
            RemovedPartitions = result.RemovedPartitions.Select(HierarchyChangeItemDto.FromModel).ToList(),
            RenamedPartitions = result.RenamedPartitions.Select(HierarchyRenameItemDto.FromModel).ToList(),
            AddedContainers = result.AddedContainers.Select(HierarchyChangeItemDto.FromModel).ToList(),
            RemovedContainers = result.RemovedContainers.Select(HierarchyChangeItemDto.FromModel).ToList(),
            RenamedContainers = result.RenamedContainers.Select(HierarchyRenameItemDto.FromModel).ToList(),
            MovedContainers = result.MovedContainers.Select(HierarchyMoveItemDto.FromModel).ToList()
        };
    }
}

/// <summary>
/// Represents a partition or container that was added or removed.
/// </summary>
public class HierarchyChangeItemDto
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
    public string ItemType { get; set; } = null!;

    /// <summary>
    /// Creates an API DTO from the domain model.
    /// </summary>
    public static HierarchyChangeItemDto FromModel(HierarchyChangeItem item)
    {
        return new HierarchyChangeItemDto
        {
            ExternalId = item.ExternalId,
            Name = item.Name,
            WasSelected = item.WasSelected,
            ItemType = item.ItemType.ToString()
        };
    }
}

/// <summary>
/// Represents a partition or container that was renamed.
/// </summary>
public class HierarchyRenameItemDto
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
    public string ItemType { get; set; } = null!;

    /// <summary>
    /// Creates an API DTO from the domain model.
    /// </summary>
    public static HierarchyRenameItemDto FromModel(HierarchyRenameItem item)
    {
        return new HierarchyRenameItemDto
        {
            ExternalId = item.ExternalId,
            OldName = item.OldName,
            NewName = item.NewName,
            ItemType = item.ItemType.ToString()
        };
    }
}

/// <summary>
/// Represents a container that moved to a different parent.
/// </summary>
public class HierarchyMoveItemDto
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

    /// <summary>
    /// Creates an API DTO from the domain model.
    /// </summary>
    public static HierarchyMoveItemDto FromModel(HierarchyMoveItem item)
    {
        return new HierarchyMoveItemDto
        {
            ExternalId = item.ExternalId,
            Name = item.Name,
            OldParentExternalId = item.OldParentExternalId,
            NewParentExternalId = item.NewParentExternalId
        };
    }
}
