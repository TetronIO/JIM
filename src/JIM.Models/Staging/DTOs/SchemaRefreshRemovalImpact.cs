// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// The data a schema refresh's "Apply and Remove" option (#1485) would remove, counted before anything is
/// committed so the administrator confirms with the numbers in front of them: how many Connected System
/// Objects each removed Object Type still holds (each would be marked Obsolete and flow through the standard
/// deprovisioning pipeline), and how many stored values each removed attribute still carries (each would be
/// deleted).
/// </summary>
public class SchemaRefreshRemovalImpact
{
    /// <summary>
    /// One entry per Object Type the Connected System no longer reports, with the count of Connected System
    /// Objects that would be marked Obsolete.
    /// </summary>
    public List<SchemaRefreshRemovalTypeImpact> RemovedObjectTypes { get; set; } = new();

    /// <summary>
    /// One entry per removed attribute on a surviving Object Type, with the count of stored values that would
    /// be deleted.
    /// </summary>
    public List<SchemaRefreshRemovalAttributeImpact> RemovedAttributes { get; set; } = new();

    /// <summary>
    /// The total number of Connected System Objects the removal would mark Obsolete.
    /// </summary>
    public int TotalConnectedSystemObjects => RemovedObjectTypes.Sum(t => t.ConnectedSystemObjectCount);

    /// <summary>
    /// The total number of stored attribute values the removal would delete.
    /// </summary>
    public int TotalStoredValues => RemovedAttributes.Sum(a => a.StoredValueCount);
}

/// <summary>
/// The data impact of one removed Object Type: the Connected System Objects that would be marked Obsolete.
/// </summary>
public class SchemaRefreshRemovalTypeImpact
{
    public int ObjectTypeId { get; set; }
    public string ObjectTypeName { get; set; } = null!;
    public int ConnectedSystemObjectCount { get; set; }
}

/// <summary>
/// The data impact of one removed attribute on a surviving Object Type: the stored values that would be
/// deleted.
/// </summary>
public class SchemaRefreshRemovalAttributeImpact
{
    public int AttributeId { get; set; }
    public string AttributeName { get; set; } = null!;
    public string ObjectTypeName { get; set; } = null!;
    public int StoredValueCount { get; set; }
}
