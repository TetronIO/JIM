// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional.DTOs;

/// <summary>
/// What became of one provisioned password change offered to the queue (#1697): whether it inserted a new row,
/// took over an existing one, or lost to a row that already carries the account's real password.
/// </summary>
public class ProvisionedPasswordStagingOutcome
{
    /// <summary>
    /// The id of the row now in the table for this change's (Metaverse Object, Connected System) key, or
    /// <see cref="Guid.Empty"/> for <see cref="ProvisionedPasswordStagingDisposition.Coalesced"/>: nothing was
    /// written, so there is no row to point at other than the one already there, which this statement never read.
    /// </summary>
    public Guid RowId { get; init; }

    /// <summary>
    /// The id the change itself was offered under, before any coalescing.
    /// </summary>
    public required Guid RequestedId { get; init; }

    public required ProvisionedPasswordStagingDisposition Disposition { get; init; }
}
