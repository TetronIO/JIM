// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Web.Models.Api;

/// <summary>
/// DTO for a Run Profile in list views.
/// </summary>
public class RunProfileDto
{
    /// <summary>
    /// The unique identifier of the Run Profile.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The user-supplied name for this Run Profile.
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// The Connected System this Run Profile belongs to.
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
    /// The partition name if this Run Profile targets a specific partition.
    /// </summary>
    public string? PartitionName { get; set; }

    /// <summary>
    /// True when this Run Profile targets a partition that is no longer selected on the Connected System, which
    /// makes the Run Profile inoperable: a deselected partition is not managed by JIM, so executing this Run
    /// Profile is refused rather than reading scope the administrator has withdrawn.
    /// </summary>
    /// <remarks>
    /// Always false for a Run Profile that targets no partition; such a Run Profile follows whatever is selected.
    /// </remarks>
    public bool TargetsDeselectedPartition { get; set; }

    /// <summary>
    /// File path for file-based connectors.
    /// </summary>
    public string? FilePath { get; set; }

    /// <summary>
    /// SPEC-1082 D10: whether Verification Mode is enabled. Only meaningful when <see cref="RunType"/>
    /// is <c>FullImport</c>.
    /// </summary>
    public bool VerifyImportContentHashes { get; set; }

    /// <summary>
    /// Run Profile Safeguards (#1618): the limits on what this Run Profile may attempt. Always present.
    /// </summary>
    public RunProfileSafeguardsDto Safeguards { get; set; } = new();

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
            TargetsDeselectedPartition = runProfile.TargetsADeselectedPartition(),
            FilePath = runProfile.FilePath,
            VerifyImportContentHashes = runProfile.VerifyImportContentHashes,
            Safeguards = new RunProfileSafeguardsDto
            {
                MaxCreates = runProfile.MaxCreates,
                MaxUpdates = runProfile.MaxUpdates,
                MaxDeletes = runProfile.MaxDeletes
            }
        };
    }
}

/// <summary>
/// Run Profile Safeguards (#1618): the limits an administrator can set on what a Run Profile may
/// attempt in a single run. Null means no limit; zero is a valid limit ("attempt none of these").
/// </summary>
/// <remarks>
/// Layer 1 (this type) carries the three Export limits only. Layer 2 adds <c>MaxDetectedDeletions</c>
/// and <c>MaxDetectedDeletionsPercent</c> for Full Import's deletion-detection gate.
/// </remarks>
public class RunProfileSafeguardsDto
{
    /// <summary>
    /// The maximum number of creates an Export run may attempt. Export Run Profiles only.
    /// </summary>
    public int? MaxCreates { get; set; }

    /// <summary>
    /// The maximum number of updates an Export run may attempt. Export Run Profiles only.
    /// </summary>
    public int? MaxUpdates { get; set; }

    /// <summary>
    /// The maximum number of deletes an Export run may attempt. Export Run Profiles only.
    /// </summary>
    public int? MaxDeletes { get; set; }
}

/// <summary>
/// Response returned when a Run Profile execution is triggered.
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
