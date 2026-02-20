using JIM.Models.Staging;

namespace JIM.Web.Models.Api;

/// <summary>
/// DTO for a run profile in list views.
/// </summary>
public class RunProfileDto
{
    /// <summary>
    /// The unique identifier of the run profile.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The user-supplied name for this run profile.
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// The connected system this run profile belongs to.
    /// </summary>
    public int ConnectedSystemId { get; set; }

    /// <summary>
    /// The type of synchronisation operation (FullImport, DeltaImport, FullSynchronisation, DeltaSynchronisation, Export).
    /// </summary>
    public ConnectedSystemRunType RunType { get; set; }

    /// <summary>
    /// How many items to process in one batch.
    /// </summary>
    public int PageSize { get; set; }

    /// <summary>
    /// The partition name if this run profile targets a specific partition.
    /// </summary>
    public string? PartitionName { get; set; }

    /// <summary>
    /// File path for file-based connectors.
    /// </summary>
    public string? FilePath { get; set; }

    /// <summary>
    /// Creates a DTO from a ConnectedSystemRunProfile entity.
    /// </summary>
    public static RunProfileDto FromEntity(ConnectedSystemRunProfile runProfile)
    {
        return new RunProfileDto
        {
            Id = runProfile.Id,
            Name = runProfile.Name,
            ConnectedSystemId = runProfile.ConnectedSystemId,
            RunType = runProfile.RunType,
            PageSize = runProfile.PageSize,
            PartitionName = runProfile.Partition?.Name,
            FilePath = runProfile.FilePath
        };
    }
}

/// <summary>
/// Response returned when a run profile execution is triggered.
/// </summary>
public class RunProfileExecutionResponse
{
    /// <summary>
    /// The activity ID for tracking the execution.
    /// </summary>
    public Guid ActivityId { get; set; }

    /// <summary>
    /// The worker task ID for the queued execution.
    /// </summary>
    public Guid TaskId { get; set; }

    /// <summary>
    /// Message describing the result.
    /// </summary>
    public string Message { get; set; } = null!;

    /// <summary>
    /// Any warning messages about the execution (e.g., partition validation warnings).
    /// Empty if no warnings.
    /// </summary>
    public List<string> Warnings { get; set; } = new();
}
