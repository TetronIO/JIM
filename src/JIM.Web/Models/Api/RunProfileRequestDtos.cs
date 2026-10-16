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

    /// <summary>
    /// Allow Object Type Changes (#2049). Defaults to false, and only valid when <see cref="RunType"/> is
    /// <c>FullImport</c>; the API rejects true on any other Run Type with a 400 response.
    /// <para>
    /// Where the Connector can tell an Object Type change from a new object (today only the LDAP Connector, whose
    /// External IDs name one object across every Object Type), an import object that has no match under its own
    /// Object Type but whose External ID JIM already holds under another Object Type is refused by default: the
    /// Run Profile Execution Item records an Object Type Changed error naming both Object Types, nothing is
    /// created, and the held Connected System Object is kept as it is and protected from deletion detection, so
    /// nothing is deprovisioned.
    /// </para>
    /// <para>
    /// When true, this Full Import carries the change out instead: it imports the object as a new Connected System
    /// Object of the new Object Type, and deletion detection retires (marks obsolete) the held one, whose next
    /// synchronisation disconnects it under the usual Deletion Rules. Usually set on a second, hand-run Full Import
    /// Run Profile used only when an Object Type change is intended. For Connectors that cannot tell the two apart
    /// (SQL, File, SCIM) the setting has no effect.
    /// </para>
    /// </summary>
    public bool AllowObjectTypeChanges { get; set; }

    /// <summary>
    /// Run Profile Safeguards (#1618): the limits this Run Profile should carry. Optional; omit for
    /// no limits. MaxCreates, MaxUpdates and MaxDeletes are only valid on an Export Run Profile, and
    /// MaxDetectedDeletions and MaxDetectedDeletionsPercent only on a Full Import or Delta Import Run
    /// Profile; the API rejects a non-null member on any other Run Type with a 400 response.
    /// </summary>
    public RunProfileSafeguardsDto? Safeguards { get; set; }
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

    /// <summary>
    /// Allow Object Type Changes (#2049): updates whether this Full Import carries out an Object Type change rather
    /// than refusing it. Omitted (null) leaves the current value unchanged; false is always accepted; true is
    /// rejected with a 400 response unless the Run Profile's Run Type is <c>FullImport</c>. Only has an effect for a
    /// Connector that can tell an Object Type change from a new object (today the LDAP Connector). See
    /// <see cref="CreateRunProfileRequest.AllowObjectTypeChanges"/> for behaviour.
    /// </summary>
    public bool? AllowObjectTypeChanges { get; set; }

    /// <summary>
    /// Run Profile Safeguards (#1618): when present, replaces ALL members of the Run Profile's
    /// safeguards: a null member clears that limit, a populated member sets it. Omit the whole object
    /// to leave every limit unchanged. MaxCreates, MaxUpdates and MaxDeletes are only valid on an Export
    /// Run Profile, and MaxDetectedDeletions and MaxDetectedDeletionsPercent only on a Full Import or
    /// Delta Import Run Profile; the API rejects a non-null member on any other Run Type with a 400 response.
    /// </summary>
    public RunProfileSafeguardsDto? Safeguards { get; set; }
}

/// <summary>
/// Optional request body for executing a Run Profile. Posting no body at all queues the run as before.
/// </summary>
public class ExecuteRunProfileRequest
{
    /// <summary>
    /// Optional: the Activity id of the Full Synchronisation preview read before starting this run, recorded on the
    /// run's Activity so its audit trail says what the run was expected to do. Must be a completed Full Synchronisation preview of this Connected
    /// System, and the Run Profile must be a Full Synchronisation; anything else is refused with a 400.
    /// </summary>
    public Guid? PreviewActivityId { get; set; }
}
