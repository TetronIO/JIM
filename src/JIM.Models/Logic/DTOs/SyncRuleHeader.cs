namespace JIM.Models.Logic.DTOs;

/// <summary>
/// Lightweight representation of a SyncRule for list views.
/// </summary>
public class SyncRuleHeader
{
    public int Id { get; set; }

    public string Name { get; set; } = null!;

    public DateTime Created { get; set; }

    public int ConnectedSystemId { get; set; }

    public string ConnectedSystemName { get; set; } = null!;

    public int ConnectedSystemObjectTypeId { get; set; }

    public string ConnectedSystemObjectTypeName { get; set; } = null!;

    public int MetaverseObjectTypeId { get; set; }

    public string MetaverseObjectTypeName { get; set; } = null!;

    public SyncRuleDirection Direction { get; set; }

    public bool? ProvisionToConnectedSystem { get; set; }

    public bool? ProjectToMetaverse { get; set; }

    public bool Enabled { get; set; }

    /// <summary>
    /// For Export rules: When true (default), inbound changes from the target system will trigger
    /// re-evaluation of this export rule to detect and remediate drift.
    /// Only applicable when Direction = Export.
    /// </summary>
    public bool EnforceState { get; set; }

    /// <summary>
    /// Creates a header from a SyncRule entity.
    /// </summary>
    public static SyncRuleHeader FromEntity(SyncRule entity)
    {
        return new SyncRuleHeader
        {
            Id = entity.Id,
            Name = entity.Name,
            Created = entity.Created,
            ConnectedSystemId = entity.ConnectedSystemId,
            ConnectedSystemName = entity.ConnectedSystem?.Name ?? string.Empty,
            ConnectedSystemObjectTypeId = entity.ConnectedSystemObjectTypeId,
            ConnectedSystemObjectTypeName = entity.ConnectedSystemObjectType?.Name ?? string.Empty,
            MetaverseObjectTypeId = entity.MetaverseObjectTypeId,
            MetaverseObjectTypeName = entity.MetaverseObjectType?.Name ?? string.Empty,
            Direction = entity.Direction,
            ProvisionToConnectedSystem = entity.ProvisionToConnectedSystem,
            ProjectToMetaverse = entity.ProjectToMetaverse,
            Enabled = entity.Enabled,
            EnforceState = entity.EnforceState
        };
    }
}