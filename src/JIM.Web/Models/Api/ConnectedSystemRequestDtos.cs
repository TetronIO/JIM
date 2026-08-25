// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations;
using JIM.Models.Core;
using JIM.Models.Staging;

namespace JIM.Web.Models.Api;

/// <summary>
/// Request DTO for creating a new Connected System.
/// </summary>
public class CreateConnectedSystemRequest
{
    /// <summary>
    /// The name for the Connected System.
    /// </summary>
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string Name { get; set; } = null!;

    /// <summary>
    /// Optional description for the Connected System.
    /// </summary>
    [StringLength(1000)]
    public string? Description { get; set; }

    /// <summary>
    /// The ID of the ConnectorDefinition to use.
    /// </summary>
    [Required]
    public int ConnectorDefinitionId { get; set; }

    /// <summary>
    /// An optional reason for the change, recorded against this Connected System's change history.
    /// </summary>
    [StringLength(2000)]
    public string? ChangeReason { get; set; }
}

/// <summary>
/// Request DTO for updating an existing Connected System.
/// </summary>
public class UpdateConnectedSystemRequest
{
    /// <summary>
    /// The updated name for the Connected System.
    /// </summary>
    [StringLength(200, MinimumLength = 1)]
    public string? Name { get; set; }

    /// <summary>
    /// The updated description for the Connected System.
    /// </summary>
    [StringLength(1000)]
    public string? Description { get; set; }

    /// <summary>
    /// Updated setting values as key-value pairs where key is the setting ID.
    /// </summary>
    public Dictionary<int, ConnectedSystemSettingValueUpdate>? SettingValues { get; set; }

    /// <summary>
    /// Maximum number of export batches to process concurrently.
    /// Only applicable when the connector supports parallel export.
    /// </summary>
    [Range(1, 16)]
    public int? MaxExportParallelism { get; set; }

    /// <summary>
    /// How long an account provisioned into this Connected System stays owed an initial password before JIM
    /// records an expiry and stops trying. Omitted or null leaves the current value unchanged; JIM's default when
    /// none has ever been set is seven days. Raise it ahead of a planned outage longer than the current window,
    /// or every account provisioned meanwhile expires without a password.
    /// </summary>
    public TimeSpan? InitialPasswordTimeToLive { get; set; }

    /// <summary>
    /// Whether to refuse to send a password to this Connected System over a connection JIM cannot confirm is
    /// encrypted. Omitted or null leaves the current value unchanged; off when never set.
    /// <para>
    /// Off by default because a signed and sealed bind is a legitimate encrypted alternative that cannot be
    /// detected from the Connected System's settings alone, so refusing on the settings would refuse a valid
    /// configuration. Turning it on applies to every password JIM sends to this system.
    /// </para>
    /// </summary>
    public bool? RequireSecureTransport { get; set; }

    /// <summary>
    /// Controls how an import-time reference attribute value that cannot be resolved to a Connected System Object
    /// is treated: Error (default), Warn, or Ignore. Null or omitted leaves the current value unchanged.
    /// </summary>
    [EnumDataType(typeof(UnresolvedReferenceHandling))]
    public UnresolvedReferenceHandling? UnresolvedReferenceHandling { get; set; }

    /// <summary>
    /// An optional reason for the change, recorded against this Connected System's change history.
    /// </summary>
    [StringLength(2000)]
    public string? ChangeReason { get; set; }
}

/// <summary>
/// DTO for updating a single setting value.
/// </summary>
public class ConnectedSystemSettingValueUpdate
{
    /// <summary>
    /// String value for String or StringEncrypted settings.
    /// </summary>
    public string? StringValue { get; set; }

    /// <summary>
    /// Integer value for Integer settings.
    /// </summary>
    public int? IntValue { get; set; }

    /// <summary>
    /// Checkbox/boolean value for CheckBox settings.
    /// </summary>
    public bool? CheckboxValue { get; set; }
}

/// <summary>
/// Request DTO for updating a Connected System Object Type.
/// </summary>
public class UpdateConnectedSystemObjectTypeRequest
{
    /// <summary>
    /// Whether this object type is selected for management by JIM.
    /// </summary>
    public bool? Selected { get; set; }

    /// <summary>
    /// Controls whether Metaverse Object attribute values contributed by a Connected System Object of this type
    /// should be removed when the CSO is obsoleted.
    /// </summary>
    public bool? RemoveContributedAttributesOnObsoletion { get; set; }
}

/// <summary>
/// Request DTO for setting which auxiliary classes a Connected System Object Type carries.
/// </summary>
/// <remarks>
/// The whole set, not a delta: whatever is not named here is withdrawn. An empty list withdraws every selection,
/// which is deliberately expressible rather than requiring a separate endpoint.
/// </remarks>
public class SetObjectTypeAuxiliaryClassesRequest
{
    /// <summary>
    /// The auxiliary classes the Object Type should carry, by their own Connected System Object Type ids. Each must
    /// be an auxiliary class in the same Connected System.
    /// </summary>
    public List<int>? ObjectTypeIds { get; set; }
}

/// <summary>
/// Request DTO for setting the Structural Carrier Class of an auxiliary Connected System Object Type.
/// </summary>
public class SetObjectTypeStructuralCarrierRequest
{
    /// <summary>
    /// The structural Object Type JIM writes alongside the auxiliary class when creating an object, or null to
    /// clear the carrier and leave the Object Type importable but not creatable.
    /// </summary>
    public int? StructuralCarrierObjectTypeId { get; set; }
}

/// <summary>
/// Request DTO for starting an auxiliary class discovery run.
/// </summary>
public class StartAuxiliaryClassDiscoveryRequest
{
    /// <summary>
    /// How much of the Connected System to read: a quick sample of each Object Type, or a full scan of everything
    /// in scope.
    /// </summary>
    [Required]
    public AuxiliaryClassDiscoveryScope Scope { get; set; }

    /// <summary>
    /// How many entries of each Object Type a quick sample should read. Required for a quick sample, and ignored
    /// for a full scan, which has no per-type limit.
    /// </summary>
    public int? SampleSizePerObjectType { get; set; }
}

/// <summary>
/// Response DTO for a queued auxiliary class discovery run.
/// </summary>
public class AuxiliaryClassDiscoveryStartedDto
{
    /// <summary>
    /// The queued task, for a caller that wants to cancel it.
    /// </summary>
    public Guid? WorkerTaskId { get; set; }

    /// <summary>
    /// The Activity carrying the run's progress, cancellation and errors.
    /// </summary>
    public Guid? ActivityId { get; set; }
}

/// <summary>
/// Request DTO for updating a Connected System Attribute.
/// </summary>
public class UpdateConnectedSystemAttributeRequest
{
    /// <summary>
    /// Whether this attribute is selected for management by JIM.
    /// </summary>
    public bool? Selected { get; set; }

    /// <summary>
    /// Indicates if this attribute is a unique identifier for the object type in the Connected System.
    /// </summary>
    public bool? IsExternalId { get; set; }

    /// <summary>
    /// Indicates if this attribute is used as a secondary identifier by the Connected System (e.g., DN in LDAP).
    /// </summary>
    public bool? IsSecondaryExternalId { get; set; }

    /// <summary>
    /// Overrides the data type schema discovery inferred for this attribute.
    /// </summary>
    /// <remarks>
    /// Accepted only where the Connector declares that its schema cannot state a type definitively
    /// (<c>SupportsUserSelectedAttributeTypes</c>): a delimited file names no types at all, and Oracle
    /// has a single numeric type, so a <c>NUMBER</c> column may be a whole number, a counter or a
    /// fractional figure. Refused once the attribute is referenced by a Synchronisation Rule or holds
    /// values, because changing the type would reinterpret data already imported under the old one.
    /// </remarks>
    public AttributeDataType? Type { get; set; }
}

/// <summary>
/// Request DTO for bulk updating multiple Connected System Attributes.
/// </summary>
public class BulkUpdateConnectedSystemAttributesRequest
{
    /// <summary>
    /// Dictionary of attribute updates keyed by attribute ID.
    /// </summary>
    [Required]
    public Dictionary<int, UpdateConnectedSystemAttributeRequest> Attributes { get; set; } = new();
}

/// <summary>
/// Response from a bulk attribute update operation.
/// </summary>
public class BulkUpdateConnectedSystemAttributesResponse
{
    /// <summary>
    /// The activity ID for the bulk update operation.
    /// </summary>
    public Guid ActivityId { get; set; }

    /// <summary>
    /// Number of attributes successfully updated.
    /// </summary>
    public int UpdatedCount { get; set; }

    /// <summary>
    /// List of updated attributes.
    /// </summary>
    public List<ConnectedSystemAttributeDto> UpdatedAttributes { get; set; } = new();

    /// <summary>
    /// Any errors that occurred during the update. Null if no errors.
    /// </summary>
    public List<BulkUpdateAttributeError>? Errors { get; set; }
}

/// <summary>
/// Error details for a failed attribute update in a bulk operation.
/// </summary>
public class BulkUpdateAttributeError
{
    /// <summary>
    /// The ID of the attribute that failed to update.
    /// </summary>
    public int AttributeId { get; set; }

    /// <summary>
    /// The error message describing why the update failed.
    /// </summary>
    public string ErrorMessage { get; set; } = null!;
}

/// <summary>
/// Request DTO for updating a Connected System Partition.
/// </summary>
public class UpdateConnectedSystemPartitionRequest
{
    /// <summary>
    /// Whether this partition is selected for import operations.
    /// When selected, objects within this partition will be imported during sync.
    /// </summary>
    public bool? Selected { get; set; }
}

/// <summary>
/// Request DTO for updating a Connected System Container.
/// </summary>
public class UpdateConnectedSystemContainerRequest
{
    /// <summary>
    /// Whether this container is selected for import operations.
    /// When selected, objects within this container will be imported during sync.
    /// </summary>
    public bool? Selected { get; set; }

    /// <summary>
    /// Whether this Container is carved out of a selection an ancestor made, leaving the objects within it
    /// deliberately unimported. Mutually exclusive with <see cref="Selected"/>: a request that would leave both set
    /// is rejected with 400, whether it states both itself or states one against a stored other.
    /// Omit to leave the stored exclusion unchanged.
    /// </summary>
    public bool? Excluded { get; set; }

    /// <summary>
    /// How far beneath this Container objects are imported from, when it is selected. Subtree imports from this
    /// Container and every Container beneath it; OneLevel imports only the objects held directly in it, leaving
    /// Containers beneath it to be selected in their own right.
    /// Omit to leave the stored scope unchanged.
    /// </summary>
    public ConnectedSystemContainerScope? Scope { get; set; }
}
