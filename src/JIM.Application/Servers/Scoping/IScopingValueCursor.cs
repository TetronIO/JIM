// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Application.Servers.Scoping;

/// <summary>
/// The values a criterion is evaluated over, one at a time. Implemented by structs and consumed through a generic
/// constraint, so walking them allocates nothing on synchronisation's hot path.
/// </summary>
internal interface IScopingValueCursor
{
    /// <summary>The next value, or false once there are no more.</summary>
    bool TryGetNext(out ScopingValue value);
}
