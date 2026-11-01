// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional.DTOs;

/// <summary>
/// What became of one Exchange change offered to the queue (#2021): whether it inserted a new row, superseded the row
/// already held for its account, or left a cancelled row declaring the same state as it was.
/// </summary>
public class ExchangeChangeStagingOutcome
{
    /// <summary>
    /// The id the change itself was offered under, before any coalescing.
    /// </summary>
    public required Guid RequestedId { get; init; }

    /// <summary>
    /// The id of the row now in the table for the change's account: the inserted row, the row superseded in place, or
    /// the cancelled row left as it was.
    /// </summary>
    public required Guid RowId { get; init; }

    public required ExchangeChangeStagingDisposition Disposition { get; init; }

    /// <summary>
    /// The Activity of the change the superseded row carried, so the supersession can be recorded beneath it. Null
    /// unless the disposition is <see cref="ExchangeChangeStagingDisposition.Superseded"/>.
    /// </summary>
    public Guid? PreviousActivityId { get; init; }
}
