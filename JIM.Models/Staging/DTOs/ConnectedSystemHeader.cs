namespace JIM.Models.Staging.DTOs;

/// <summary>
/// Lightweight representation of a ConnectedSystem for list views.
/// </summary>
public class ConnectedSystemHeader
{
    public int Id { get; set; }

    public string Name { get; set; } = null!;

    public string? Description { get; set; }

    public DateTime Created { get; set; }

    public ConnectedSystemStatus Status { get; set; }

    public int ObjectCount { get; set; }

    public int ConnectorsCount { get; set; }

    public int PendingExportObjectsCount { get; set; }

    public string ConnectorName { get; set; } = null!;

    public int ConnectorId { get; set; }

    public override string ToString() => Name;

    /// <summary>
    /// Creates a header from a ConnectedSystem entity.
    /// </summary>
    public static ConnectedSystemHeader FromEntity(ConnectedSystem entity)
    {
        return new ConnectedSystemHeader
        {
            Id = entity.Id,
            Name = entity.Name,
            Description = entity.Description,
            Created = entity.Created,
            Status = entity.Status,
            ObjectCount = entity.Objects?.Count ?? 0,
            ConnectorsCount = entity.ObjectTypes?.Count ?? 0,
            PendingExportObjectsCount = entity.PendingExports?.Count ?? 0,
            ConnectorName = entity.ConnectorDefinition?.Name ?? string.Empty,
            ConnectorId = entity.ConnectorDefinition?.Id ?? 0
        };
    }
}