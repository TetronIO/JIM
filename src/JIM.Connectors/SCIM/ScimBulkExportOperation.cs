// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.SCIM;

/// <summary>
/// An export operation together with the position of the Pending Export it came from.
/// <para>
/// The index travels with the operation because it is the only thing that survives batching: a bulk
/// request reorders nothing itself, but the response may come back in any order and JIM has to return
/// one result per Pending Export in the order they arrived. The index is also what the operation's
/// <c>bulkId</c> is built from, so the correlation the provider echoes back is the same identity.
/// </para>
/// </summary>
internal sealed record ScimBulkExportOperation(int Index, ScimExportOperation Operation);
