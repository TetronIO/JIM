// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// The well-known tags a Connector can attach to a Connected System Object Type attribute during schema discovery.
/// </summary>
/// <remarks>
/// Tags are connector-owned classification, held as plain values rather than an enum so a Connector can express a
/// concept JIM does not model without a schema change. A schema refresh replaces an attribute's tags with whatever
/// the Connector reports, so a tag is never accumulated or left stale. An attribute carrying no tags is
/// unclassified, which every consumer must treat as "show it, do not group it".
/// </remarks>
public static class AttributeTags
{
    /// <summary>
    /// An attribute the Connected System maintains for its own operation rather than one that describes the object,
    /// i.e. an RFC 4512 attribute type whose usage is not <c>userApplications</c> (<c>createTimestamp</c>,
    /// <c>memberOf</c>, OpenLDAP's <c>pwdFailureTime</c>).
    /// </summary>
    /// <remarks>
    /// Such an attribute belongs to no object class, is always read-only, and is discovered unselected: the schema
    /// screen groups and holds these back until asked for, because a directory publishes many and most are of no
    /// interest to identity management.
    /// </remarks>
    public const string Operational = "operational";
}
