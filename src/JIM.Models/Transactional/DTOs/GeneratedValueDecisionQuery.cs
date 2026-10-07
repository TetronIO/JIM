// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional.DTOs;

/// <summary>
/// A <see cref="GeneratedValueDecisionFilter"/> resolved by the application layer into what the repository can test
/// row by row (Unique Value Generation, #242, release 4, Phase 9): a Synchronisation Rule becomes its generated flows,
/// and a Connected System's participation becomes the generated flows exported to it, both of which are configuration
/// the repository does not interpret.
/// </summary>
public class GeneratedValueDecisionQuery
{
    /// <summary>
    /// Restrict to one status; null for both list statuses (<see cref="GeneratedValueDecisionStatus.NeedsDecision"/> and
    /// <see cref="GeneratedValueDecisionStatus.RenameAllowed"/>).
    /// </summary>
    public GeneratedValueDecisionStatus? Status { get; set; }

    /// <summary>
    /// Also return values that are not held (status <see cref="GeneratedValueDecisionStatus.Released"/>). Only meaningful
    /// with <see cref="Ids"/>: a read of one value by id answers whatever its state.
    /// </summary>
    public bool IncludeReleased { get; set; }

    /// <summary>
    /// Restrict to assignments of these generated flows (<c>SyncRuleMappingGeneration</c> ids); null for every flow. An
    /// empty collection matches nothing.
    /// </summary>
    public IReadOnlyCollection<int>? GenerationIds { get; set; }

    /// <summary>
    /// Restrict to decisions involving this Connected System: it rejected the value, it anchors the value, or the
    /// value's flow is among <see cref="GenerationIdsParticipatingInConnectedSystem"/>.
    /// </summary>
    public int? ConnectedSystemId { get; set; }

    /// <summary>
    /// The generated flows whose values are exported to and checked in <see cref="ConnectedSystemId"/>. Read only with it.
    /// </summary>
    public IReadOnlyCollection<int> GenerationIdsParticipatingInConnectedSystem { get; set; } = [];

    /// <summary>
    /// Restrict to one Metaverse Object: its import-mode values, and export-mode values on Connected System Objects
    /// joined to it.
    /// </summary>
    public Guid? MetaverseObjectId { get; set; }

    /// <summary>Restrict to these assignments.</summary>
    public IReadOnlyCollection<Guid>? Ids { get; set; }
}
