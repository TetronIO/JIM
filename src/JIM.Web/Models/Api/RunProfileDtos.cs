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
    /// Whether Verification Mode is enabled: the Full Import skips no object whose content looks unchanged, and
    /// instead raises an error for any object whose stored content hash disagrees with what was read. Only
    /// meaningful when <c>RunType</c> is <c>FullImport</c>.
    /// </summary>
    /// <remarks>SPEC-1082 D10.</remarks>
    public bool VerifyImportContentHashes { get; set; }

    /// <summary>
    /// Whether this Full Import carries out an Object Type change rather than refusing it. Only ever true when
    /// <c>RunType</c> is <c>FullImport</c>, and only has an effect on a Connected System whose Connector can tell an
    /// Object Type change from a new object, as the LDAP Connector can.
    /// </summary>
    /// <remarks>Allow Object Type Changes, #2049. See <see cref="CreateRunProfileRequest.AllowObjectTypeChanges"/> for
    /// behaviour.</remarks>
    public bool AllowObjectTypeChanges { get; set; }

    /// <summary>
    /// The limits on what this Run Profile may attempt. Always present.
    /// </summary>
    /// <remarks>Run Profile Safeguards, #1618.</remarks>
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
            AllowObjectTypeChanges = runProfile.AllowObjectTypeChanges,
            Safeguards = new RunProfileSafeguardsDto
            {
                MaxCreates = runProfile.MaxCreates,
                MaxUpdates = runProfile.MaxUpdates,
                MaxDeletes = runProfile.MaxDeletes,
                MaxDetectedDeletions = runProfile.MaxDetectedDeletions,
                MaxDetectedDeletionsPercent = runProfile.MaxDetectedDeletionsPercent
            }
        };
    }
}

/// <summary>
/// Run Profile Safeguards (#1618): the limits an administrator can set on what a Run Profile may
/// attempt in a single run. Null means no limit; zero is a valid limit ("attempt none of these").
/// </summary>
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

    /// <summary>
    /// The maximum number of Connected System Objects an import may newly mark as deleted in one run:
    /// those a Full Import no longer finds, or those a Delta Import's Connector reports deleted. Full
    /// Import and Delta Import Run Profiles only. If either this or <see cref="MaxDetectedDeletionsPercent"/>
    /// would be exceeded, the run marks nothing as deleted.
    /// </summary>
    public int? MaxDetectedDeletions { get; set; }

    /// <summary>
    /// The maximum share, 0 to 100, of the Connected System Objects in the run's scope that are not already
    /// marked as deleted, before it saves anything, that an import may newly mark as deleted. Full Import and
    /// Delta Import Run Profiles only.
    /// </summary>
    public int? MaxDetectedDeletionsPercent { get; set; }
}

/// <summary>
/// Response returned when a Run Profile execution is triggered.
/// </summary>
public class RunProfileExecutionResponse
{
    /// <summary>
    /// The Activity that records the execution, and reports its progress and outcome.
    /// </summary>
    public Guid ActivityId { get; set; }

    /// <summary>
    /// The worker task queued to carry out the execution.
    /// </summary>
    public Guid TaskId { get; set; }

    /// <summary>
    /// Message describing the result.
    /// </summary>
    public string Message { get; set; } = null!;

    /// <summary>
    /// Warnings about the execution, such as a partition validation warning, or that the preview it cites was out
    /// of date. Empty when there are none.
    /// </summary>
    public List<string> Warnings { get; set; } = new();
}
