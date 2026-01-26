namespace JIM.Web.Models.Api;

/// <summary>
/// Response item for a deleted CSO in the deleted objects view.
/// </summary>
public class DeletedCsoResponse
{
    /// <summary>
    /// The unique identifier of the change record.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The external ID of the deleted CSO (preserved at deletion time).
    /// </summary>
    public string? ExternalId { get; set; }

    /// <summary>
    /// The display name of the deleted CSO (preserved at deletion time).
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// The object type name of the deleted CSO.
    /// </summary>
    public string? ObjectTypeName { get; set; }

    /// <summary>
    /// The Connected System ID the deleted CSO belonged to.
    /// </summary>
    public int ConnectedSystemId { get; set; }

    /// <summary>
    /// The Connected System name (if available).
    /// </summary>
    public string? ConnectedSystemName { get; set; }

    /// <summary>
    /// When the deletion occurred.
    /// </summary>
    public DateTime ChangeTime { get; set; }

    /// <summary>
    /// The type of security principal that initiated the deletion.
    /// </summary>
    public string? InitiatedByType { get; set; }

    /// <summary>
    /// The display name of the security principal that initiated the deletion.
    /// </summary>
    public string? InitiatedByName { get; set; }
}

/// <summary>
/// Response item for a deleted MVO in the deleted objects view.
/// </summary>
public class DeletedMvoResponse
{
    /// <summary>
    /// The unique identifier of the change record.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The display name of the deleted MVO (preserved at deletion time).
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// The object type name of the deleted MVO.
    /// </summary>
    public string? ObjectTypeName { get; set; }

    /// <summary>
    /// The object type ID of the deleted MVO.
    /// </summary>
    public int? ObjectTypeId { get; set; }

    /// <summary>
    /// When the deletion occurred.
    /// </summary>
    public DateTime ChangeTime { get; set; }

    /// <summary>
    /// The type of security principal that initiated the deletion.
    /// </summary>
    public string? InitiatedByType { get; set; }

    /// <summary>
    /// The display name of the security principal that initiated the deletion.
    /// </summary>
    public string? InitiatedByName { get; set; }
}

/// <summary>
/// Paginated response for deleted objects queries.
/// </summary>
/// <typeparam name="T">The type of deleted object response.</typeparam>
public class DeletedObjectsPagedResponse<T>
{
    /// <summary>
    /// The deleted object records.
    /// </summary>
    public List<T> Items { get; set; } = new();

    /// <summary>
    /// Total count of matching records (for pagination).
    /// </summary>
    public int TotalCount { get; set; }

    /// <summary>
    /// Current page number (1-based).
    /// </summary>
    public int Page { get; set; }

    /// <summary>
    /// Number of items per page.
    /// </summary>
    public int PageSize { get; set; }
}
