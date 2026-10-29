// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.PostgresData.Repositories;

/// <summary>
/// Recording a saved configuration change as awaiting synchronisation (#2615). The worker's side, claiming and applying
/// the rows, is in <c>SyncRepository.ConfigurationChangeSynchronisations.cs</c>.
/// </summary>
public partial class ConnectedSystemRepository
{
    /// <inheritdoc />
    public async Task CreateConfigurationChangeSynchronisationAsync(ConfigurationChangeSynchronisation change)
    {
        ArgumentNullException.ThrowIfNull(change);

        // A row is only ever created awaiting synchronisation. A caller handing over a claimed or applied one is a bug
        // that would record a change as already dealt with when no run has applied it, silently losing it.
        if (change.ClaimedByActivityId.HasValue || change.AppliedAt.HasValue)
            throw new ArgumentException(
                "A configuration change must be created awaiting synchronisation, with no claiming run and no applied time.",
                nameof(change));

        // Add() on an entity with no navigations attaches only this row, so it is safe on the portal's NoTracking
        // context (it traverses nothing it could re-insert). The foreign keys are scalars on purpose.
        Repository.Database.ConfigurationChangeSynchronisations.Add(change);
        await Repository.Database.SaveChangesAsync();
    }
}
