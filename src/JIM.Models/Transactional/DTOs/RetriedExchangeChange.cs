// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional.DTOs;

/// <summary>
/// A queued Exchange change an administrator's retry made due again (#2021, Phase 6b): what the retry records under the
/// change's own Activity, so the change's history says who retried it and when.
/// </summary>
public class RetriedExchangeChange
{
    /// <summary>The queued change.</summary>
    public Guid Id { get; init; }

    /// <summary>The change's own Activity.</summary>
    public Guid ActivityId { get; init; }

    /// <summary>The Connected System whose Exchange the change is for.</summary>
    public int ConnectedSystemId { get; init; }

    /// <summary>The account the change is for.</summary>
    public Guid? ConnectedSystemObjectId { get; init; }

    /// <summary>The identity whose account it is.</summary>
    public Guid? MetaverseObjectId { get; init; }

    /// <summary>The kind of recipient declared.</summary>
    public string RecipientType { get; init; } = string.Empty;
}
