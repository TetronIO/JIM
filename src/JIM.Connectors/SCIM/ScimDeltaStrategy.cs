// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.SCIM;

/// <summary>
/// How a Delta Import finds out what changed. SCIM 2.0 defines no change feed, so the only mechanism
/// the protocol offers is a filter over <c>meta.lastModified</c>; everything else is a full scan.
/// </summary>
public enum ScimDeltaStrategy
{
    /// <summary>
    /// Choose from what the service provider advertises: filter by last-modified date where it supports
    /// filtering, and scan everything where it does not.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Ask the provider for the resources changed since the watermark of the last completed import.
    /// </summary>
    LastModifiedFilter = 1,

    /// <summary>
    /// Read every resource and let JIM work out what changed. Always available, and the floor every
    /// other strategy falls back to.
    /// </summary>
    FullScan = 2
}
