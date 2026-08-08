// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations;
using JIM.Models.Staging;

namespace JIM.Web.Models.Api;

/// <summary>
/// Request DTO for creating a new Run Profile.
/// </summary>
public class CreateRunProfileRequest
{
    /// <summary>
    /// The name for the Run Profile.
    /// </summary>
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string Name { get; set; } = null!;

    /// <summary>
    /// The type of synchronisation operation (FullImport, DeltaImport, FullSynchronisation, DeltaSynchronisation, Export).
    /// </summary>
    [Required]
    public ConnectedSystemRunType RunType { get; set; }

    /// <summary>
    /// How many items to process in one batch. Defaults to 100.
    /// </summary>
    [Range(1, 10000)]
    public int PageSize { get; set; } = 100;

    /// <summary>
    /// Optional partition ID if the connector supports partitions.
    /// </summary>
    public int? PartitionId { get; set; }

    /// <summary>
    /// Optional file path for file-based connectors.
    /// </summary>
    [StringLength(500)]
    public string? FilePath { get; set; }

    /// <summary>
    /// SPEC-1082 D10: enables Verification Mode. Only valid when <see cref="RunType"/> is
    /// <c>FullImport</c>; the API rejects true on any other Run Type with a 400 response. When
    /// enabled, the Full Import performs no content-hash skips and instead compares each object's
    /// stored import content hash against the freshly computed incoming hash, raising a diagnostic
    /// error for any disagreement the skip optimisation would otherwise have missed. Defaults to false.
    /// </summary>
    public bool VerifyImportContentHashes { get; set; }
}

/// <summary>
/// Request DTO for updating an existing Run Profile.
/// </summary>
public class UpdateRunProfileRequest
{
    /// <summary>
    /// The updated name for the Run Profile.
    /// </summary>
    [StringLength(200, MinimumLength = 1)]
    public string? Name { get; set; }

    /// <summary>
    /// The updated page size.
    /// </summary>
    [Range(1, 10000)]
    public int? PageSize { get; set; }

    /// <summary>
    /// Updated partition ID if the connector supports partitions.
    /// </summary>
    public int? PartitionId { get; set; }

    /// <summary>
    /// Updated file path for file-based connectors.
    /// </summary>
    [StringLength(500)]
    public string? FilePath { get; set; }

    /// <summary>
    /// SPEC-1082 D10: updates Verification Mode. Omitted (null) leaves the current value unchanged;
    /// true is rejected with a 400 response unless the Run Profile's Run Type is <c>FullImport</c>.
    /// See <see cref="CreateRunProfileRequest.VerifyImportContentHashes"/> for behaviour.
    /// </summary>
    public bool? VerifyImportContentHashes { get; set; }
}
