// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Transactional;

namespace JIM.Application.Servers;

/// <summary>
/// The Causality Explorer's reads (#2100): an object's causal timeline, the runs that queued a Pending Export, each
/// row's nearest recorded causes, and the labels a trail names its foci by.
/// </summary>
/// <remarks>
/// Every read here costs a fixed handful of round trips however many rows it returns. A row's nearest causes are the
/// walk's own first level, built by the walk's own <see cref="GroupIntoCohorts"/> and source-import hop, so the line a
/// row reads can never disagree with the Lineage it opens; only the source import is found differently, for a page of
/// items at once rather than one member at a time.
/// </remarks>
public partial class ActivityServer
{
    /// <summary>
    /// How many timeline rows a page holds when the caller does not say.
    /// </summary>
    public const int CausalTimelineDefaultPageSize = 50;

    /// <summary>
    /// The most timeline rows one page may hold.
    /// </summary>
    public const int CausalTimelineMaxPageSize = 100;

    /// <summary>
    /// Each item's nearest recorded causes (#2100): the first level of the upward walk from it, at most
    /// <see cref="CausalNearestCauses.MaxCohorts"/> cohorts with the total beside them.
    /// </summary>
    /// <param name="runProfileExecutionItemIds">The items whose causes to read.</param>
    /// <returns>The causes per item; an item history retention has removed is absent.</returns>
    public async Task<Dictionary<Guid, CausalNearestCauses>> GetNearestCausesAsync(IReadOnlyCollection<Guid> runProfileExecutionItemIds)
    {
        ArgumentNullException.ThrowIfNull(runProfileExecutionItemIds);

        var ids = runProfileExecutionItemIds.Distinct().ToList();
        if (ids.Count == 0)
            return [];

        var summaries = await Application.Repository.Activity.GetRunProfileExecutionItemCausalSummariesAsync(ids);
        return await BuildNearestCausesAsync(summaries.Values.ToList());
    }

    /// <summary>
    /// One page of an object's causal timeline (#2100): the execution items about it, newest first, each with its
    /// nearest recorded causes. The object may be live or deleted.
    /// </summary>
    /// <param name="subject">The object whose timeline to read.</param>
    /// <param name="page">The page to return, from 1; a lower value reads the first page.</param>
    /// <param name="pageSize">How many rows a page holds, brought within 1 to <see cref="CausalTimelineMaxPageSize"/>.</param>
    public async Task<CausalTimeline> GetCausalTimelineAsync(
        CausalTimelineSubject subject, int page = 1, int pageSize = CausalTimelineDefaultPageSize)
    {
        ArgumentNullException.ThrowIfNull(subject);

        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, CausalTimelineMaxPageSize);
        // Widened before multiplying: a page far past the end reads as an empty page rather than overflowing into a
        // negative skip.
        var skip = (int)Math.Min((long)(page - 1) * pageSize, int.MaxValue);

        var (entries, totalCount) = await Application.Repository.Activity.GetCausalTimelinePageAsync(subject, skip, pageSize);
        var nearestCauses = await BuildNearestCausesAsync(entries.Select(ToCausalSummary).ToList());
        var headers = await GetCausalObjectHeadersAsync([subject]);

        // Without Metaverse Object change tracking, the object's own change records were never written, so its timeline
        // holds only what its joined objects recorded; the explorer says so rather than implying it is complete.
        var changesNotRecorded = subject.Kind == CausalTimelineSubjectKind.MetaverseObject &&
                                 !await Application.ServiceSettings.GetMvoChangeTrackingEnabledAsync();

        return new CausalTimeline
        {
            Subject = subject,
            Header = headers.GetValueOrDefault(subject),
            Rows = ToRows(entries, nearestCauses),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
            MetaverseObjectChangesNotRecorded = changesNotRecorded
        };
    }

    /// <summary>
    /// The runs that staged a Pending Export (#2100), newest first, each with its nearest recorded causes: what the
    /// Pending Export page's Queued by panel lists.
    /// </summary>
    /// <param name="pendingExportId">The Pending Export.</param>
    /// <returns>The staging runs, or null where the Pending Export does not exist.</returns>
    public async Task<CausalQueuedBy?> GetPendingExportQueuedByAsync(Guid pendingExportId)
    {
        var pendingExport = await Application.Repository.ConnectedSystems.GetPendingExportCoreAsync(pendingExportId);
        if (pendingExport == null)
            return null;

        var stagers = pendingExport.Stagers();
        if (stagers.Count == 0)
            return new CausalQueuedBy { PendingExportId = pendingExportId };

        var entries = await Application.Repository.Activity.GetCausalTimelineEntriesAsync(stagers);

        // Newest first. Items of one run share their Activity's time, so the order the export recorded them in, which
        // is the order they staged, settles a tie rather than anything arbitrary such as their ids.
        var retained = stagers
            .Select((id, recorded) => (Entry: entries.GetValueOrDefault(id), Recorded: recorded))
            .Where(stager => stager.Entry != null)
            .OrderByDescending(stager => stager.Entry!.Executed)
            .ThenByDescending(stager => stager.Recorded)
            .Select(stager => stager.Entry!)
            .ToList();
        var nearestCauses = await BuildNearestCausesAsync(retained.Select(ToCausalSummary).ToList());

        return new CausalQueuedBy
        {
            PendingExportId = pendingExportId,
            Rows = ToRows(retained, nearestCauses),
            NotRetainedCount = stagers.Count - retained.Count,
            // The export keeps only its most recent stagers, so a full list may have dropped earlier ones to make room.
            EarlierRunsMayBeOmitted = stagers.Count >= PendingExport.MaxRecordedStagers
        };
    }

    /// <summary>
    /// What is known of each object (#2100): the object itself while it exists, or its deletion record once it does
    /// not. One read per kind of object, and deletion records only for the objects no longer found.
    /// </summary>
    /// <param name="subjects">The objects to describe.</param>
    /// <returns>The header per object; an object neither live nor recorded as deleted is absent.</returns>
    public async Task<Dictionary<CausalTimelineSubject, CausalObjectHeader>> GetCausalObjectHeadersAsync(
        IReadOnlyCollection<CausalTimelineSubject> subjects)
    {
        ArgumentNullException.ThrowIfNull(subjects);

        var headers = new List<CausalObjectHeader>();
        headers.AddRange(await ReadConnectedSystemObjectHeadersAsync(IdsOfKind(subjects, CausalTimelineSubjectKind.ConnectedSystemObject)));
        headers.AddRange(await ReadMetaverseObjectHeadersAsync(IdsOfKind(subjects, CausalTimelineSubjectKind.MetaverseObject)));
        return headers.ToDictionary(header => header.Subject);
    }

    /// <summary>
    /// The labels a Causality Explorer trail names its foci by (#2100), for the whole trail at once: each item with its
    /// run, and each object live or deleted. A focus absent from both is no longer retained.
    /// </summary>
    /// <param name="runProfileExecutionItemIds">The items the trail names.</param>
    /// <param name="subjects">The objects the trail names.</param>
    public async Task<CausalFocusLabels> GetCausalFocusLabelsAsync(
        IReadOnlyCollection<Guid> runProfileExecutionItemIds, IReadOnlyCollection<CausalTimelineSubject> subjects)
    {
        ArgumentNullException.ThrowIfNull(runProfileExecutionItemIds);
        ArgumentNullException.ThrowIfNull(subjects);

        var itemIds = runProfileExecutionItemIds.Distinct().ToList();
        var items = itemIds.Count > 0
            ? await Application.Repository.Activity.GetCausalTimelineEntriesAsync(itemIds)
            : [];

        return new CausalFocusLabels
        {
            Items = items,
            Objects = await GetCausalObjectHeadersAsync(subjects)
        };
    }

    /// <summary>
    /// The nearest causes for many items, in one read of their edges and one of their source imports however many
    /// items there are.
    /// </summary>
    private async Task<Dictionary<Guid, CausalNearestCauses>> BuildNearestCausesAsync(List<CausalChainItemSummary> items)
    {
        var distinctItems = items.DistinctBy(item => item.Id).ToList();
        if (distinctItems.Count == 0)
            return [];

        var edges = await Application.Repository.Activity
            .GetCausalEdgesByEffectRunProfileExecutionItemIdsAsync(distinctItems.Select(item => item.Id).ToList());
        var edgesByEffect = edges
            .GroupBy(edge => edge.EffectRunProfileExecutionItemId)
            .ToDictionary(group => group.Key, group => group.ToList());
        var exportExecutionItemIds = await ResolveExportExecutionItemIdsAsync(edges);

        // Only a synchronisation-side item has an import behind it, exactly as the walk decides it.
        var sourceHopItemIds = distinctItems
            .Where(item => SourceImportHopItemTypes.Contains(item.ObjectChangeType))
            .Select(item => item.Id)
            .ToList();
        var importEvents = sourceHopItemIds.Count > 0
            ? await Application.Repository.Activity.GetLatestImportEventsForItemsAsync(sourceHopItemIds)
            : [];

        return distinctItems.ToDictionary(item => item.Id, item => BuildNearestCauses(
            item,
            edgesByEffect.GetValueOrDefault(item.Id),
            exportExecutionItemIds,
            importEvents.GetValueOrDefault(item.Id)));
    }

    /// <summary>
    /// One item's nearest causes: the walk's root level for it, in the walk's order (its recorded causes, then the
    /// import that fed it), ending as the walk would end it where there are none.
    /// </summary>
    private static CausalNearestCauses BuildNearestCauses(
        CausalChainItemSummary item,
        List<CausalEdge>? edges,
        Dictionary<Guid, Guid> exportExecutionItemIds,
        CausalSourceImportEvent? importEvent)
    {
        var cohorts = edges == null ? [] : GroupIntoCohorts(edges, exportExecutionItemIds);
        if (importEvent != null)
            cohorts.Add(BuildSourceImportCohort(importEvent, item.ConnectedSystemObjectId));

        return new CausalNearestCauses
        {
            RunProfileExecutionItemId = item.Id,
            Cohorts = cohorts.Take(CausalNearestCauses.MaxCohorts).ToList(),
            TotalCohortCount = cohorts.Count,
            Resolution = cohorts.Count > 0
                ? CausalChainResolution.Resolved
                : IsTimelineSevered(item)
                    ? CausalChainResolution.CauseNotRetained
                    : CausalChainResolution.NoFurtherCauses
        };
    }

    /// <summary>
    /// A timeline entry as the walk summarises an item: an entry carries every field the summary does, read from the
    /// same columns, so a page's rows need no second read before their causes can be worked out.
    /// </summary>
    private static CausalChainItemSummary ToCausalSummary(CausalTimelineEntry entry) => new()
    {
        Id = entry.RunProfileExecutionItemId,
        ObjectChangeType = entry.ObjectChangeType,
        ConnectedSystemObjectId = entry.ConnectedSystemObjectId,
        ActivityExecuted = entry.Executed,
        ConnectedSystemId = entry.ConnectedSystemId,
        ExternalIdSnapshot = entry.ExternalId
    };

    private static List<CausalTimelineRow> ToRows(
        IEnumerable<CausalTimelineEntry> entries, Dictionary<Guid, CausalNearestCauses> nearestCauses)
    {
        return entries
            .Select(entry => new CausalTimelineRow
            {
                Entry = entry,
                NearestCauses = nearestCauses[entry.RunProfileExecutionItemId]
            })
            .ToList();
    }

    private static List<Guid> IdsOfKind(IEnumerable<CausalTimelineSubject> subjects, CausalTimelineSubjectKind kind) =>
        subjects.Where(subject => subject.Kind == kind).Select(subject => subject.Id).Distinct().ToList();

    private async Task<List<CausalObjectHeader>> ReadConnectedSystemObjectHeadersAsync(List<Guid> ids)
    {
        if (ids.Count == 0)
            return [];

        var live = await Application.Repository.ConnectedSystems.GetConnectedSystemObjectsCoreByIdsAsync(ids);
        var headers = live
            .Select(cso => new CausalObjectHeader
            {
                Subject = new CausalTimelineSubject(CausalTimelineSubjectKind.ConnectedSystemObject, cso.Id),
                DisplayName = cso.Name,
                ExternalId = cso.ExternalIdAttributeValue?.ToStringNoName(),
                ObjectTypeName = cso.Type.Name,
                ConnectedSystemId = cso.ConnectedSystemId,
                ConnectedSystemName = cso.ConnectedSystem.Name
            })
            .ToList();

        var missing = ids.Except(live.Select(cso => cso.Id)).ToList();
        if (missing.Count == 0)
            return headers;

        var deletionRecords = await Application.Repository.ConnectedSystems.GetDeletedCsoChangesByObjectIdsAsync(missing);
        if (deletionRecords.Count == 0)
            return headers;

        // A deletion record keeps its Connected System's id but not its name; the names are few, and read once.
        var connectedSystemNames = await Application.Repository.ConnectedSystems.GetConnectedSystemNamesAsync();
        headers.AddRange(missing.Join(deletionRecords,
            id => (Guid?)id,
            record => record.DeletedConnectedSystemObjectId,
            (id, record) => new CausalObjectHeader
            {
                Subject = new CausalTimelineSubject(CausalTimelineSubjectKind.ConnectedSystemObject, id),
                IsDeleted = true,
                DeletedAt = record.ChangeTime,
                DisplayName = record.DeletedObjectDisplayName,
                ExternalId = record.DeletedObjectExternalId,
                ObjectTypeName = record.DeletedObjectType?.Name,
                ConnectedSystemId = record.ConnectedSystemId,
                ConnectedSystemName = connectedSystemNames.GetValueOrDefault(record.ConnectedSystemId)
            }));

        return headers;
    }

    private async Task<List<CausalObjectHeader>> ReadMetaverseObjectHeadersAsync(List<Guid> ids)
    {
        if (ids.Count == 0)
            return [];

        var live = await Application.Repository.Metaverse.GetMetaverseObjectsCoreByIdsAsync(ids);
        var headers = live
            .Select(mvo => new CausalObjectHeader
            {
                Subject = new CausalTimelineSubject(CausalTimelineSubjectKind.MetaverseObject, mvo.Id),
                DisplayName = mvo.Name,
                ObjectTypeName = mvo.Type.Name,
                ObjectTypePluralName = mvo.Type.PluralName
            })
            .ToList();

        var missing = ids.Except(live.Select(mvo => mvo.Id)).ToList();
        if (missing.Count == 0)
            return headers;

        var deletionRecords = await Application.Repository.Metaverse.GetDeletedMvoChangesByObjectIdsAsync(missing);
        headers.AddRange(missing.Join(deletionRecords,
            id => (Guid?)id,
            record => record.DeletedMetaverseObjectId,
            (id, record) => new CausalObjectHeader
            {
                Subject = new CausalTimelineSubject(CausalTimelineSubjectKind.MetaverseObject, id),
                IsDeleted = true,
                DeletedAt = record.ChangeTime,
                DisplayName = record.DeletedObjectDisplayName,
                ObjectTypeName = record.DeletedObjectType?.Name,
                ObjectTypePluralName = record.DeletedObjectType?.PluralName
            }));

        return headers;
    }
}
