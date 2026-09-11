// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Linq.Expressions;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Scheduling;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using JIM.Models.Utility;
using Microsoft.EntityFrameworkCore;
namespace JIM.PostgresData.Repositories;

public class ActivityRepository : IActivityRepository
{
    private PostgresDataRepository Repository { get; }

    internal ActivityRepository(PostgresDataRepository dataRepository)
    {
        Repository = dataRepository;
    }

    public async Task CreateActivityAsync(Activity activity)
    {
        Repository.Database.Activities.Add(activity);
        await Repository.Database.SaveChangesAsync();
    }

    /// <inheritdoc />
    public async Task CreateActivityRunProfileExecutionItemsAsync(IReadOnlyCollection<ActivityRunProfileExecutionItem> items)
    {
        if (items.Count == 0)
            return;

        foreach (var item in items.Where(i => i.Id == Guid.Empty))
            item.Id = Guid.NewGuid();

        // AddRange traverses each item's navigation graph and marks every untracked entity it reaches as Added.
        // Connected System Object change snapshots carried on the sync outcomes reference pre-existing entities
        // (the CSO the change belongs to, attribute definitions), which must not be re-inserted. Sever those
        // navigations first, preserving their scalar/shadow foreign keys so the persisted rows keep the links.
        var severedAttributeIds = new List<(ConnectedSystemObjectChangeAttribute AttributeChange, int AttributeId)>();
        var severedReferenceValueIds = new List<(ConnectedSystemObjectChangeAttributeValue ValueChange, Guid ReferenceCsoId)>();
        var changeSnapshots = items
            .SelectMany(i => i.SyncOutcomes.Select(o => o.ConnectedSystemObjectChange))
            .Concat(items.Select(i => i.ConnectedSystemObjectChange))
            .Where(c => c != null)
            .Select(c => c!)
            .Distinct()
            .ToList();

        foreach (var change in changeSnapshots)
        {
            if (change.ConnectedSystemObject != null)
            {
                change.ConnectedSystemObjectId ??= change.ConnectedSystemObject.Id;
                change.ConnectedSystemObject = null;
            }

            foreach (var attributeChange in change.AttributeChanges)
            {
                if (attributeChange.Attribute != null)
                {
                    severedAttributeIds.Add((attributeChange, attributeChange.Attribute.Id));
                    attributeChange.Attribute = null;
                }

                foreach (var valueChange in attributeChange.ValueChanges.Where(vc => vc.ReferenceValue != null))
                {
                    severedReferenceValueIds.Add((valueChange, valueChange.ReferenceValue!.Id));
                    valueChange.ReferenceValue = null;
                }
            }
        }

        Repository.Database.ActivityRunProfileExecutionItems.AddRange(items);

        // Re-apply the severed foreign keys via their shadow properties now the entities are tracked.
        foreach (var (attributeChange, attributeId) in severedAttributeIds)
            Repository.Database.Entry(attributeChange).Property("AttributeId").CurrentValue = attributeId;
        foreach (var (valueChange, referenceCsoId) in severedReferenceValueIds)
            Repository.Database.Entry(valueChange).Property("ReferenceValueId").CurrentValue = referenceCsoId;

        await Repository.Database.SaveChangesAsync();

        // Maintain the Activity stat counters (#1078) for this EF-persisted batch too, so
        // housekeeping Activities read their stats from counters like the bulk sync paths.
        // Runs after SaveChangesAsync so EF's fixup has stamped each outcome's RPEI foreign key.
        var syncOutcomes = items.SelectMany(i => i.SyncOutcomes).ToList();
        await ActivityStatCounterWriter.UpsertDeltasAsync(
            Repository.Database, ActivityStatCounterCalculator.CalculateRpeiInsertDeltas(items, syncOutcomes));

        // Drain any causal edges buffered on these items (#1223). This path is EF-based and the buffer is
        // deliberately unmapped, so AddRange above does not reach the edges; without this, every edge written
        // by a caller that persists through here (Metaverse Object Housekeeping, which is where grace-period
        // deletions and their cascades happen) would be dropped with nothing failing. Ids are resolved here for
        // the same reason the bulk path resolves them at flush time: neither the item nor the outcome has one
        // until it is persisted.
        var edges = new List<CausalEdge>();
        foreach (var item in items.Where(i => i.CausalEdges.Count > 0))
        {
            foreach (var edge in item.CausalEdges)
                edge.ResolveTransientReferences(item.Id);

            edges.AddRange(item.CausalEdges);
        }

        if (edges.Count == 0)
            return;

        Repository.Database.CausalEdges.AddRange(edges);
        await Repository.Database.SaveChangesAsync();

        // Emptied once written, matching the bulk path: an item re-persisted later must not duplicate them.
        foreach (var item in items.Where(i => i.CausalEdges.Count > 0))
            item.CausalEdges.Clear();
    }

    public async Task UpdateActivityAsync(Activity activity)
    {
        // Use detach-safe update to avoid graph traversal on detached Activity entities.
        // After ClearChangeTracker(), Update() would traverse RPEIs → CSO → MVO → Type → Attributes
        // causing identity conflicts with shared MetaverseAttribute/MetaverseObjectType instances.
        Repository.UpdateDetachedSafe(activity);
        await Repository.Database.SaveChangesAsync();
    }

    /// <summary>
    /// Atomically increments AttemptCount and advances LastSeen on the aggregated failed-authentication Activity row
    /// matching the given window bucket. On relational providers this issues a single atomic SQL UPDATE
    /// (<c>ExecuteUpdateAsync</c>), making concurrent increments for the same window race-safe. The in-memory test
    /// provider does not support <c>ExecuteUpdateAsync</c> (see the identical pattern in
    /// <c>SyncRepository.CsOperations.cs</c>), so it falls back to a tracked load/update/save.
    /// </summary>
    public async Task<bool> IncrementAggregatedFailedAuthenticationAsync(string apiKeyPrefix, string clientIp, string reason, DateTime windowStart, DateTime lastSeen)
    {
        var query = Repository.Database.Activities
            .Where(a => a.TargetType == ActivityTargetType.Authentication
                && a.ApiKeyPrefix == apiKeyPrefix
                && a.ClientIpAddress == clientIp
                && a.SecurityEventReason == reason
                && a.AggregationWindowStart == windowStart);

        if (Repository.Database.Database.IsRelational())
        {
            var rowsAffected = await query.ExecuteUpdateAsync(setters => setters
                .SetProperty(a => a.AttemptCount, a => (a.AttemptCount ?? 0) + 1)
                .SetProperty(a => a.LastSeen, lastSeen));
            return rowsAffected > 0;
        }

        // InMemory provider (tests): ExecuteUpdateAsync not supported.
        var existing = await query.AsTracking().SingleOrDefaultAsync();
        if (existing == null)
            return false;

        existing.AttemptCount = (existing.AttemptCount ?? 0) + 1;
        existing.LastSeen = lastSeen;
        await Repository.Database.SaveChangesAsync();
        return true;
    }

    public async Task<(int TotalWithErrors, int TotalRpeis, int TotalUnhandledErrors)> GetActivityRpeiErrorCountsAsync(Guid activityId)
    {
        var counts = await Repository.Database.ActivityRunProfileExecutionItems
            .Where(r => r.Activity.Id == activityId)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalRpeis = g.Count(),
                TotalWithErrors = g.Count(r => r.ErrorType != null && r.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet),
                TotalUnhandledErrors = g.Count(r => r.ErrorType == ActivityRunProfileExecutionItemErrorType.UnhandledError)
            })
            .FirstOrDefaultAsync();

        return counts != null
            ? (counts.TotalWithErrors, counts.TotalRpeis, counts.TotalUnhandledErrors)
            : (0, 0, 0);
    }

    public async Task DeleteActivityAsync(Activity activity)
    {
        Repository.Database.Activities.Remove(activity);
        await Repository.Database.SaveChangesAsync();
    }

    /// <summary>
    /// Retrieves a page's worth of top-level activities, i.e. those that do not have a parent activity.
    /// </summary>
    public async Task<PagedResultSet<Activity>> GetActivitiesAsync(
        int page,
        int pageSize,
        string? searchQuery = null,
        string? sortBy = null,
        bool sortDescending = true,
        Guid? initiatedById = null,
        IEnumerable<ActivityTargetOperationType>? operationFilter = null,
        IEnumerable<ActivityOutcomeType>? outcomeFilter = null,
        IEnumerable<ActivityTargetType>? typeFilter = null,
        IEnumerable<ActivityStatus>? statusFilter = null,
        bool? hasChildActivities = null,
        IEnumerable<ActivityInitiatorType>? initiatorTypeFilter = null,
        DateTime? createdFrom = null,
        DateTime? createdTo = null,
        IEnumerable<string>? connectedSystemFilter = null,
        IEnumerable<string>? runProfileFilter = null,
        string? initiatedByFilter = null,
        bool? initiatedBySchedule = null,
        IEnumerable<Guid>? scheduleFilter = null)
    {
        if (pageSize < 1)
            throw new ArgumentOutOfRangeException(nameof(pageSize), "pageSize must be a positive number");

        if (page < 1)
            page = 1;

        // limit page size to avoid increasing latency unnecessarily
        if (pageSize > 100)
            pageSize = 100;

        var query = BuildTopLevelActivitiesQuery(
            searchQuery, sortBy, sortDescending, initiatedById, operationFilter, outcomeFilter, typeFilter,
            statusFilter, hasChildActivities, initiatorTypeFilter, createdFrom, createdTo,
            connectedSystemFilter, runProfileFilter, initiatedByFilter, initiatedBySchedule, scheduleFilter);

        // Get total count for pagination
        var grossCount = await query.CountAsync();
        var offset = (page - 1) * pageSize;
        var results = await query.Skip(offset).Take(pageSize).ToListAsync();

        var pagedResultSet = new PagedResultSet<Activity>
        {
            PageSize = pageSize,
            TotalResults = grossCount,
            CurrentPage = page,
            Results = results
        };

        if (page == 1 && pagedResultSet.TotalPages == 0)
            return pagedResultSet;

        // don't let users try and request a page that doesn't exist
        if (page <= pagedResultSet.TotalPages)
            return pagedResultSet;

        pagedResultSet.TotalResults = 0;
        pagedResultSet.Results.Clear();
        return pagedResultSet;
    }

    /// <summary>
    /// The largest window any of this repository's range reads will return, bounding the latency of a single
    /// read. It is deliberately five times the paged readers' page-size cap, because the two caps protect against
    /// different things: a page size is a number a person picked from a fixed list and never approaches 100,
    /// whereas a virtualiser asks for however many rows the viewport needs, and a cap it can actually reach
    /// truncates the window silently, rendering the shortfall as blank rows rather than raising anything.
    /// 500 puts it out of a viewport's reach; the derivation from the list grid's height and row-height
    /// arithmetic lives on <c>MetaverseRepository.MaxHeaderWindowSize</c>, which this cap mirrors.
    /// </summary>
    private const int MaxActivityWindowSize = 500;

    /// <inheritdoc/>
    public async Task<RangeResultSet<Activity>> GetActivitiesRangeAsync(
        int startIndex,
        int count,
        string? searchQuery = null,
        string? sortBy = null,
        bool sortDescending = true,
        Guid? initiatedById = null,
        IEnumerable<ActivityTargetOperationType>? operationFilter = null,
        IEnumerable<ActivityOutcomeType>? outcomeFilter = null,
        IEnumerable<ActivityTargetType>? typeFilter = null,
        IEnumerable<ActivityStatus>? statusFilter = null,
        bool? hasChildActivities = null,
        IEnumerable<ActivityInitiatorType>? initiatorTypeFilter = null,
        DateTime? createdFrom = null,
        DateTime? createdTo = null,
        IEnumerable<string>? connectedSystemFilter = null,
        IEnumerable<string>? runProfileFilter = null,
        string? initiatedByFilter = null,
        bool? initiatedBySchedule = null,
        IEnumerable<Guid>? scheduleFilter = null,
        bool includeTotalCount = true)
    {
        if (count < 1)
            throw new ArgumentOutOfRangeException(nameof(count), "count must be a positive number");

        if (startIndex < 0)
            startIndex = 0;

        if (count > MaxActivityWindowSize)
            count = MaxActivityWindowSize;

        var query = BuildTopLevelActivitiesQuery(
            searchQuery, sortBy, sortDescending, initiatedById, operationFilter, outcomeFilter, typeFilter,
            statusFilter, hasChildActivities, initiatorTypeFilter, createdFrom, createdTo,
            connectedSystemFilter, runProfileFilter, initiatedByFilter, initiatedBySchedule, scheduleFilter);

        // Counting scans every matching Activity rather than a window of them, so it is the expensive half of
        // this method at scale and is skipped entirely when the caller already holds the total. Sorting cannot
        // change how many Activities match, so a caller only has to re-count when the filters change.
        int? grossCount = null;
        if (includeTotalCount)
            grossCount = await query.CountAsync();

        var results = await query.Skip(startIndex).Take(count).ToListAsync();

        return new RangeResultSet<Activity>
        {
            Results = results,
            TotalResults = grossCount
        };
    }

    /// <summary>
    /// Shared query core for the paged and range Activity list reads: applies every filter and the sort to the
    /// top-level (parentless) Activities and returns the composed query for the caller to count and window.
    /// Shared so the two reads can never disagree on which Activities match; callers own input validation.
    /// </summary>
    private IQueryable<Activity> BuildTopLevelActivitiesQuery(
        string? searchQuery,
        string? sortBy,
        bool sortDescending,
        Guid? initiatedById,
        IEnumerable<ActivityTargetOperationType>? operationFilter,
        IEnumerable<ActivityOutcomeType>? outcomeFilter,
        IEnumerable<ActivityTargetType>? typeFilter,
        IEnumerable<ActivityStatus>? statusFilter,
        bool? hasChildActivities,
        IEnumerable<ActivityInitiatorType>? initiatorTypeFilter,
        DateTime? createdFrom,
        DateTime? createdTo,
        IEnumerable<string>? connectedSystemFilter,
        IEnumerable<string>? runProfileFilter,
        string? initiatedByFilter,
        bool? initiatedBySchedule,
        IEnumerable<Guid>? scheduleFilter)
    {
        var query = Repository.Database.Activities

            .Where(a => a.ParentActivityId == null)
            .AsQueryable();

        // Apply initiated by filter (searches by InitiatedById which covers both MVO and ApiKey)
        if (initiatedById.HasValue)
        {
            query = query.Where(a => a.InitiatedById == initiatedById.Value);
        }

        // Apply operation filter
        if (operationFilter != null)
        {
            var operations = operationFilter.ToList();
            if (operations.Count > 0)
                query = query.Where(a => operations.Contains(a.TargetOperationType));
        }

        // Apply type filter
        if (typeFilter != null)
        {
            var types = typeFilter.ToList();
            if (types.Count > 0)
                query = query.Where(a => types.Contains(a.TargetType));
        }

        // Apply status filter
        if (statusFilter != null)
        {
            var statuses = statusFilter.ToList();
            if (statuses.Count > 0)
                query = query.Where(a => statuses.Contains(a.Status));
        }

        // Apply initiator-type filter (user / API key / system)
        if (initiatorTypeFilter != null)
        {
            var initiatorTypes = initiatorTypeFilter.ToList();
            if (initiatorTypes.Count > 0)
                query = query.Where(a => initiatorTypes.Contains(a.InitiatedByType));
        }

        // Apply the Connected System filter, which matches the Activity's Target Context (the system the
        // operation was performed against).
        var connectedSystems = connectedSystemFilter?.ToList();
        if (connectedSystems is { Count: > 0 })
            query = query.Where(a => a.TargetContext != null && connectedSystems.Contains(a.TargetContext));

        // Apply the Run Profile filter, which matches the Activity's Target Name.
        var runProfiles = runProfileFilter?.ToList();
        if (runProfiles is { Count: > 0 })
            query = query.Where(a => a.TargetName != null && runProfiles.Contains(a.TargetName));

        // Apply the initiator-name filter. Distinct from initiatedById above: this is a case-insensitive
        // partial match on the recorded name, for callers who know who they are looking for but not their id.
        if (!string.IsNullOrWhiteSpace(initiatedByFilter))
        {
            var filterLower = initiatedByFilter.ToLower();
            query = query.Where(a => a.InitiatedByName != null && a.InitiatedByName.ToLower().Contains(filterLower));
        }

        // Apply the Schedule attribution filters. The attribution is denormalised onto the Activity, so both of
        // these are plain indexed predicates rather than a join through Schedule Executions.
        if (initiatedBySchedule == true)
        {
            query = query.Where(a => a.ScheduledByScheduleId != null);
        }
        else if (initiatedBySchedule == false)
        {
            query = query.Where(a => a.ScheduledByScheduleId == null);
        }

        var scheduleIds = scheduleFilter?.ToList();
        if (scheduleIds is { Count: > 0 })
            query = query.Where(a => a.ScheduledByScheduleId != null && scheduleIds.Contains(a.ScheduledByScheduleId!.Value));

        // Apply date-range filter (either bound may be open). Captured into non-nullable locals so the query
        // expressions carry plain DateTime values.
        if (createdFrom.HasValue)
        {
            var from = createdFrom.Value;
            query = query.Where(a => a.Created >= from);
        }
        if (createdTo.HasValue)
        {
            var to = createdTo.Value;
            query = query.Where(a => a.Created <= to);
        }

        // Apply outcome filter (activities that have > 0 for any of the selected outcome stat types)
        if (outcomeFilter != null)
        {
            var outcomes = outcomeFilter.ToList();
            if (outcomes.Count > 0)
            {
                query = query.Where(a =>
                    (outcomes.Contains(ActivityOutcomeType.Added) && a.TotalAdded > 0) ||
                    (outcomes.Contains(ActivityOutcomeType.Updated) && a.TotalUpdated > 0) ||
                    (outcomes.Contains(ActivityOutcomeType.Deleted) && a.TotalDeleted > 0) ||
                    (outcomes.Contains(ActivityOutcomeType.Projected) && a.TotalProjected > 0) ||
                    (outcomes.Contains(ActivityOutcomeType.Joined) && a.TotalJoined > 0) ||
                    (outcomes.Contains(ActivityOutcomeType.AttributeFlows) && a.TotalAttributeFlows > 0) ||
                    (outcomes.Contains(ActivityOutcomeType.Disconnected) && a.TotalDisconnected > 0) ||
                    (outcomes.Contains(ActivityOutcomeType.DriftCorrections) && a.TotalDriftCorrections > 0) ||
                    (outcomes.Contains(ActivityOutcomeType.Provisioned) && a.TotalProvisioned > 0) ||
                    (outcomes.Contains(ActivityOutcomeType.Exported) && a.TotalExported > 0) ||
                    (outcomes.Contains(ActivityOutcomeType.Deprovisioned) && a.TotalDeprovisioned > 0) ||
                    (outcomes.Contains(ActivityOutcomeType.Created) && a.TotalCreated > 0) ||
                    (outcomes.Contains(ActivityOutcomeType.PendingExports) && a.TotalPendingExports > 0) ||
                    (outcomes.Contains(ActivityOutcomeType.Errors) && a.TotalErrors > 0));
            }
        }

        // Apply search filter (target name, context, and initiated by name)
        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            var searchLower = searchQuery.ToLower();
            query = query.Where(a =>
                (a.TargetName != null && a.TargetName.ToLower().Contains(searchLower)) ||
                (a.TargetContext != null && a.TargetContext.ToLower().Contains(searchLower)) ||
                (a.InitiatedByName != null && a.InitiatedByName.ToLower().Contains(searchLower)));
        }

        // Apply child activities filter
        if (hasChildActivities == true)
        {
            query = query.Where(a => Repository.Database.Activities.Any(c => c.ParentActivityId == a.Id));
        }
        else if (hasChildActivities == false)
        {
            query = query.Where(a => !Repository.Database.Activities.Any(c => c.ParentActivityId == a.Id));
        }

        // Apply sorting
        var ordered = sortBy?.ToLower() switch
        {
            "targetcontext" or "connectedsystem" => sortDescending
                ? query.OrderByDescending(a => a.TargetContext)
                : query.OrderBy(a => a.TargetContext),
            "targettype" or "type" => sortDescending
                ? query.OrderByDescending(a => a.TargetType)
                : query.OrderBy(a => a.TargetType),
            "targetname" or "target" => sortDescending
                ? query.OrderByDescending(a => a.TargetName)
                : query.OrderBy(a => a.TargetName),
            "targetoperationtype" or "operation" => sortDescending
                ? query.OrderByDescending(a => a.TargetOperationType)
                : query.OrderBy(a => a.TargetOperationType),
            "initiatedbyname" or "initiatedby" => sortDescending
                ? query.OrderByDescending(a => a.InitiatedByName)
                : query.OrderBy(a => a.InitiatedByName),
            "status" => sortDescending
                ? query.OrderByDescending(a => a.Status)
                : query.OrderBy(a => a.Status),
            "executiontime" => sortDescending
                ? query.OrderByDescending(a => a.ExecutionTime)
                : query.OrderBy(a => a.ExecutionTime),
            _ => sortDescending
                ? query.OrderByDescending(a => a.Created)
                : query.OrderBy(a => a.Created) // Default: sort by Created
        };

        // Deterministic tie-break: Skip/Take windows are only stable under a total order, and every sort key
        // above can tie (a Schedule that fans several Run Profiles out at once stamps them all with the same
        // created time). Without it, PostgreSQL may order tied rows differently per window, repeating some
        // Activities and skipping others as the reader scrolls.
        return ordered.ThenBy(a => a.Id);
    }

    public async Task<Activity?> GetActivityAsync(Guid id)
    {
        return await Repository.Database.Activities
            .SingleOrDefaultAsync(a => a.Id == id);
    }

    /// <inheritdoc />
    public async Task<Activity?> GetLatestCompletedExportActivityAsync(int connectedSystemId)
    {
        return await Repository.Database.Activities
            .AsNoTracking()
            .Where(a => a.ConnectedSystemId == connectedSystemId
                        && a.TargetType == ActivityTargetType.ConnectedSystemRunProfile
                        && a.ConnectedSystemRunType == ConnectedSystemRunType.Export
                        && a.Status != ActivityStatus.InProgress)
            .OrderByDescending(a => a.Created)
            .ThenByDescending(a => a.Id)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Gets a page's worth of direct child activities for a given parent activity ID,
    /// ordered by creation date ascending.
    /// </summary>
    /// <inheritdoc />
    public async Task<List<PasswordSynchronisationEvent>> GetPasswordSynchronisationEventsAsync(Guid metaverseObjectId, int maximumEvents)
    {
        if (maximumEvents < 1)
            return [];

        // Two reads rather than an Include, so the limit applies to changes rather than to rows: a single
        // Include over parents and children, taken to a row count, can cut a change off from half its outcomes
        // and show an identity a success while hiding the refusal that followed it.
        var changes = await Repository.Database.Activities
            .AsNoTracking()
            .Where(a => a.MetaverseObjectId == metaverseObjectId
                        && a.TargetType == ActivityTargetType.PasswordSynchronisation
                        && a.ParentActivityId == null)
            .OrderByDescending(a => a.Created)
            .Take(maximumEvents)
            .Select(a => new PasswordSynchronisationEvent
            {
                ActivityId = a.Id,
                Created = a.Created,
                InitiatedByName = a.InitiatedByName,
                InitiatedByType = a.InitiatedByType,
                Message = a.Message,
                // The origin is the enum's name in TargetContext (#1635), written by SetPasswordAsync. Spelt out as
                // string comparisons rather than Enum.Parse so it translates to a CASE in the database, and so an
                // Activity from before origins were recorded (null, or anything else) projects to null.
                Origin = a.TargetContext == nameof(PendingPasswordChangeOrigin.Explicit) ? PendingPasswordChangeOrigin.Explicit
                    : a.TargetContext == nameof(PendingPasswordChangeOrigin.Propagated) ? PendingPasswordChangeOrigin.Propagated
                    : (PendingPasswordChangeOrigin?)null
            })
            .ToListAsync();

        if (changes.Count == 0)
            return changes;

        var changeIds = changes.Select(c => c.ActivityId).ToList();

        var outcomes = await Repository.Database.Activities
            .AsNoTracking()
            .Where(a => a.ParentActivityId != null && changeIds.Contains(a.ParentActivityId.Value))
            .OrderBy(a => a.Created)
            .Select(a => new
            {
                ParentActivityId = a.ParentActivityId!.Value,
                // The same fields as OutcomeProjection below; an expression cannot be invoked inside another
                // projection, so the shape is repeated here and the per-change read keeps the shared one.
                Outcome = new PasswordSynchronisationEventOutcome
                {
                    ActivityId = a.Id,
                    ConnectedSystemId = a.ConnectedSystemId,
                    ConnectedSystemName = a.TargetName ?? string.Empty,
                    Status = a.Status,
                    ErrorMessage = a.ErrorMessage,
                    Message = a.Message,
                    OccurredAt = a.Created
                }
            })
            .ToListAsync();

        var byChange = outcomes
            .GroupBy(o => o.ParentActivityId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<PasswordSynchronisationEventOutcome>)g.Select(o => o.Outcome).ToList());

        foreach (var change in changes)
        {
            if (byChange.TryGetValue(change.ActivityId, out var changeOutcomes))
                change.Outcomes = changeOutcomes;
        }

        return changes;
    }

    /// <inheritdoc />
    public async Task<List<PasswordSynchronisationEventOutcome>> GetPasswordSynchronisationOutcomesAsync(Guid changeActivityId)
    {
        return await Repository.Database.Activities
            .AsNoTracking()
            .Where(a => a.ParentActivityId == changeActivityId)
            .OrderBy(a => a.Created)
            .ThenBy(a => a.Id)
            .Select(OutcomeProjection)
            .ToListAsync();
    }

    /// <summary>
    /// One delivery outcome Activity in the shape the password surfaces read. An expression tree over the entity
    /// so EF Core projects it in the database rather than materialising the Activity; it must stay field-for-field
    /// identical to the inline projection in <see cref="GetPasswordSynchronisationEventsAsync"/>.
    /// </summary>
    private static readonly System.Linq.Expressions.Expression<Func<Activity, PasswordSynchronisationEventOutcome>> OutcomeProjection = a =>
        new PasswordSynchronisationEventOutcome
        {
            ActivityId = a.Id,
            ConnectedSystemId = a.ConnectedSystemId,
            ConnectedSystemName = a.TargetName ?? string.Empty,
            Status = a.Status,
            ErrorMessage = a.ErrorMessage,
            Message = a.Message,
            OccurredAt = a.Created
        };

    public async Task<PagedResultSet<Activity>> GetChildActivitiesAsync(Guid parentActivityId, int page, int pageSize)
    {
        if (pageSize < 1)
            throw new ArgumentOutOfRangeException(nameof(pageSize), "pageSize must be a positive number");

        if (page < 1)
            page = 1;

        // limit page size to avoid increasing latency unnecessarily
        if (pageSize > 100)
            pageSize = 100;

        var offset = (page - 1) * pageSize;
        var (results, grossCount) = await QueryChildActivitiesByRangeAsync(
            parentActivityId, offset, pageSize, searchQuery: null, includeTotalCount: true);

        var pagedResultSet = new PagedResultSet<Activity>
        {
            PageSize = pageSize,
            // The count was requested above, so it is always present here; paging cannot work without it.
            TotalResults = grossCount ?? throw new InvalidOperationException(
                "The paged child Activity read asked for the total match count and did not receive one."),
            CurrentPage = page,
            Results = results
        };

        if (page == 1 && pagedResultSet.TotalPages == 0)
            return pagedResultSet;

        // don't let users try and request a page that doesn't exist
        if (page <= pagedResultSet.TotalPages)
            return pagedResultSet;

        pagedResultSet.TotalResults = 0;
        pagedResultSet.Results.Clear();
        return pagedResultSet;
    }

    /// <inheritdoc />
    public async Task<RangeResultSet<Activity>> GetChildActivitiesRangeAsync(
        Guid parentActivityId,
        int offset,
        int count,
        string? searchQuery = null,
        bool includeTotalCount = true)
    {
        if (count < 1)
            throw new ArgumentOutOfRangeException(nameof(count), "count must be a positive number");

        if (offset < 0)
            offset = 0;

        if (count > MaxActivityWindowSize)
            count = MaxActivityWindowSize;

        var (results, grossCount) = await QueryChildActivitiesByRangeAsync(
            parentActivityId, offset, count, searchQuery, includeTotalCount);

        return new RangeResultSet<Activity>
        {
            Results = results,
            TotalResults = grossCount
        };
    }

    /// <summary>
    /// Shared core for the paged and range child Activity reads: the direct children of one Activity,
    /// oldest first, windowed by absolute <paramref name="offset"/> and <paramref name="count"/>, alongside the
    /// total child count (or null for that total when <paramref name="includeTotalCount"/> is false). Shared so
    /// the two reads can never disagree on which Activities are children; callers own input validation.
    /// </summary>
    private async Task<(List<Activity> Results, int? TotalResults)> QueryChildActivitiesByRangeAsync(
        Guid parentActivityId,
        int offset,
        int count,
        string? searchQuery,
        bool includeTotalCount)
    {
        var children = Repository.Database.Activities
            .Where(a => a.ParentActivityId == parentActivityId);

        // Case-insensitive search over what the table shows for a child: what it acted on, and what it said.
        // ToLower/Contains rather than ILike so the shared core stays executable on the in-memory provider the
        // unit tier uses.
        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            var searchLower = searchQuery.ToLower();
            children = children.Where(a =>
                (a.TargetName != null && a.TargetName.ToLower().Contains(searchLower)) ||
                (a.TargetContext != null && a.TargetContext.ToLower().Contains(searchLower)) ||
                (a.Message != null && a.Message.ToLower().Contains(searchLower)));
        }

        var query = children
            .OrderBy(a => a.Created)
            // Deterministic tie-break: Skip/Take windows are only stable under a total order, and a run that
            // spawns its children in one batch gives them all the same created time. Without it, PostgreSQL may
            // order tied rows differently per window, repeating some children and skipping others.
            .ThenBy(a => a.Id);

        // Counting scans every child rather than a window of them, so it is skipped entirely when the caller
        // already holds the total. Ordering cannot change how many children there are.
        int? grossCount = null;
        if (includeTotalCount)
            grossCount = await query.CountAsync();

        var results = await query.Skip(offset).Take(count).ToListAsync();
        return (results, grossCount);
    }

    public async Task<Dictionary<Guid, int>> GetChildActivityCountsAsync(IEnumerable<Guid> activityIds)
    {
        var ids = activityIds.ToList();
        return await Repository.Database.Activities

            .Where(a => a.ParentActivityId != null && ids.Contains(a.ParentActivityId.Value))
            .GroupBy(a => a.ParentActivityId!.Value)
            .Select(g => new { ParentId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ParentId, x => x.Count);
    }

    public async Task<ActivityFilterOptions> GetWorkerTaskActivityFilterOptionsAsync()
    {
        var query = BuildWorkerTaskQuery();

        var connectedSystems = await query
            .Where(a => a.TargetContext != null)
            .Select(a => a.TargetContext!)
            .Distinct()
            .OrderBy(name => name)
            .ToListAsync();

        var runProfiles = await query
            .Where(a => a.TargetName != null)
            .Select(a => a.TargetName!)
            .Distinct()
            .OrderBy(name => name)
            .ToListAsync();

        // One option per Schedule id, carrying the most recently recorded name: a Schedule renamed part-way through
        // its history would otherwise yield two options sharing an id, which a MudSelect cannot represent.
        var schedules = await query
            .Where(a => a.ScheduledByScheduleId != null && a.ScheduledByScheduleName != null)
            .GroupBy(a => a.ScheduledByScheduleId!.Value)
            .Select(g => new ScheduleFilterOption
            {
                Id = g.Key,
                Name = g.OrderByDescending(a => a.Created).Select(a => a.ScheduledByScheduleName!).First()
            })
            .OrderBy(s => s.Name)
            .ToListAsync();

        return new ActivityFilterOptions
        {
            ConnectedSystems = connectedSystems,
            RunProfiles = runProfiles,
            Schedules = schedules
        };
    }

    /// <summary>
    /// Builds the base query for worker task activities, filtering to parent activities
    /// with worker task target types and operation types.
    ///
    /// Only <see cref="GetWorkerTaskActivityFilterOptionsAsync"/> uses this now: the Worker Task Activity
    /// *listing* is served by <see cref="GetActivitiesAsync"/>, to which Operations &gt; History passes the
    /// same target types and operations as its typeFilter and operationFilter. Keep the two in step; the
    /// drop-downs must offer exactly the values the list can return.
    /// </summary>
    private IQueryable<Activity> BuildWorkerTaskQuery()
    {
        // Worker task activity types - Run Profile executions and Connected System operations
        // Note: ExampleDataTemplate and HistoryRetentionCleanup are intentionally excluded
        var workerTaskTargetTypes = new[]
        {
            ActivityTargetType.ConnectedSystemRunProfile,
            ActivityTargetType.ConnectedSystem
        };

        // Worker task operations
        var workerTaskOperations = new[]
        {
            ActivityTargetOperationType.Execute,
            ActivityTargetOperationType.Clear,
            ActivityTargetOperationType.Delete
        };

        return Repository.Database.Activities

            .Where(a => a.ParentActivityId == null)
            .Where(a => workerTaskTargetTypes.Contains(a.TargetType) && workerTaskOperations.Contains(a.TargetOperationType))
            .AsQueryable();
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Schedule execution queries
    // -----------------------------------------------------------------------------------------------------------------

    public async Task<List<Activity>> GetActivitiesByScheduleExecutionAsync(Guid scheduleExecutionId)
    {
        return await Repository.Database.Activities

            .Where(a => a.ScheduleExecutionId == scheduleExecutionId)
            .OrderBy(a => a.ScheduleStepIndex)
            .ThenBy(a => a.Created)
            .ToListAsync();
    }

    public async Task<Dictionary<Guid, List<ScheduleStepObservation>>> GetScheduleStepOutcomesAsync(IReadOnlyCollection<Guid> scheduleExecutionIds)
    {
        if (scheduleExecutionIds.Count == 0)
            return [];

        var rows = await Repository.Database.Activities
            .AsNoTracking()
            .Where(a => a.ScheduleExecutionId.HasValue &&
                        scheduleExecutionIds.Contains(a.ScheduleExecutionId.Value) &&
                        a.ScheduleStepIndex.HasValue)
            .OrderBy(a => a.ScheduleStepIndex)
            .ThenBy(a => a.Created)
            .Select(a => new
            {
                ScheduleExecutionId = a.ScheduleExecutionId!.Value,
                StepIndex = a.ScheduleStepIndex!.Value,
                ActivityId = a.Id,
                a.TargetContext,
                a.TargetName,
                a.Status
            })
            .ToListAsync();

        return rows
            .GroupBy(r => r.ScheduleExecutionId)
            .ToDictionary(g => g.Key, g => g.Select(r => new ScheduleStepObservation
            {
                StepIndex = r.StepIndex,
                Name = ScheduleStepReading.NameOf(r.TargetContext, r.TargetName, r.StepIndex),
                ActivityId = r.ActivityId,
                ActivityStatus = r.Status
            }).ToList());
    }

    public async Task<List<Activity>> GetActivitiesByScheduleExecutionStepAsync(Guid scheduleExecutionId, int stepIndex)
    {
        return await Repository.Database.Activities

            .Where(a => a.ScheduleExecutionId == scheduleExecutionId && a.ScheduleStepIndex == stepIndex)
            .OrderBy(a => a.Created)
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<bool> HasAnyRunProfileExecutionAsync()
    {
        // TargetOperationType must be Execute: creating, editing and deleting a Run Profile all record
        // ConnectedSystemRunProfile-typed Activities too, and without this filter merely defining a Run Profile
        // would read as having run one. Status is deliberately unfiltered; a run that is in progress or that
        // failed was still run, which is all this question asks.
        return await Repository.Database.Activities
            .AnyAsync(a => a.TargetType == ActivityTargetType.ConnectedSystemRunProfile
                           && a.TargetOperationType == ActivityTargetOperationType.Execute);
    }

    private IQueryable<Activity> ConfigurationChangeQuery(ActivityTargetType targetType, int targetObjectId)
    {
        // Membership of an object's configuration history is determined by "carries a captured snapshot version AND
        // belongs to this object (by FK)", NOT by the activity's own TargetType. A configuration object is changed both
        // by whole-object saves (TargetType SyncRule / ConnectedSystem) and by granular sub-entity endpoints whose
        // activities are typed for the child (ConnectedSystemRunProfile, ObjectMatchingRule, etc.). Keying on the FK +
        // version surfaces every versioned change regardless of which endpoint recorded it; only capture sets a version,
        // so non-configuration activities (which never carry one) are naturally excluded.
        var query = Repository.Database.Activities
            .Where(a => a.ConfigurationChangeVersion != null);

        return targetType switch
        {
            ActivityTargetType.ConnectedSystem => query.Where(a => a.ConnectedSystemId == targetObjectId),
            ActivityTargetType.SynchronisationRule => query.Where(a => a.SyncRuleId == targetObjectId),
            ActivityTargetType.MetaverseAttribute => query.Where(a => a.MetaverseAttributeId == targetObjectId),
            ActivityTargetType.MetaverseObjectType => query.Where(a => a.MetaverseObjectTypeId == targetObjectId),
            ActivityTargetType.PredefinedSearch => query.Where(a => a.PredefinedSearchId == targetObjectId),
            ActivityTargetType.Role => query.Where(a => a.RoleId == targetObjectId),
            ActivityTargetType.ConnectorDefinition => query.Where(a => a.ConnectorDefinitionId == targetObjectId),
            ActivityTargetType.ExampleDataTemplate => query.Where(a => a.ExampleDataTemplateId == targetObjectId),
            ActivityTargetType.ExampleDataSet => query.Where(a => a.ExampleDataSetId == targetObjectId),
            _ => throw new ArgumentOutOfRangeException(nameof(targetType), targetType,
                "Unsupported configuration target type for change history.")
        };
    }

    // Guid-keyed counterpart of ConfigurationChangeQuery, for configuration objects (e.g. a Schedule) whose id is a Guid
    // and which associate their change activities via a dedicated Guid foreign key rather than an integer one.
    private IQueryable<Activity> ConfigurationChangeQuery(ActivityTargetType targetType, Guid targetObjectId)
    {
        var query = Repository.Database.Activities
            .Where(a => a.ConfigurationChangeVersion != null);

        return targetType switch
        {
            ActivityTargetType.Schedule => query.Where(a => a.ScheduleId == targetObjectId),
            ActivityTargetType.TrustedCertificate => query.Where(a => a.TrustedCertificateId == targetObjectId),
            ActivityTargetType.ApiKey => query.Where(a => a.ApiKeyId == targetObjectId),
            _ => throw new ArgumentOutOfRangeException(nameof(targetType), targetType,
                "Unsupported Guid-keyed configuration target type for change history.")
        };
    }

    // String-keyed counterpart of ConfigurationChangeQuery, for configuration objects whose primary key is a string
    // (Service Settings, keyed by their setting key).
    private IQueryable<Activity> ConfigurationChangeQuery(ActivityTargetType targetType, string targetObjectKey)
    {
        var query = Repository.Database.Activities
            .Where(a => a.ConfigurationChangeVersion != null);

        return targetType switch
        {
            ActivityTargetType.ServiceSetting => query.Where(a => a.ServiceSettingKey == targetObjectKey),
            _ => throw new ArgumentOutOfRangeException(nameof(targetType), targetType,
                "Unsupported string-keyed configuration target type for change history.")
        };
    }

    private static readonly Expression<Func<Activity, ConfigurationChangeActivityData>> ToConfigurationChangeData = a => new ConfigurationChangeActivityData
    {
        ActivityId = a.Id,
        Version = a.ConfigurationChangeVersion ?? 0,
        Operation = a.TargetOperationType,
        InitiatedByType = a.InitiatedByType,
        InitiatedById = a.InitiatedById,
        InitiatedByName = a.InitiatedByName,
        When = a.Created,
        Reason = a.ChangeReason,
        SnapshotJson = a.ConfigurationChangeSnapshot
    };

    public async Task<int> GetMaxConfigurationChangeVersionAsync(ActivityTargetType targetType, int targetObjectId)
    {
        var max = await ConfigurationChangeQuery(targetType, targetObjectId).MaxAsync(a => (int?)a.ConfigurationChangeVersion);
        return max ?? 0;
    }

    public async Task<int> GetMaxConfigurationChangeVersionAsync(ActivityTargetType targetType, Guid targetObjectId)
    {
        var max = await ConfigurationChangeQuery(targetType, targetObjectId).MaxAsync(a => (int?)a.ConfigurationChangeVersion);
        return max ?? 0;
    }

    public async Task<string?> GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType targetType, int targetObjectId)
    {
        return await ConfigurationChangeQuery(targetType, targetObjectId)
            .OrderByDescending(a => a.ConfigurationChangeVersion)
            .Select(a => a.ConfigurationChangeSnapshot)
            .FirstOrDefaultAsync();
    }

    public async Task<string?> GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType targetType, Guid targetObjectId)
    {
        return await ConfigurationChangeQuery(targetType, targetObjectId)
            .OrderByDescending(a => a.ConfigurationChangeVersion)
            .Select(a => a.ConfigurationChangeSnapshot)
            .FirstOrDefaultAsync();
    }

    public async Task<int> GetConfigurationChangeCountAsync(ActivityTargetType targetType, int targetObjectId)
    {
        return await ConfigurationChangeQuery(targetType, targetObjectId).CountAsync();
    }

    public async Task<int> GetConfigurationChangeCountAsync(ActivityTargetType targetType, Guid targetObjectId)
    {
        return await ConfigurationChangeQuery(targetType, targetObjectId).CountAsync();
    }

    public async Task<List<ConfigurationChangeActivityData>> GetConfigurationChangeActivitiesAsync(ActivityTargetType targetType, int targetObjectId, int skip, int take)
    {
        return await ConfigurationChangeQuery(targetType, targetObjectId)
            .OrderByDescending(a => a.ConfigurationChangeVersion)
            .Skip(skip)
            .Take(take)
            .Select(ToConfigurationChangeData)
            .ToListAsync();
    }

    public async Task<List<ConfigurationChangeActivityData>> GetConfigurationChangeActivitiesAsync(ActivityTargetType targetType, Guid targetObjectId, int skip, int take)
    {
        return await ConfigurationChangeQuery(targetType, targetObjectId)
            .OrderByDescending(a => a.ConfigurationChangeVersion)
            .Skip(skip)
            .Take(take)
            .Select(ToConfigurationChangeData)
            .ToListAsync();
    }

    public async Task<ConfigurationChangeActivityData?> GetConfigurationChangeActivityByVersionAsync(ActivityTargetType targetType, int targetObjectId, int version)
    {
        return await ConfigurationChangeQuery(targetType, targetObjectId)
            .Where(a => a.ConfigurationChangeVersion == version)
            .Select(ToConfigurationChangeData)
            .FirstOrDefaultAsync();
    }

    public async Task<ConfigurationChangeActivityData?> GetConfigurationChangeActivityByVersionAsync(ActivityTargetType targetType, Guid targetObjectId, int version)
    {
        return await ConfigurationChangeQuery(targetType, targetObjectId)
            .Where(a => a.ConfigurationChangeVersion == version)
            .Select(ToConfigurationChangeData)
            .FirstOrDefaultAsync();
    }

    public async Task<ConfigurationChangeActivityData?> GetConfigurationChangeActivityBeforeVersionAsync(ActivityTargetType targetType, int targetObjectId, int version)
    {
        return await ConfigurationChangeQuery(targetType, targetObjectId)
            .Where(a => a.ConfigurationChangeVersion < version)
            .OrderByDescending(a => a.ConfigurationChangeVersion)
            .Select(ToConfigurationChangeData)
            .FirstOrDefaultAsync();
    }

    public async Task<ConfigurationChangeActivityData?> GetConfigurationChangeActivityBeforeVersionAsync(ActivityTargetType targetType, Guid targetObjectId, int version)
    {
        return await ConfigurationChangeQuery(targetType, targetObjectId)
            .Where(a => a.ConfigurationChangeVersion < version)
            .OrderByDescending(a => a.ConfigurationChangeVersion)
            .Select(ToConfigurationChangeData)
            .FirstOrDefaultAsync();
    }

    public async Task<int> GetMaxConfigurationChangeVersionAsync(ActivityTargetType targetType, string targetObjectKey)
    {
        var max = await ConfigurationChangeQuery(targetType, targetObjectKey).MaxAsync(a => (int?)a.ConfigurationChangeVersion);
        return max ?? 0;
    }

    public async Task<string?> GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType targetType, string targetObjectKey)
    {
        return await ConfigurationChangeQuery(targetType, targetObjectKey)
            .OrderByDescending(a => a.ConfigurationChangeVersion)
            .Select(a => a.ConfigurationChangeSnapshot)
            .FirstOrDefaultAsync();
    }

    public async Task<int> GetConfigurationChangeCountAsync(ActivityTargetType targetType, string targetObjectKey)
    {
        return await ConfigurationChangeQuery(targetType, targetObjectKey).CountAsync();
    }

    public async Task<List<ConfigurationChangeActivityData>> GetConfigurationChangeActivitiesAsync(ActivityTargetType targetType, string targetObjectKey, int skip, int take)
    {
        return await ConfigurationChangeQuery(targetType, targetObjectKey)
            .OrderByDescending(a => a.ConfigurationChangeVersion)
            .Skip(skip)
            .Take(take)
            .Select(ToConfigurationChangeData)
            .ToListAsync();
    }

    public async Task<ConfigurationChangeActivityData?> GetConfigurationChangeActivityByVersionAsync(ActivityTargetType targetType, string targetObjectKey, int version)
    {
        return await ConfigurationChangeQuery(targetType, targetObjectKey)
            .Where(a => a.ConfigurationChangeVersion == version)
            .Select(ToConfigurationChangeData)
            .FirstOrDefaultAsync();
    }

    public async Task<ConfigurationChangeActivityData?> GetConfigurationChangeActivityBeforeVersionAsync(ActivityTargetType targetType, string targetObjectKey, int version)
    {
        return await ConfigurationChangeQuery(targetType, targetObjectKey)
            .Where(a => a.ConfigurationChangeVersion < version)
            .OrderByDescending(a => a.ConfigurationChangeVersion)
            .Select(ToConfigurationChangeData)
            .FirstOrDefaultAsync();
    }

    #region synchronisation related
    public async Task<PagedResultSet<ActivityRunProfileExecutionItemHeader>> GetActivityRunProfileExecutionItemHeadersAsync(
        Guid activityId,
        int page,
        int pageSize,
        string? searchQuery = null,
        string? sortBy = null,
        bool sortDescending = false,
        IEnumerable<string>? objectTypeFilter = null,
        IEnumerable<ActivityRunProfileExecutionItemErrorType>? errorTypeFilter = null,
        IEnumerable<ActivityRunProfileExecutionItemSyncOutcomeType>? outcomeTypeFilter = null)
    {
        if (pageSize < 1)
            throw new ArgumentOutOfRangeException(nameof(pageSize), "pageSize must be a positive number");

        if (page < 1)
            page = 1;

        // limit page size to avoid increasing latency unnecessarily
        if (pageSize > 100)
            pageSize = 100;

        var offset = (page - 1) * pageSize;
        var (results, totalCount) = await QueryActivityRunProfileExecutionItemHeadersByRangeAsync(
            activityId, offset, pageSize, searchQuery, sortBy, sortDescending,
            objectTypeFilter, errorTypeFilter, outcomeTypeFilter, includeTotalCount: true);

        // Build paged result set
        var pagedResultSet = new PagedResultSet<ActivityRunProfileExecutionItemHeader>
        {
            PageSize = pageSize,
            // The count was requested above, so it is always present here; paging cannot work without it.
            TotalResults = totalCount ?? throw new InvalidOperationException(
                "The paged execution item header read asked for the total match count and did not receive one."),
            CurrentPage = page,
            Results = results
        };

        if (page == 1 && pagedResultSet.TotalPages == 0)
            return pagedResultSet;

        // don't let users try and request a page that doesn't exist
        if (page <= pagedResultSet.TotalPages)
            return pagedResultSet;

        pagedResultSet.TotalResults = 0;
        pagedResultSet.Results.Clear();
        return pagedResultSet;
    }

    /// <inheritdoc />
    public async Task<RangeResultSet<ActivityRunProfileExecutionItemHeader>> GetActivityRunProfileExecutionItemHeadersRangeAsync(
        Guid activityId,
        int offset,
        int count,
        string? searchQuery = null,
        string? sortBy = null,
        bool sortDescending = false,
        IEnumerable<string>? objectTypeFilter = null,
        IEnumerable<ActivityRunProfileExecutionItemErrorType>? errorTypeFilter = null,
        IEnumerable<ActivityRunProfileExecutionItemSyncOutcomeType>? outcomeTypeFilter = null,
        bool includeTotalCount = true)
    {
        if (count < 1)
            throw new ArgumentOutOfRangeException(nameof(count), "count must be a positive number");

        if (offset < 0)
            offset = 0;

        if (count > MaxActivityWindowSize)
            count = MaxActivityWindowSize;

        var (results, totalCount) = await QueryActivityRunProfileExecutionItemHeadersByRangeAsync(
            activityId, offset, count, searchQuery, sortBy, sortDescending,
            objectTypeFilter, errorTypeFilter, outcomeTypeFilter, includeTotalCount);

        return new RangeResultSet<ActivityRunProfileExecutionItemHeader>
        {
            Results = results,
            TotalResults = totalCount
        };
    }

    /// <summary>
    /// Shared core for the paged and range Run Profile Execution Item header reads: applies every filter and the
    /// sort to one Activity's execution items, windows them by absolute <paramref name="offset"/> and
    /// <paramref name="count"/>, and projects the window to header DTOs, alongside the total match count (or
    /// null for that total when <paramref name="includeTotalCount"/> is false). Shared so the two reads can
    /// never disagree on which items match; callers own input validation and clamping.
    /// </summary>
    private async Task<(List<ActivityRunProfileExecutionItemHeader> Results, int? TotalResults)> QueryActivityRunProfileExecutionItemHeadersByRangeAsync(
        Guid activityId,
        int offset,
        int count,
        string? searchQuery,
        string? sortBy,
        bool sortDescending,
        IEnumerable<string>? objectTypeFilter,
        IEnumerable<ActivityRunProfileExecutionItemErrorType>? errorTypeFilter,
        IEnumerable<ActivityRunProfileExecutionItemSyncOutcomeType>? outcomeTypeFilter,
        bool includeTotalCount)
    {
        // Execution items can reference Connected System Objects in any system, so name candidates are
        // matched by attribute name rather than by pre-resolved ids. Lowered here so the comparison
        // translates to a plain lower(...) = ... in SQL. Coalesced in tier order below; extend alongside
        // ObjectNaming.ConnectedSystemNameAttributes (guarded by ConnectedSystemNameAttributeTierCountTests).
        var nameCandidate1 = ObjectNaming.ConnectedSystemNameAttributes[0].ToLower();
        var nameCandidate2 = ObjectNaming.ConnectedSystemNameAttributes[1].ToLower();
        var nameCandidate3 = ObjectNaming.ConnectedSystemNameAttributes[2].ToLower();

        // Header-tier read: do NOT eager-load the ConnectedSystemObject graph. The previous
        // implementation Include()d each RPEI's CSO plus its entire (multi-valued) AttributeValues
        // collection and then projected in memory, so a page that landed on a few large group CSOs
        // pulled tens of thousands of attribute-value rows into memory just to render 100 grid rows
        // (the 100% CPU seen on large activities). Instead, resolve the live display name / external
        // id / type via correlated subqueries in the SQL projection below. Filters and sorts still
        // reference the CSO navigations; EF Core translates those to SQL without an Include.
        // AsNoTracking because this is a read-only projection.
        var query = Repository.Database.ActivityRunProfileExecutionItems
            .AsNoTracking()
            .Where(a => a.ActivityId == activityId);

        // Apply object type filter if specified (falls back to ObjectTypeSnapshot when CSO/Type is null)
        if (objectTypeFilter != null)
        {
            var objectTypes = objectTypeFilter.ToList();
            if (objectTypes.Count > 0)
            {
                query = query.Where(a =>
                    (a.ConnectedSystemObject != null &&
                     a.ConnectedSystemObject.Type != null &&
                     objectTypes.Contains(a.ConnectedSystemObject.Type.Name)) ||
                    (a.ObjectTypeSnapshot != null &&
                     objectTypes.Contains(a.ObjectTypeSnapshot)));
            }
        }

        // Apply error type filter if specified
        if (errorTypeFilter != null)
        {
            var errorTypes = errorTypeFilter.ToList();
            if (errorTypes.Count > 0)
            {
                query = query.Where(a =>
                    a.ErrorType != null &&
                    errorTypes.Contains(a.ErrorType.Value));
            }
        }

        // Apply outcome type filter if specified — matches against the denormalised OutcomeSummary string
        if (outcomeTypeFilter != null)
        {
            // Pre-compute the "<OutcomeType>:" tokens client-side. Embedding ot.ToString() inside the
            // predicate makes EF Core try (and fail) to translate object.ToString() to SQL; hoisting it
            // out yields captured constant strings that translate to OutcomeSummary LIKE '%token%'.
            var outcomeTokens = outcomeTypeFilter.Select(ot => ot + ":").ToList();
            if (outcomeTokens.Count > 0)
            {
                query = query.Where(a =>
                    a.OutcomeSummary != null &&
                    outcomeTokens.Any(token => a.OutcomeSummary.Contains(token)));
            }
        }

        // Apply search filter - search on display name and external ID (case-insensitive).
        // Object type is excluded from search as it has a dedicated filter control.
        // Falls back to snapshot fields when CSO navigation is null.
        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            var searchPattern = $"%{searchQuery}%";
            query = query.Where(item =>
                // Search the name candidates (live CSO attributes)
                (item.ConnectedSystemObject != null &&
                 item.ConnectedSystemObject.AttributeValues.Any(av =>
                    (av.Attribute.Name.ToLower() == nameCandidate1 ||
                     av.Attribute.Name.ToLower() == nameCandidate2 ||
                     av.Attribute.Name.ToLower() == nameCandidate3) &&
                    av.StringValue != null &&
                    EF.Functions.ILike(av.StringValue, searchPattern))) ||
                // Search display name (snapshot fallback)
                (item.DisplayNameSnapshot != null &&
                 EF.Functions.ILike(item.DisplayNameSnapshot, searchPattern)) ||
                // Search external ID (live CSO attribute), rendered by the same shared definition the
                // projection below uses so a non-Text anchor is matchable as it is displayed (#1286)
                (item.ConnectedSystemObject != null &&
                 item.ConnectedSystemObject.AttributeValues
                    .Where(av => av.AttributeId == item.ConnectedSystemObject.ExternalIdAttributeId)
                    .AsQueryable()
                    .Select(ExternalIdValueText.FromAttributeValue)
                    .Any(externalIdText => EF.Functions.ILike(externalIdText!, searchPattern))) ||
                // Search external ID (snapshot fallback)
                (item.ExternalIdSnapshot != null &&
                 EF.Functions.ILike(item.ExternalIdSnapshot, searchPattern)));
        }

        // Apply sorting
        var ordered = sortBy?.ToLower() switch
        {
            // Sorts on the rendered external id, so the sort key matches what the External Id column
            // shows for every anchor type rather than only for a Text one (#1286).
            "externalid" => sortDescending
                ? query.OrderByDescending(item => item.ConnectedSystemObject != null
                    ? item.ConnectedSystemObject.AttributeValues
                        .Where(av => av.AttributeId == item.ConnectedSystemObject.ExternalIdAttributeId)
                        .AsQueryable()
                        .Select(ExternalIdValueText.FromAttributeValue)
                        .FirstOrDefault() ?? item.ExternalIdSnapshot
                    : item.ExternalIdSnapshot)
                : query.OrderBy(item => item.ConnectedSystemObject != null
                    ? item.ConnectedSystemObject.AttributeValues
                        .Where(av => av.AttributeId == item.ConnectedSystemObject.ExternalIdAttributeId)
                        .AsQueryable()
                        .Select(ExternalIdValueText.FromAttributeValue)
                        .FirstOrDefault() ?? item.ExternalIdSnapshot
                    : item.ExternalIdSnapshot),
            // Sorts on the resolved live name, coalescing the naming tiers in preference order so the
            // sort key matches what the Display Name column renders, then the snapshot for deleted CSOs.
            "displayname" or "name" => sortDescending
                ? query.OrderByDescending(item => item.ConnectedSystemObject != null
                    ? item.ConnectedSystemObject.AttributeValues.Where(av => av.Attribute.Name.ToLower() == nameCandidate1).Select(av => av.StringValue).FirstOrDefault()
                      ?? item.ConnectedSystemObject.AttributeValues.Where(av => av.Attribute.Name.ToLower() == nameCandidate2).Select(av => av.StringValue).FirstOrDefault()
                      ?? item.ConnectedSystemObject.AttributeValues.Where(av => av.Attribute.Name.ToLower() == nameCandidate3).Select(av => av.StringValue).FirstOrDefault()
                      ?? item.DisplayNameSnapshot
                    : item.DisplayNameSnapshot)
                : query.OrderBy(item => item.ConnectedSystemObject != null
                    ? item.ConnectedSystemObject.AttributeValues.Where(av => av.Attribute.Name.ToLower() == nameCandidate1).Select(av => av.StringValue).FirstOrDefault()
                      ?? item.ConnectedSystemObject.AttributeValues.Where(av => av.Attribute.Name.ToLower() == nameCandidate2).Select(av => av.StringValue).FirstOrDefault()
                      ?? item.ConnectedSystemObject.AttributeValues.Where(av => av.Attribute.Name.ToLower() == nameCandidate3).Select(av => av.StringValue).FirstOrDefault()
                      ?? item.DisplayNameSnapshot
                    : item.DisplayNameSnapshot),
            "type" or "objecttype" => sortDescending
                ? query.OrderByDescending(item => item.ConnectedSystemObject != null && item.ConnectedSystemObject.Type != null
                    ? item.ConnectedSystemObject.Type.Name
                    : item.ObjectTypeSnapshot)
                : query.OrderBy(item => item.ConnectedSystemObject != null && item.ConnectedSystemObject.Type != null
                    ? item.ConnectedSystemObject.Type.Name
                    : item.ObjectTypeSnapshot),
            "errortype" => sortDescending
                ? query.OrderByDescending(item => item.ErrorType)
                : query.OrderBy(item => item.ErrorType),
            _ => sortDescending
                ? query.OrderByDescending(item => item.Id)
                : query.OrderBy(item => item.Id)
        };

        // Deterministic tie-break: Skip/Take windows are only stable under a total order, and every named sort
        // key above can tie (a run over one object type gives every item the same type; a clean run gives every
        // item the same null error type). Without it, PostgreSQL may order tied rows differently per window,
        // repeating some items and skipping others as the reader scrolls. The default sort is already by id, so
        // the tie-break is a no-op there rather than a second key that could disagree with the first.
        query = ordered.ThenBy(item => item.Id);

        // Count query. It scans every matching item rather than a window of them, so it is the expensive half
        // of this method at scale and is skipped entirely when the caller already holds the total. Sorting
        // cannot change how many items match, so a caller only has to re-count when the filters change.
        int? totalCount = null;
        if (includeTotalCount)
            totalCount = await query.CountAsync();

        // Apply the window, then project in SQL to only the columns the header needs. The live
        // display name and external-id value are pulled with correlated subqueries against the CSO's
        // AttributeValues (no full-collection materialisation), falling back to the RPEI snapshot
        // columns when the CSO is gone or the value is absent. External-id formatting mirrors
        // ConnectedSystemObjectAttributeValue.ToStringNoName and runs in memory over the <= count
        // projected rows.
        var projected = await query
            .Skip(offset).Take(count)
            .Select(i => new
            {
                i.Id,
                i.ErrorType,
                i.ObjectChangeType,
                i.OutcomeSummary,
                i.DisplayNameSnapshot,
                i.ExternalIdSnapshot,
                i.ObjectTypeSnapshot,
                DisplayNameLive =
                    i.ConnectedSystemObject!.AttributeValues.Where(av => av.Attribute.Name.ToLower() == nameCandidate1).Select(av => av.StringValue).FirstOrDefault()
                    ?? i.ConnectedSystemObject!.AttributeValues.Where(av => av.Attribute.Name.ToLower() == nameCandidate2).Select(av => av.StringValue).FirstOrDefault()
                    ?? i.ConnectedSystemObject!.AttributeValues.Where(av => av.Attribute.Name.ToLower() == nameCandidate3).Select(av => av.StringValue).FirstOrDefault(),
                TypeLive = i.ConnectedSystemObject!.Type!.Name,
                // External id rendered by ExternalIdValueText, the shared definition the Connector Space
                // list uses too. Keep the projection single-column: selecting an anonymous type of the raw
                // columns here (`.Select(av => new {...}).FirstOrDefault()`) makes EF Core emit a
                // ROW_NUMBER() window over the WHOLE AttributeValues table instead of a correlated
                // subquery, which is catastrophic at scale.
                ExtIdText = i.ConnectedSystemObject!.AttributeValues
                    .Where(av => av.AttributeId == i.ConnectedSystemObject!.ExternalIdAttributeId)
                    .AsQueryable()
                    .Select(ExternalIdValueText.FromAttributeValue)
                    .FirstOrDefault()
            })
            .ToListAsync();

        // Project to the header DTO in memory (fallback to snapshot fields when the live CSO value
        // is absent, preserving historical display data for deleted objects).
        var results = projected.Select(p => new ActivityRunProfileExecutionItemHeader
        {
            Id = p.Id,
            ExternalIdValue = p.ExtIdText ?? p.ExternalIdSnapshot,
            DisplayName = p.DisplayNameLive ?? p.DisplayNameSnapshot,
            ConnectedSystemObjectType = p.TypeLive ?? p.ObjectTypeSnapshot,
            ErrorType = p.ErrorType,
            ObjectChangeType = p.ObjectChangeType,
            OutcomeSummary = p.OutcomeSummary
        }).ToList();

        return (results, totalCount);
    }

    /// <summary>
    /// Normalised per-dimension stat counts for one Activity, buildable either from the persisted
    /// stat counter rows (#1078, the cheap path) or by aggregating the RPEI/outcome tables (the
    /// legacy and finalisation path). <see cref="BuildActivityRunProfileExecutionStats"/> maps
    /// either source onto the stats model identically.
    /// </summary>
    private sealed class ActivityStatAggregation
    {
        public Dictionary<ObjectChangeType, int> ChangeTypeCounts { get; } = new();
        public Dictionary<string, int> ObjectTypeCounts { get; } = new();
        public Dictionary<ActivityRunProfileExecutionItemErrorType, int> ErrorTypeCounts { get; } = new();
        public Dictionary<NoChangeReason, int> NoChangeReasonCounts { get; } = new();
        public Dictionary<ActivityRunProfileExecutionItemSyncOutcomeType, int> OutcomeTypeCounts { get; } = new();

        /// <summary>
        /// Entries an import read and discarded through an exclusion, keyed by the excluded Container's id
        /// (#1255). Only ever populated from the counter rows: there are no execution items to aggregate it
        /// from, which is the whole reason the count has to be written as the import runs.
        /// </summary>
        public Dictionary<int, int> ExclusionDiscardCounts { get; } = new();
    }

    public async Task<ActivityRunProfileExecutionStats> GetActivityRunProfileExecutionStatsAsync(Guid activityId)
    {
        // Get total objects processed from the activity itself (tracks all objects in scope)
        var activity = await Repository.Database.Activities.OrderBy(a => a.Id).FirstOrDefaultAsync(a => a.Id == activityId);

        // Counter maintenance is raw SQL; on non-relational providers (the EF in-memory test
        // provider) stats always derive from aggregation, exactly as before #1078.
        var isRelational = Repository.Database.Database.IsRelational();

        // Cheap path (#1078): in-progress Activities read the advisory incremental counters the
        // persistence paths maintain; finalised Activities read the exact stored counters. Either
        // way the read is O(counter rows), never an aggregation over the RPEI/outcome tables.
        if (activity != null && isRelational && (activity.Status == ActivityStatus.InProgress || activity.RunProfileExecutionStatsFinalised))
        {
            var counterAggregation = await BuildStatAggregationFromCountersAsync(activityId);
            return BuildActivityRunProfileExecutionStats(activityId, activity, counterAggregation);
        }

        // Legacy path: the Activity completed before the counter table existed (or the Activity
        // row is missing). Aggregate from the RPEI/outcome tables as before.
        var aggregation = await BuildStatAggregationFromRpeiTablesAsync(activityId);

        // Lazily finalise completed legacy Activities so every subsequent read is cheap; their
        // stats are immutable, so aggregating more than once is pure waste.
        if (activity != null && isRelational && IsTerminalActivityStatus(activity.Status))
        {
            await ReplaceCountersWithAggregationAsync(activity.Id, aggregation, alsoPersistFinalisedFlag: true);
            activity.RunProfileExecutionStatsFinalised = true;
        }

        return BuildActivityRunProfileExecutionStats(activityId, activity, aggregation);
    }

    /// <inheritdoc />
    public async Task FinaliseActivityRunProfileExecutionStatsAsync(Activity activity)
    {
        // Non-relational providers have no counter table; leaving the flag false keeps their
        // stats on the aggregation path, matching pre-#1078 behaviour.
        if (!Repository.Database.Database.IsRelational())
            return;

        var aggregation = await BuildStatAggregationFromRpeiTablesAsync(activity.Id);
        await ReplaceCountersWithAggregationAsync(activity.Id, aggregation, alsoPersistFinalisedFlag: false);

        // The caller's subsequent UpdateActivityAsync persists the flag together with the
        // terminal status; if that update never happens, the flag stays false and the lazy
        // finalisation above repairs it on first read.
        activity.RunProfileExecutionStatsFinalised = true;
    }

    /// <inheritdoc />
    public async Task<ActivityProgress?> GetActivityProgressAsync(Guid activityId)
    {
        // Scalar projection only: the progress read is served at a high frequency while a run is
        // executing, so it must never materialise the Activity's execution item graph.
        var progress = await Repository.Database.Activities
            .AsNoTracking()
            .Where(a => a.Id == activityId)
            .Select(a => new ActivityProgress
            {
                ActivityId = a.Id,
                Status = a.Status,
                Message = a.Message,
                ObjectsProcessed = a.ObjectsProcessed,
                ObjectsToProcess = a.ObjectsToProcess,
                Created = a.Created,
                Executed = a.Executed == default ? null : a.Executed,
                TargetType = a.TargetType,
                RunType = a.ConnectedSystemRunType
            })
            .SingleOrDefaultAsync();

        if (progress == null)
            return null;

        // The run's steps (#454). A handful of rows per Activity, and the progress read is the one
        // call the portal, the API and PowerShell all make while a run is executing, so the steps
        // travel with the progress rather than needing a second round trip.
        progress.Phases = await GetActivityPhasesAsync(activityId);

        // Operation breakdown from the stat counter rows (#1078): advisory incremental values
        // while the run is in flight, exact values once finalised. O(counter rows) either way.
        var counters = await Repository.Database.ActivityStatCounters
            .AsNoTracking()
            .Where(c => c.ActivityId == activityId &&
                        (c.Dimension == ActivityStatDimension.ObjectChangeType || c.Dimension == ActivityStatDimension.ErrorType))
            .ToListAsync();

        foreach (var counter in counters)
        {
            switch (counter.Dimension)
            {
                case ActivityStatDimension.ObjectChangeType when int.TryParse(counter.Key, out var changeType):
                    progress.OperationCounts[((ObjectChangeType)changeType).ToString()] = counter.Count;
                    break;
                case ActivityStatDimension.ErrorType:
                    progress.TotalErrors += counter.Count;
                    break;
            }
        }

        return progress;
    }

    public async Task<List<ActivityPhase>> GetActivityPhasesAsync(Guid activityId)
    {
        return await Repository.Database.ActivityPhases
            .AsNoTracking()
            .Where(p => p.ActivityId == activityId)
            .OrderBy(p => p.Order)
            .ToListAsync();
    }

    private static bool IsTerminalActivityStatus(ActivityStatus status) => status.IsTerminal();

    /// <summary>
    /// Builds the stat aggregation from the persisted <see cref="ActivityStatCounter"/> rows.
    /// Rows whose enum keys cannot be parsed (which should not occur) are skipped defensively.
    /// </summary>
    private async Task<ActivityStatAggregation> BuildStatAggregationFromCountersAsync(Guid activityId)
    {
        var aggregation = new ActivityStatAggregation();
        var counters = await Repository.Database.ActivityStatCounters
            .AsNoTracking()
            .Where(c => c.ActivityId == activityId)
            .ToListAsync();

        foreach (var counter in counters)
        {
            var count = (int)Math.Min(counter.Count, int.MaxValue);
            switch (counter.Dimension)
            {
                case ActivityStatDimension.ObjectTypeName:
                    aggregation.ObjectTypeCounts[counter.Key] = count;
                    break;
                case ActivityStatDimension.ObjectChangeType when int.TryParse(counter.Key, out var changeType):
                    aggregation.ChangeTypeCounts[(ObjectChangeType)changeType] = count;
                    break;
                case ActivityStatDimension.ErrorType when int.TryParse(counter.Key, out var errorType):
                    aggregation.ErrorTypeCounts[(ActivityRunProfileExecutionItemErrorType)errorType] = count;
                    break;
                case ActivityStatDimension.NoChangeReason when int.TryParse(counter.Key, out var reason):
                    aggregation.NoChangeReasonCounts[(NoChangeReason)reason] = count;
                    break;
                case ActivityStatDimension.OutcomeType when int.TryParse(counter.Key, out var outcomeType):
                    aggregation.OutcomeTypeCounts[(ActivityRunProfileExecutionItemSyncOutcomeType)outcomeType] = count;
                    break;
                case ActivityStatDimension.ExcludedContainer when int.TryParse(counter.Key, out var containerId):
                    aggregation.ExclusionDiscardCounts[containerId] = count;
                    break;
            }
        }

        return aggregation;
    }

    /// <summary>
    /// Builds the stat aggregation by aggregating the RPEI and Sync Outcome tables: the exact
    /// (and at scale, expensive) source used for legacy reads and completion-time finalisation.
    /// </summary>
    private async Task<ActivityStatAggregation> BuildStatAggregationFromRpeiTablesAsync(Guid activityId)
    {
        var aggregation = new ActivityStatAggregation();
        var rpeiQuery = Repository.Database.ActivityRunProfileExecutionItems
            .Where(q => q.Activity.Id == activityId);

        // The one dimension this source cannot answer, read from its counter rows so that an aggregation is a
        // complete answer whichever path built it. A discarded entry produced no execution item to aggregate,
        // which is exactly why the import writes the count as it runs (#1255).
        var exclusionCounters = await Repository.Database.ActivityStatCounters
            .AsNoTracking()
            .Where(c => c.ActivityId == activityId && c.Dimension == ActivityStatDimension.ExcludedContainer)
            .ToListAsync();
        foreach (var counter in exclusionCounters.Where(c => int.TryParse(c.Key, CultureInfo.InvariantCulture, out _)))
            aggregation.ExclusionDiscardCounts[int.Parse(counter.Key, CultureInfo.InvariantCulture)] =
                (int)Math.Min(counter.Count, int.MaxValue);

        var changeTypeData = await rpeiQuery
            .GroupBy(q => new { q.ObjectChangeType, q.NoChangeReason })
            .Select(g => new { g.Key.ObjectChangeType, g.Key.NoChangeReason, Count = g.Count() })
            .ToListAsync();
        foreach (var row in changeTypeData)
        {
            aggregation.ChangeTypeCounts[row.ObjectChangeType] =
                aggregation.ChangeTypeCounts.GetValueOrDefault(row.ObjectChangeType) + row.Count;

            // Reasons are only meaningful (and only consumed) for NoChange items.
            if (row.ObjectChangeType == ObjectChangeType.NoChange && row.NoChangeReason.HasValue)
            {
                aggregation.NoChangeReasonCounts[row.NoChangeReason.Value] =
                    aggregation.NoChangeReasonCounts.GetValueOrDefault(row.NoChangeReason.Value) + row.Count;
            }
        }

        // Object type counts with names. Falls back to ObjectTypeSnapshot when the CSO/Type
        // navigation is null (e.g. export RPEIs where the CSO was deleted, or the snapshot was
        // populated but FK not retained).
        var objectTypeCounts = await rpeiQuery
            .Select(q => q.ConnectedSystemObject != null && q.ConnectedSystemObject.Type != null
                ? q.ConnectedSystemObject.Type.Name
                : q.ObjectTypeSnapshot)
            .Where(name => name != null)
            .GroupBy(name => name!)
            .Select(g => new { TypeName = g.Key, Count = g.Count() })
            .ToListAsync();
        foreach (var row in objectTypeCounts)
            aggregation.ObjectTypeCounts[row.TypeName] = row.Count;

        var errorTypeCounts = await rpeiQuery
            .Where(q => q.ErrorType != null && q.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet)
            .GroupBy(q => q.ErrorType!.Value)
            .Select(g => new { ErrorType = g.Key, Count = g.Count() })
            .ToListAsync();
        foreach (var row in errorTypeCounts)
            aggregation.ErrorTypeCounts[row.ErrorType] = row.Count;

        var outcomeTypeCounts = await Repository.Database.ActivityRunProfileExecutionItemSyncOutcomes
            .Where(o => o.ActivityRunProfileExecutionItem.Activity.Id == activityId)
            .GroupBy(o => o.OutcomeType)
            .Select(g => new { OutcomeType = g.Key, Count = g.Count() })
            .ToListAsync();
        foreach (var row in outcomeTypeCounts)
            aggregation.OutcomeTypeCounts[row.OutcomeType] = row.Count;

        return aggregation;
    }

    /// <summary>
    /// Replaces the Activity's counter rows with the exact values from the given aggregation,
    /// atomically, optionally also persisting the finalised flag directly (used by the lazy
    /// legacy path, which has no subsequent Activity update to carry the flag).
    /// </summary>
    private async Task ReplaceCountersWithAggregationAsync(Guid activityId, ActivityStatAggregation aggregation, bool alsoPersistFinalisedFlag)
    {
        var deltas = new Dictionary<ActivityStatCounterKey, long>();
        foreach (var (changeType, count) in aggregation.ChangeTypeCounts)
            deltas[new ActivityStatCounterKey(activityId, ActivityStatDimension.ObjectChangeType, ActivityStatCounterCalculator.EnumKey(changeType))] = count;
        foreach (var (typeName, count) in aggregation.ObjectTypeCounts)
            deltas[new ActivityStatCounterKey(activityId, ActivityStatDimension.ObjectTypeName, typeName)] = count;
        foreach (var (errorType, count) in aggregation.ErrorTypeCounts)
            deltas[new ActivityStatCounterKey(activityId, ActivityStatDimension.ErrorType, ActivityStatCounterCalculator.EnumKey(errorType))] = count;
        foreach (var (reason, count) in aggregation.NoChangeReasonCounts)
            deltas[new ActivityStatCounterKey(activityId, ActivityStatDimension.NoChangeReason, ActivityStatCounterCalculator.EnumKey(reason))] = count;
        foreach (var (outcomeType, count) in aggregation.OutcomeTypeCounts)
            deltas[new ActivityStatCounterKey(activityId, ActivityStatDimension.OutcomeType, ActivityStatCounterCalculator.EnumKey(outcomeType))] = count;

        var database = Repository.Database.Database;
        var ownTransaction = database.CurrentTransaction == null ? await database.BeginTransactionAsync() : null;
        try
        {
            // Only the dimensions this aggregation recomputes. Counters that cannot be derived from the execution
            // items (the exclusion discard counts, #1255: a discarded entry produced no item) would otherwise be
            // deleted and never rewritten, so the Activity would lose them the moment it completed.
            await database.ExecuteSqlRawAsync(
                @"DELETE FROM ""ActivityStatCounters"" WHERE ""ActivityId"" = {0} AND ""Dimension"" = ANY({1})",
                activityId,
                RunProfileExecutionStatsDimensions.RecomputedFromExecutionItems.Select(d => (int)d).ToArray());
            await ActivityStatCounterWriter.UpsertDeltasAsync(Repository.Database, deltas);
            if (alsoPersistFinalisedFlag)
                await database.ExecuteSqlRawAsync(@"UPDATE ""Activities"" SET ""RunProfileExecutionStatsFinalised"" = TRUE WHERE ""Id"" = {0}", activityId);
            if (ownTransaction != null)
                await ownTransaction.CommitAsync();
        }
        finally
        {
            if (ownTransaction != null)
                await ownTransaction.DisposeAsync();
        }
    }

    /// <summary>
    /// Maps a stat aggregation onto the stats model. Preserves the pre-counter semantics exactly:
    /// when outcome counts exist they drive the richer per-outcome stats (e.g. one object exported
    /// to two systems counts twice), otherwise the RPEI ObjectChangeType counts drive the legacy
    /// mapping, with the Activity's target type disambiguating housekeeping deletions.
    /// </summary>
    private static ActivityRunProfileExecutionStats BuildActivityRunProfileExecutionStats(Guid activityId, Activity? activity, ActivityStatAggregation aggregation)
    {
        var totalObjectsProcessed = activity?.ObjectsProcessed ?? 0;
        var hasOutcomes = aggregation.OutcomeTypeCounts.Count > 0;

        var objectTypeCounts = aggregation.ObjectTypeCounts;
        var errorTypeCounts = aggregation.ErrorTypeCounts;
        var totalObjectTypes = objectTypeCounts.Count;

        // Shared stats always come from RPEIs. Total errors equals the sum of the per-error-type
        // counts because both use the same predicate (ErrorType set and not NotSet).
        var totalObjectChangeCount = aggregation.ChangeTypeCounts.Values.Sum();
        var totalObjectErrors = errorTypeCounts.Values.Sum();

        int ChangeTypeCount(ObjectChangeType changeType) => aggregation.ChangeTypeCounts.GetValueOrDefault(changeType);
        int OutcomeCount(ActivityRunProfileExecutionItemSyncOutcomeType outcomeType) => aggregation.OutcomeTypeCounts.GetValueOrDefault(outcomeType);

        // --- Outcome-based or RPEI-based stats depending on whether outcomes exist ---
        int totalCsoAdds, totalCsoUpdates, totalCsoDeletes;
        int totalProjections, totalJoins, totalAttributeFlows;
        int totalDisconnections, totalDisconnectedOutOfScope;
        int totalExported, totalDeprovisioned;
        int totalPendingExportsFromOutcomes;
        int totalDriftCorrections;
        int totalProvisioned;
        int totalMvoDeleted;

        if (hasOutcomes)
        {
            // Import stats from outcomes. Deletions: CsoDeleted (sync-phase actual deletions)
            // + DeletionDetected (import-phase detection).
            totalCsoAdds = OutcomeCount(ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded);
            totalCsoUpdates = OutcomeCount(ActivityRunProfileExecutionItemSyncOutcomeType.CsoUpdated);
            totalCsoDeletes = OutcomeCount(ActivityRunProfileExecutionItemSyncOutcomeType.CsoDeleted)
                + OutcomeCount(ActivityRunProfileExecutionItemSyncOutcomeType.DeletionDetected);

            // Sync stats from outcomes
            totalProjections = OutcomeCount(ActivityRunProfileExecutionItemSyncOutcomeType.Projected);
            totalJoins = OutcomeCount(ActivityRunProfileExecutionItemSyncOutcomeType.Joined);
            totalAttributeFlows = OutcomeCount(ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow);
            totalDisconnections = OutcomeCount(ActivityRunProfileExecutionItemSyncOutcomeType.Disconnected);
            totalDisconnectedOutOfScope = OutcomeCount(ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope);

            // Export stats from outcomes
            totalExported = OutcomeCount(ActivityRunProfileExecutionItemSyncOutcomeType.Exported);
            totalDeprovisioned = OutcomeCount(ActivityRunProfileExecutionItemSyncOutcomeType.Deprovisioned);

            // Pending Export, drift correction, provisioning and Metaverse Object deletion
            // stats from outcomes (housekeeping batches, #1020)
            // Both staging outcome types: a queued deprovision is a Pending Export, and only its intent differs
            totalPendingExportsFromOutcomes = OutcomeCount(ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated)
                                              + OutcomeCount(ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued);
            totalDriftCorrections = OutcomeCount(ActivityRunProfileExecutionItemSyncOutcomeType.DriftCorrection);
            totalProvisioned = OutcomeCount(ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned);
            totalMvoDeleted = OutcomeCount(ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted);
        }
        else
        {
            // Legacy fallback: derive stats from RPEI ObjectChangeType (pre-outcome graph behaviour).
            // ObjectChangeType.Deleted is ambiguous between CSO and Metaverse Object deletions; the
            // Activity's target type disambiguates: a housekeeping batch only ever deletes MVOs.
            var isHousekeeping = activity?.TargetType == ActivityTargetType.MetaverseObjectHousekeeping;
            var totalDeleted = ChangeTypeCount(ObjectChangeType.Deleted);

            totalCsoAdds = ChangeTypeCount(ObjectChangeType.Added);
            totalCsoUpdates = ChangeTypeCount(ObjectChangeType.Updated);
            totalCsoDeletes = isHousekeeping ? 0 : totalDeleted;
            totalMvoDeleted = isHousekeeping ? totalDeleted : 0;

            totalProjections = ChangeTypeCount(ObjectChangeType.Projected);
            totalJoins = ChangeTypeCount(ObjectChangeType.Joined);
            totalAttributeFlows = ChangeTypeCount(ObjectChangeType.AttributeFlow);

            totalDisconnections = ChangeTypeCount(ObjectChangeType.Disconnected);
            totalDisconnectedOutOfScope = ChangeTypeCount(ObjectChangeType.DisconnectedOutOfScope);

            totalExported = ChangeTypeCount(ObjectChangeType.Exported);
            totalDeprovisioned = ChangeTypeCount(ObjectChangeType.Deprovisioned);

            totalPendingExportsFromOutcomes = 0;

            totalDriftCorrections = ChangeTypeCount(ObjectChangeType.DriftCorrection);

            totalProvisioned = 0; // Provisioned is an outcome-only concept; no ObjectChangeType equivalent
        }

        // --- Stats that always come from RPEIs (no outcome type equivalent) ---
        var totalOutOfScopeRetainJoin = ChangeTypeCount(ObjectChangeType.OutOfScopeRetainJoin);
        var totalCreated = ChangeTypeCount(ObjectChangeType.Created);

        // Pending Export stats: use outcome-based count when available, otherwise fall back to RPEI count
        var totalPendingExports = hasOutcomes ? totalPendingExportsFromOutcomes : ChangeTypeCount(ObjectChangeType.PendingExport);

        // Pending Export reconciliation stats (populated during confirming import)
        // TotalPendingExportsConfirmed is stored directly on the Activity (not derived from RPEIs)
        var totalPendingExportsConfirmed = activity?.PendingExportsConfirmed ?? 0;
        // Retrying and Failed are derived from error type counts
        errorTypeCounts.TryGetValue(ActivityRunProfileExecutionItemErrorType.ExportNotConfirmed, out var totalPendingExportsRetrying);
        errorTypeCounts.TryGetValue(ActivityRunProfileExecutionItemErrorType.ExportConfirmationFailed, out var totalPendingExportsFailed);

        // NoChange stats (always from RPEIs — no outcome equivalent)
        var totalNoChanges = ChangeTypeCount(ObjectChangeType.NoChange);
        var totalMvoNoAttributeChanges = aggregation.NoChangeReasonCounts.GetValueOrDefault(NoChangeReason.MvoNoAttributeChanges);
        var totalCsoAlreadyCurrent = aggregation.NoChangeReasonCounts.GetValueOrDefault(NoChangeReason.CsoAlreadyCurrent);

        return new ActivityRunProfileExecutionStats
        {
            ActivityId = activityId,
            TotalObjectsProcessed = totalObjectsProcessed,
            TotalObjectChangeCount = totalObjectChangeCount,
            TotalObjectErrors = totalObjectErrors,
            TotalObjectTypes = totalObjectTypes,
            ObjectTypeCounts = objectTypeCounts,
            ErrorTypeCounts = errorTypeCounts,
            EntriesDiscardedByExcludedContainer = aggregation.ExclusionDiscardCounts,

            // Import stats
            TotalCsoAdds = totalCsoAdds,
            TotalCsoUpdates = totalCsoUpdates,
            TotalCsoDeletes = totalCsoDeletes,

            // Sync stats
            TotalProjections = totalProjections,
            TotalJoins = totalJoins,
            TotalAttributeFlows = totalAttributeFlows,
            TotalDisconnections = totalDisconnections,
            TotalDisconnectedOutOfScope = totalDisconnectedOutOfScope,
            TotalOutOfScopeRetainJoin = totalOutOfScopeRetainJoin,
            TotalDriftCorrections = totalDriftCorrections,
            TotalProvisioned = totalProvisioned,
            TotalMvoDeleted = totalMvoDeleted,

            // Direct creation stats
            TotalCreated = totalCreated,

            // Export stats
            TotalExported = totalExported,
            TotalDeprovisioned = totalDeprovisioned,

            // Pending Export stats
            TotalPendingExports = totalPendingExports,

            // Pending Export reconciliation stats
            TotalPendingExportsConfirmed = totalPendingExportsConfirmed,
            TotalPendingExportsRetrying = totalPendingExportsRetrying,
            TotalPendingExportsFailed = totalPendingExportsFailed,

            // NoChange stats
#pragma warning disable CS0618 // Type or member is obsolete
            TotalNoChanges = totalNoChanges,
#pragma warning restore CS0618
            TotalMvoNoAttributeChanges = totalMvoNoAttributeChanges,
            TotalCsoAlreadyCurrent = totalCsoAlreadyCurrent,
        };
    }

    /// <summary>
    /// Loads every causal edge whose effect is one of the given Run Profile Execution Items (#1223).
    /// </summary>
    public async Task<List<CausalEdge>> GetCausalEdgesByEffectRunProfileExecutionItemIdsAsync(IReadOnlyCollection<Guid> effectRunProfileExecutionItemIds)
    {
        if (effectRunProfileExecutionItemIds.Count == 0)
            return [];

        var ids = effectRunProfileExecutionItemIds as List<Guid> ?? effectRunProfileExecutionItemIds.ToList();
        return await Repository.Database.CausalEdges
            .AsNoTracking()
            .Where(e => ids.Contains(e.EffectRunProfileExecutionItemId))
            .OrderBy(e => e.Created)
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<Dictionary<Guid, Guid>> GetExportExecutionItemIdsByPendingExportIdsAsync(IReadOnlyCollection<Guid> pendingExportIds)
    {
        if (pendingExportIds.Count == 0)
            return [];

        var ids = pendingExportIds as List<Guid> ?? pendingExportIds.ToList();
        var rows = await Repository.Database.CausalEdges
            .AsNoTracking()
            .Where(e => e.EdgeType == CausalEdgeType.PendingExportQueueingCausedExportExecution &&
                        e.CausePendingExportId.HasValue &&
                        ids.Contains(e.CausePendingExportId.Value))
            .OrderBy(e => e.Created)
            .Select(e => new { PendingExportId = e.CausePendingExportId!.Value, e.EffectRunProfileExecutionItemId })
            .ToListAsync();

        // A Pending Export is executed once and deleted, so one row per id is the shape. Grouping rather than
        // keying directly is defensive only: a duplicate must not throw and take the whole panel with it, and
        // the earliest edge is the one that executed the export the confirmation is about.
        return rows
            .GroupBy(r => r.PendingExportId)
            .ToDictionary(g => g.Key, g => g.First().EffectRunProfileExecutionItemId);
    }

    /// <summary>
    /// Summarises the given Run Profile Execution Items for the causal walk (#1223); presence in the result is
    /// the retention check.
    /// </summary>
    public async Task<Dictionary<Guid, CausalChainItemSummary>> GetRunProfileExecutionItemCausalSummariesAsync(
        IReadOnlyCollection<Guid> runProfileExecutionItemIds)
    {
        if (runProfileExecutionItemIds.Count == 0)
            return [];

        var ids = runProfileExecutionItemIds as List<Guid> ?? runProfileExecutionItemIds.ToList();
        // A scalar projection: the walk needs a handful of fields per cause, and materialising execution items
        // to answer it would load attribute graphs for every cause in a cascade.
        var summaries = await Repository.Database.ActivityRunProfileExecutionItems
            .AsNoTracking()
            .Where(r => ids.Contains(r.Id))
            .Select(r => new CausalChainItemSummary
            {
                Id = r.Id,
                ObjectChangeType = r.ObjectChangeType,
                ConnectedSystemObjectId = r.ConnectedSystemObjectId,
                ActivityExecuted = r.Activity.Executed,
                ConnectedSystemId = r.Activity.ConnectedSystemId,
                ExternalIdSnapshot = r.ExternalIdSnapshot
            })
            .ToListAsync();

        return summaries.ToDictionary(s => s.Id);
    }

    /// <summary>
    /// The import event that last changed a Connected System Object at or before the given Activity time,
    /// excluding the asking item itself (#1223).
    /// </summary>
    public async Task<CausalSourceImportEvent?> GetLatestImportItemForCsoAsync(
        Guid connectedSystemObjectId, DateTime atOrBeforeActivityExecuted, Guid excludeRunProfileExecutionItemId)
    {
        return await GetLatestImportEventAsync(
            Repository.Database.ActivityRunProfileExecutionItems
                .Where(r => r.ConnectedSystemObjectId == connectedSystemObjectId),
            atOrBeforeActivityExecuted, excludeRunProfileExecutionItemId);
    }

    /// <summary>
    /// The source-import hop's degraded key (#1495): the same event as
    /// <see cref="GetLatestImportItemForCsoAsync"/>, found by the external ID snapshotted on the items after
    /// the record's deletion has nulled the id they would otherwise be walked on.
    /// </summary>
    public async Task<CausalSourceImportEvent?> GetLatestImportItemForExternalIdAsync(
        int connectedSystemId, string externalIdSnapshot, DateTime atOrBeforeActivityExecuted,
        Guid excludeRunProfileExecutionItemId)
    {
        return await GetLatestImportEventAsync(
            Repository.Database.ActivityRunProfileExecutionItems
                .Where(r => r.ExternalIdSnapshot == externalIdSnapshot
                    && r.Activity.ConnectedSystemId == connectedSystemId),
            atOrBeforeActivityExecuted, excludeRunProfileExecutionItemId);
    }

    /// <summary>
    /// The latest import-side event among <paramref name="candidateRecords"/> at or before the given
    /// Activity time, excluding the asking item: the shared shape of the causal walk's two timeline keys.
    /// </summary>
    private async Task<CausalSourceImportEvent?> GetLatestImportEventAsync(
        IQueryable<ActivityRunProfileExecutionItem> candidateRecords, DateTime atOrBeforeActivityExecuted,
        Guid excludeRunProfileExecutionItemId)
    {
        return await candidateRecords
            .AsNoTracking()
            .Where(r => r.Id != excludeRunProfileExecutionItemId
                && (r.ObjectChangeType == ObjectChangeType.Added
                    || r.ObjectChangeType == ObjectChangeType.Updated
                    || r.ObjectChangeType == ObjectChangeType.Deleted)
                && r.Activity.Executed <= atOrBeforeActivityExecuted)
            .OrderByDescending(r => r.Activity.Executed)
            .Select(r => new CausalSourceImportEvent
            {
                RunProfileExecutionItemId = r.Id,
                ChangeType = r.ObjectChangeType,
                DisplayName = r.DisplayNameSnapshot,
                Occurred = r.Activity.Executed,
                ConnectedSystemId = r.Activity.ConnectedSystemId,
                // The Activity model carries the system by id alone; resolve the name live. Unlike the edge
                // snapshots this is a derived hop, so a deleted system degrades the sentence rather than
                // falsifying history.
                ConnectedSystemName = Repository.Database.ConnectedSystems
                    .Where(cs => cs.Id == r.Activity.ConnectedSystemId)
                    .Select(cs => cs.Name)
                    .FirstOrDefault()
            })
            .FirstOrDefaultAsync();
    }

    public async Task<ActivityRunProfileExecutionItem?> GetActivityRunProfileExecutionItemAsync(Guid id)
    {
        // AsTracking required: multiple Include paths create cycles through ReferenceValue navigations
        // (e.g. CSO -> AttributeValues -> ReferenceValue(CSO) -> AttributeValues).
        return await Repository.Database.ActivityRunProfileExecutionItems
            .AsTracking()
            .AsSplitQuery() // Use split query to avoid cartesian explosion from multiple collection includes
            // CSO includes
            .Include(q => q.ConnectedSystemObject)
            .ThenInclude(cso => cso!.AttributeValues)
            .ThenInclude(av => av.Attribute)
            .Include(q => q.ConnectedSystemObject)
            .ThenInclude(cso => cso!.AttributeValues)
            .ThenInclude(av => av.ReferenceValue)
            .ThenInclude(rv => rv!.Type)
            .Include(q => q.ConnectedSystemObject)
            .ThenInclude(cso => cso!.AttributeValues)
            .ThenInclude(av => av.ReferenceValue)
            .ThenInclude(rv => rv!.AttributeValues)
            .ThenInclude(av => av.Attribute)
            .Include(q => q.ConnectedSystemObject)
            .ThenInclude(cso => cso!.Type)
            // CSO -> MVO includes (for projected/joined CSOs to access the linked MVO)
            .Include(q => q.ConnectedSystemObject)
            .ThenInclude(cso => cso!.MetaverseObject)
            .ThenInclude(mvo => mvo!.AttributeValues)
            .ThenInclude(av => av.Attribute)
            .Include(q => q.ConnectedSystemObject)
            .ThenInclude(cso => cso!.MetaverseObject)
            .ThenInclude(mvo => mvo!.AttributeValues)
            .ThenInclude(av => av.ReferenceValue)
            .ThenInclude(rv => rv!.Type)
            .Include(q => q.ConnectedSystemObject)
            .ThenInclude(cso => cso!.MetaverseObject)
            .ThenInclude(mvo => mvo!.AttributeValues)
            .ThenInclude(av => av.ReferenceValue)
            .ThenInclude(rv => rv!.AttributeValues)
            .ThenInclude(av => av.Attribute)
            .Include(q => q.ConnectedSystemObject)
            .ThenInclude(cso => cso!.MetaverseObject)
            .ThenInclude(mvo => mvo!.Type)
            // CSO change includes (for import updates/deletes)
            .Include(q => q.ConnectedSystemObjectChange)
            .ThenInclude(c => c!.AttributeChanges)
            .ThenInclude(ac => ac.Attribute)
            .Include(q => q.ConnectedSystemObjectChange)
            .ThenInclude(c => c!.AttributeChanges)
            .ThenInclude(ac => ac.ValueChanges)
            .ThenInclude(vc => vc.ReferenceValue)
            .ThenInclude(rv => rv!.AttributeValues)
            .ThenInclude(av => av.Attribute)
            .Include(q => q.ConnectedSystemObjectChange)
            .ThenInclude(c => c!.AttributeChanges)
            .ThenInclude(ac => ac.ValueChanges)
            .ThenInclude(vc => vc.ReferenceValue)
            .ThenInclude(rv => rv!.Type)
            // For deletions, include the preserved object type to support deletion rule UI display
            .Include(q => q.ConnectedSystemObjectChange)
            .ThenInclude(c => c!.DeletedObjectType)
            // MVO change includes (for sync operations that produce MVO attribute changes)
            .Include(q => q.MetaverseObjectChange)
            .ThenInclude(c => c!.MetaverseObject)
            .ThenInclude(mvo => mvo!.AttributeValues)
            .ThenInclude(av => av.Attribute)
            .Include(q => q.MetaverseObjectChange)
            .ThenInclude(c => c!.MetaverseObject)
            .ThenInclude(mvo => mvo!.AttributeValues)
            .ThenInclude(av => av.ReferenceValue)
            .ThenInclude(rv => rv!.Type)
            .Include(q => q.MetaverseObjectChange)
            .ThenInclude(c => c!.MetaverseObject)
            .ThenInclude(mvo => mvo!.AttributeValues)
            .ThenInclude(av => av.ReferenceValue)
            .ThenInclude(rv => rv!.AttributeValues)
            .ThenInclude(av => av.Attribute)
            .Include(q => q.MetaverseObjectChange)
            .ThenInclude(c => c!.MetaverseObject)
            .ThenInclude(mvo => mvo!.Type)
            // MVO change attribute detail (for displaying MVO attribute changes in outcome tree)
            .Include(q => q.MetaverseObjectChange)
            .ThenInclude(c => c!.AttributeChanges)
            .ThenInclude(ac => ac.Attribute)
            .Include(q => q.MetaverseObjectChange)
            .ThenInclude(c => c!.AttributeChanges)
            .ThenInclude(ac => ac.ValueChanges)
            .ThenInclude(vc => vc.ReferenceValue)
            .ThenInclude(rv => rv!.Type)
            .Include(q => q.MetaverseObjectChange)
            .ThenInclude(c => c!.AttributeChanges)
            .ThenInclude(ac => ac.ValueChanges)
            .ThenInclude(vc => vc.ReferenceValue)
            .ThenInclude(rv => rv!.AttributeValues)
            .ThenInclude(av => av.Attribute)
            // Sync outcome tree (causality chain)
            .Include(q => q.SyncOutcomes)
            // PendingExportCreated outcome CSO change snapshots (for rendering attribute detail)
            .Include(q => q.SyncOutcomes)
            .ThenInclude(o => o.ConnectedSystemObjectChange)
            .ThenInclude(c => c!.AttributeChanges)
            .ThenInclude(ac => ac.Attribute)
            .Include(q => q.SyncOutcomes)
            .ThenInclude(o => o.ConnectedSystemObjectChange)
            .ThenInclude(c => c!.AttributeChanges)
            .ThenInclude(ac => ac.ValueChanges)
            .SingleOrDefaultAsync(q => q.Id == id);
    }
    #endregion

    #region configuration drift
    public async Task<Dictionary<int, DateTime>> GetLastFullSynchronisationStartsAsync(IList<int> connectedSystemIds)
    {
        if (connectedSystemIds.Count == 0)
            return new Dictionary<int, DateTime>();

        // TargetOperationType must be Execute: Run Profile CRUD activities carry ConnectedSystemRunType too (so the
        // run type survives the profile's deletion), and without this filter deleting a Full Synchronisation Run
        // Profile would be mistaken for having run one.
        //
        // CompleteWithWarning counts: a warning is a non-fatal operational note (e.g. a delta falling back to a full
        // import), not a reason to distrust that the run applied the configuration. Errors, failures and cancellations
        // do not count, so a system whose Full Synchronisation failed keeps reporting its changes as pending.
        return await Repository.Database.Activities
            .Where(a => a.ConnectedSystemId != null
                        && connectedSystemIds.Contains(a.ConnectedSystemId!.Value)
                        && a.TargetType == ActivityTargetType.ConnectedSystemRunProfile
                        && a.TargetOperationType == ActivityTargetOperationType.Execute
                        && a.ConnectedSystemRunType == ConnectedSystemRunType.FullSynchronisation
                        && (a.Status == ActivityStatus.Complete || a.Status == ActivityStatus.CompleteWithWarning))
            .GroupBy(a => a.ConnectedSystemId!.Value)
            .Select(g => new { ConnectedSystemId = g.Key, LastStart = g.Max(a => a.Executed) })
            .ToDictionaryAsync(x => x.ConnectedSystemId, x => x.LastStart);
    }

    public async Task<List<ConfigurationChangeImpactData>> GetConfigurationChangeImpactsSinceAsync(DateTime since, ConfigurationChangeClass minimumClass)
    {
        return await Repository.Database.Activities
            .Where(a => a.Created >= since && a.ConfigurationChangeClass >= minimumClass)
            .Select(a => new ConfigurationChangeImpactData
            {
                When = a.Created,
                Class = a.ConfigurationChangeClass,
                ConnectedSystemId = a.ConnectedSystemId,
                SyncRuleId = a.SyncRuleId,
                MetaverseObjectTypeId = a.MetaverseObjectTypeId,
                MetaverseAttributeId = a.MetaverseAttributeId,
                ServiceSettingKey = a.ServiceSettingKey
            })
            .ToListAsync();
    }
    #endregion

    // Bulk RPEI operations (BulkInsertRpeisAsync, BulkUpdateRpeiOutcomesAsync,
    // PersistRpeiCsoChangesAsync, DetachRpeisFromChangeTracker, UpdateActivityProgressOutOfBandAsync)
    // have been moved to PostgresData.SyncRepository.RpeiOperations.cs — they are Worker-only
    // and no longer part of the shared IActivityRepository interface.
}
