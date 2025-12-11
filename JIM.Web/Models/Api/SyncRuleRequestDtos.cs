using System.ComponentModel.DataAnnotations;
using JIM.Models.Logic;

namespace JIM.Web.Models.Api;

/// <summary>
/// Request DTO for creating a new Sync Rule.
/// </summary>
public class CreateSyncRuleRequest
{
    /// <summary>
    /// The name for the Sync Rule.
    /// </summary>
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string Name { get; set; } = null!;

    /// <summary>
    /// The ID of the Connected System this rule applies to.
    /// </summary>
    [Required]
    public int ConnectedSystemId { get; set; }

    /// <summary>
    /// The ID of the Connected System Object Type.
    /// </summary>
    [Required]
    public int ConnectedSystemObjectTypeId { get; set; }

    /// <summary>
    /// The ID of the Metaverse Object Type.
    /// </summary>
    [Required]
    public int MetaverseObjectTypeId { get; set; }

    /// <summary>
    /// The direction of the sync rule (Import or Export).
    /// </summary>
    [Required]
    public SyncRuleDirection Direction { get; set; }

    /// <summary>
    /// Whether this rule should also cause objects to be projected to the Metaverse (for Import rules).
    /// </summary>
    public bool? ProjectToMetaverse { get; set; }

    /// <summary>
    /// Whether this rule should also cause objects to be provisioned to the Connected System (for Export rules).
    /// </summary>
    public bool? ProvisionToConnectedSystem { get; set; }

    /// <summary>
    /// Whether the sync rule is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Request DTO for updating an existing Sync Rule.
/// </summary>
public class UpdateSyncRuleRequest
{
    /// <summary>
    /// The updated name for the Sync Rule.
    /// </summary>
    [StringLength(200, MinimumLength = 1)]
    public string? Name { get; set; }

    /// <summary>
    /// Whether the sync rule is enabled.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>
    /// Whether this rule should also cause objects to be projected to the Metaverse (for Import rules).
    /// </summary>
    public bool? ProjectToMetaverse { get; set; }

    /// <summary>
    /// Whether this rule should also cause objects to be provisioned to the Connected System (for Export rules).
    /// </summary>
    public bool? ProvisionToConnectedSystem { get; set; }
}
