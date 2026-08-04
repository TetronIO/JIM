// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Connectors.SCIM;

/// <summary>
/// A Pending Export ready to dispatch: either the operation that applies it, or the outcome that
/// settled it without a request at all (an attribute the provider's schema does not have, a missing
/// External ID, or a change that amounted to nothing).
/// </summary>
internal sealed record ScimPreparedExport
{
    private ScimPreparedExport()
    {
    }

    /// <summary>The outcome, where the change was decided without asking the provider.</summary>
    public ConnectedSystemExportResult? Settled { get; private init; }

    /// <summary>The request that applies the change, where one is needed.</summary>
    public ScimExportOperation? Operation { get; private init; }

    public static ScimPreparedExport From(ConnectedSystemExportResult result) => new() { Settled = result };

    public static ScimPreparedExport From(ScimExportOperation operation) => new() { Operation = operation };
}
