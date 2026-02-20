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
    /// When this Connected System Object was joined to the Metaverse.
    /// </summary>
    public DateTime? DateJoined { get; set; }

    #region Pending Export Fields

    /// <summary>
    /// The pending Display Name value from a PendingExport, if one exists.
    /// </summary>
    public string? PendingDisplayName { get; set; }

    /// <summary>
    /// The pending External ID value from a PendingExport, if one exists.
    /// </summary>
    public string? PendingExternalId { get; set; }

    /// <summary>
    /// The secondary external ID (e.g., DN) from a pending export, if one exists and the CSO doesn't have a confirmed value.
    /// </summary>
    public string? PendingSecondaryExternalId { get; set; }

    /// <summary>
    /// Whether there is a pending export for this CSO.
    /// </summary>
    public bool HasPendingExport { get; set; }

    /// <summary>
    /// The ID of the PendingExport associated with this CSO, if one exists.
    /// </summary>
    public Guid? PendingExportId { get; set; }

    #endregion
    #endregion
}
