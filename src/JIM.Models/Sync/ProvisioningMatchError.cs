// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;

namespace JIM.Models.Sync;

/// <summary>
/// An outbound Synchronisation Rule that could not provision a Metaverse Object because export matching, which looks
/// for an existing Connected System Object to join first, could not settle on one (#2052): a rule's values found more
/// than one, or a rule could not be worked out. Nothing is joined, provisioned or staged for the Rule; the Metaverse
/// Object's other export Rules are unaffected. Surfaced to the administrator as a Run Profile Execution Item error,
/// listing the objects found where there were several.
/// </summary>
public class ProvisioningMatchError
{
    /// <summary>The Metaverse Object that was about to be provisioned.</summary>
    public required Guid MetaverseObjectId { get; init; }

    /// <summary>The outbound Synchronisation Rule that would have provisioned it.</summary>
    public required int SyncRuleId { get; init; }

    /// <summary>That Synchronisation Rule's name.</summary>
    public required string SyncRuleName { get; init; }

    /// <summary>The target Connected System.</summary>
    public required int ConnectedSystemId { get; init; }

    /// <summary>
    /// <see cref="ActivityRunProfileExecutionItemErrorType.AmbiguousProvisioningMatch"/> or
    /// <see cref="ActivityRunProfileExecutionItemErrorType.ObjectMatchingRuleFailed"/>.
    /// </summary>
    public required ActivityRunProfileExecutionItemErrorType ErrorType { get; init; }

    /// <summary>What went wrong and how to resolve it, as the administrator reads it.</summary>
    public required string Message { get; init; }

    /// <summary>The Connected System Objects an ambiguous match found, to list on the error; empty otherwise.</summary>
    public IReadOnlyList<ActivityRunProfileExecutionItemMatchCandidate> Candidates { get; init; } = [];
}
