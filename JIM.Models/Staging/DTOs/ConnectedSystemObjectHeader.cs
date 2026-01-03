namespace JIM.Models.Staging.DTOs;

public class ConnectedSystemObjectHeader
{
    #region accessors
    public Guid Id { get; set; }

    public int ConnectedSystemId { get; set; }

    public string? DisplayName { get; set; }

    public ConnectedSystemObjectAttributeValue? ExternalIdAttributeValue { get; set; }

    public string? ExternalIdAttributeName { get; set; }

    public ConnectedSystemObjectAttributeValue? SecondaryExternalIdAttributeValue { get; set; }

    public string? SecondaryExternalIdAttributeName { get; set; }

    public DateTime Created { get; set; } = DateTime.UtcNow;

    public DateTime? LastUpdated { get; set; }

    public int TypeId { get; set; }

    public string TypeName { get; set; } = string.Empty;

    public ConnectedSystemObjectStatus Status { get; set; } = ConnectedSystemObjectStatus.Normal;

    /// <summary>
    /// How was this CSO joined to an MVO, if at all?
    /// </summary>
    public ConnectedSystemObjectJoinType JoinType { get; set; } = ConnectedSystemObjectJoinType.NotJoined;

    /// <summary>
    /// When this Connector Space Object was joined to the Metaverse.
    /// </summary>
    public DateTime? DateJoined { get; set; }

    /// <summary>
    /// The display name from a pending export, if one exists and the CSO doesn't have a confirmed value.
    /// </summary>
    public string? PendingDisplayName { get; set; }

    /// <summary>
    /// The secondary external ID (e.g., DN) from a pending export, if one exists and the CSO doesn't have a confirmed value.
    /// </summary>
    public string? PendingSecondaryExternalId { get; set; }

    /// <summary>
    /// Whether there is a pending export for this CSO.
    /// </summary>
    public bool HasPendingExport { get; set; }
    #endregion
}