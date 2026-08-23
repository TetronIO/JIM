// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// What a schema refresh removal task (#1485) actually removed, reported on its Activity and logged as the
/// batch's summary statistics.
/// </summary>
public class SchemaRefreshRemovalResult
{
    /// <summary>
    /// How many Connected System Objects were marked Obsolete across the removed Object Types.
    /// </summary>
    public int ConnectedSystemObjectsObsoleted { get; set; }

    /// <summary>
    /// How many Pending Exports were removed alongside the obsoleted objects, exactly as import-detected
    /// deletions remove them.
    /// </summary>
    public int PendingExportsRemoved { get; set; }

    /// <summary>
    /// How many stored attribute values were deleted across the removed attributes.
    /// </summary>
    public int AttributeValuesRemoved { get; set; }
}
