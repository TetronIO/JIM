// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Scim;

/// <summary>
/// What a SCIM request did, as the summary Activity counts it.
/// </summary>
public enum ScimRequestOutcome
{
    /// <summary>
    /// Not counted: a read, a search, an authentication failure (the security audit's) or a server error (logged).
    /// </summary>
    NotCounted = 0,

    /// <summary>
    /// A resource created.
    /// </summary>
    Created = 1,

    /// <summary>
    /// A replace or patch accepted.
    /// </summary>
    Updated = 2,

    /// <summary>
    /// A resource deleted.
    /// </summary>
    Deleted = 3,

    /// <summary>
    /// A request refused with a 4xx answer other than an authentication failure.
    /// </summary>
    Rejected = 4
}
