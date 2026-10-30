// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using Microsoft.EntityFrameworkCore;

namespace JIM.PostgresData.Repositories;

/// <summary>
/// Recording a saved configuration change as awaiting synchronisation (#2615). The worker's side, claiming and applying
/// the rows, is in <c>SyncRepository.ConfigurationChangeSynchronisations.cs</c>.
/// </summary>
public partial class ConnectedSystemRepository
{
    /// <inheritdoc />
    public async Task<List<ObjectTypeAwaitingSynchronisation>> GetConfigurationChangesAwaitingSynchronisationAsync(IList<int> connectedSystemIds)
    {
        ArgumentNullException.ThrowIfNull(connectedSystemIds);
        if (connectedSystemIds.Count == 0)
            return [];

        // One grouped read, served by the partial index on the rows awaiting synchronisation. The Object Type's name is
        // joined in, so the caller needs no second query for it however many systems it asks about.
        return await (
                from change in Repository.Database.ConfigurationChangeSynchronisations
                join objectType in Repository.Database.ConnectedSystemObjectTypes on change.ConnectedSystemObjectTypeId equals objectType.Id
                where change.AppliedAt == null && connectedSystemIds.Contains(change.ConnectedSystemId)
                group change by new { change.ConnectedSystemId, change.ConnectedSystemObjectTypeId, ObjectTypeName = objectType.Name }
                into awaiting
                select new ObjectTypeAwaitingSynchronisation
                {
                    ConnectedSystemId = awaiting.Key.ConnectedSystemId,
                    ConnectedSystemObjectTypeId = awaiting.Key.ConnectedSystemObjectTypeId,
                    ObjectTypeName = awaiting.Key.ObjectTypeName,
                    ChangeCount = awaiting.Count(),
                    EarliestRequestedAt = awaiting.Min(c => c.RequestedAt)
                })
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<List<ConfigurationChangeSynchronisationSummary>> GetConfigurationChangeSynchronisationSummariesAwaitingAsync(int connectedSystemId, int maxChanges)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChanges, 1);

        return await SummariseConfigurationChanges(
                Repository.Database.ConfigurationChangeSynchronisations
                    .Where(change => change.ConnectedSystemId == connectedSystemId && change.AppliedAt == null))
            .OrderByDescending(summary => summary.RequestedAt)
            .ThenByDescending(summary => summary.Id)
            .Take(maxChanges)
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<List<ConfigurationChangeSynchronisationSummary>> GetConfigurationChangeSynchronisationSummariesForActivityAsync(Guid activityId, int maxChanges)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChanges, 1);

        // Both foreign keys are indexed. An Activity is a configuration change or a run, never both, so the two sides do
        // not overlap; the rows are the ones it recorded or the ones it claimed.
        return await SummariseConfigurationChanges(
                Repository.Database.ConfigurationChangeSynchronisations
                    .Where(change => change.ConfigurationChangeActivityId == activityId || change.ClaimedByActivityId == activityId))
            .OrderBy(summary => summary.ObjectTypeName)
            .ThenBy(summary => summary.RequestedAt)
            .ThenBy(summary => summary.Id)
            .Take(maxChanges)
            .ToListAsync();
    }

    // The rows with the names needed to read them: the Object Type, the Connected System, and who saved the change. The
    // Activity is a left join, because the row outlives it (history retention clears the reference rather than the row).
    private IQueryable<ConfigurationChangeSynchronisationSummary> SummariseConfigurationChanges(IQueryable<ConfigurationChangeSynchronisation> changes) =>
        from change in changes
        join objectType in Repository.Database.ConnectedSystemObjectTypes on change.ConnectedSystemObjectTypeId equals objectType.Id
        join system in Repository.Database.ConnectedSystems on change.ConnectedSystemId equals system.Id
        join activity in Repository.Database.Activities on change.ConfigurationChangeActivityId equals (Guid?)activity.Id into activities
        from activity in activities.DefaultIfEmpty()
        select new ConfigurationChangeSynchronisationSummary
        {
            Id = change.Id,
            ConnectedSystemId = change.ConnectedSystemId,
            ConnectedSystemName = system.Name,
            ConnectedSystemObjectTypeId = change.ConnectedSystemObjectTypeId,
            ObjectTypeName = objectType.Name,
            Reason = change.Reason,
            RequestedAt = change.RequestedAt,
            ConfigurationChangeActivityId = change.ConfigurationChangeActivityId,
            SyncRuleId = change.SyncRuleId,
            ClaimedByActivityId = change.ClaimedByActivityId,
            AppliedAt = change.AppliedAt,
            InitiatedByType = activity == null ? (ActivityInitiatorType?)null : activity.InitiatedByType,
            InitiatedById = activity == null ? null : activity.InitiatedById,
            InitiatedByName = activity == null ? null : activity.InitiatedByName
        };

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
        var entry = Repository.Database.ConfigurationChangeSynchronisations.Add(change);
        var saved = false;
        try
        {
            await Repository.Database.SaveChangesAsync();
            saved = true;
        }
        finally
        {
            // A row the database refused stays tracked as Added, and the next SaveChanges on this context sends it again.
            // The save that asked for it then fails its Activity on this same context, so the retry would fail that
            // write the same way and bury the Activity's failure under a second exception. The row is dropped here
            // and the original failure propagates untouched.
            if (!saved)
                entry.State = EntityState.Detached;
        }
    }
}
