using JIM.Models.Activities;
using JIM.Models.Interfaces;
namespace JIM.Models.Staging;

public class ConnectedSystemRunProfile : IAuditable
{
    public int Id { get; set; }

    /// <summary>
    /// The user-supplied name for this run profile.
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// When the entity was created (UTC).
    /// </summary>
    public DateTime Created { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The type of security principal that created this entity.
    /// </summary>
    public ActivityInitiatorType CreatedByType { get; set; }

    /// <summary>
    /// The unique identifier of the principal that created this entity.
    /// Null for system-created (seeded) entities.
    /// </summary>
    public Guid? CreatedById { get; set; }

    /// <summary>
    /// The display name of the principal at the time of creation.
    /// Retained even if the principal is later deleted.
    /// </summary>
    public string? CreatedByName { get; set; }

    /// <summary>
    /// When the entity was last modified (UTC). Null if never modified after creation.
    /// </summary>
    public DateTime? LastUpdated { get; set; }

    /// <summary>
    /// The type of security principal that last modified this entity.
    /// </summary>
    public ActivityInitiatorType LastUpdatedByType { get; set; }

    /// <summary>
    /// The unique identifier of the principal that last modified this entity.
    /// </summary>
    public Guid? LastUpdatedById { get; set; }

    /// <summary>
    /// The display name of the principal at the time of the last modification.
    /// </summary>
    public string? LastUpdatedByName { get; set; }

    /// <summary>
    /// Unique identifier for the parent object.
    /// </summary>
    public int ConnectedSystemId { get; set; }
        
    /// <summary>
    /// If the connected system implements partitions, then a run profile needs to target a partition.
    /// </summary>
    public ConnectedSystemPartition? Partition { get; set; }

    public ConnectedSystemRunType RunType { get; set; }

    /// <summary>
    /// How many items to process in one go via the Connector.
    /// </summary>
    public int PageSize { get; set; }

    /// <summary>
    /// If this run profile is for a file-based connector, then a full path to the file to read from/write to is required.
    /// This enables the user to specify different files for different run types, i.e. there might be a full-import file and a delta-import file.
    /// </summary>
    public string? FilePath { get; set; }

    /// <summary>
    /// Back-link to dependent activity objects. 
    /// Optional relationship.
    /// Used by EntityFramework.
    /// </summary>
    public List<Activity>? Activities { get; set; }

    public override string ToString() => Name;
}