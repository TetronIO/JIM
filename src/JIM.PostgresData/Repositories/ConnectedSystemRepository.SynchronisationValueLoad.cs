// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace JIM.PostgresData.Repositories;

/// <summary>
/// #2818: a Delta Synchronisation page that leaves large groups' member attributes out where Drift Detection is their only
/// reader, which compares them in the database instead (<c>SyncRepository.GetDriftHeldReferenceAnswersAsync</c>).
/// </summary>
public partial class ConnectedSystemRepository
{
    /// <summary>
    /// The page's objects, tracked as the whole load tracks them, each with every value but those of the attributes left out
    /// of it, which are recorded on it (<see cref="SynchronisationPartiallyLoadedAttribute"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four statements: the page's objects without values; for the attributes the plan names, each object's count capped at
    /// the threshold and whether any value references an object on the page; the values of the objects loaded whole,
    /// exactly as the whole load's include reads them; and the other objects' values by attribute-id ranges that skip the
    /// attributes left out (<see cref="AttributeValuesByRangesSql"/>, shared with #2816 and #2817), so no member row is read.
    /// Both value loads are tracked, with each value's attribute, so relationship fix-up joins them to their objects and
    /// to the tracked attribute definitions, as the include did, and a value is never left untracked under a tracked object.
    /// </para>
    /// <para>
    /// An attribute is left out of an object only where Drift Detection can be answered from the database when it runs,
    /// after the page's Metaverse Objects and joins are saved: the object is joined, Normal and the only one on the page
    /// joined to its Metaverse Object; it holds at least the threshold's values; and none of them references an object on
    /// the page. The whole load reads what a held value resolves to when the page loads; the database answers when drift
    /// runs; and between the two only the page's own objects can join or leave a Metaverse Object, so a group referencing
    /// none of them gets the same answer either way. Every other object is loaded whole, and the reasons are counted on
    /// <paramref name="loadSpan"/>.
    /// </para>
    /// </remarks>
    private async Task<List<ConnectedSystemObject>> LoadModifiedPageLeavingAttributesOutAsync(IQueryable<ConnectedSystemObject> pageWithoutValues,
        SynchronisationValueLoadPlan plan, System.Diagnostics.Activity? loadSpan)
    {
        var page = await pageWithoutValues.ToListAsync();
        if (page.Count == 0)
            return page;

        var (leftOut, loadedWhole) = await DecideAttributesLeftOutAsync(page, plan);

        var wholeIds = page.Where(cso => !leftOut.ContainsKey(cso.Id)).Select(cso => cso.Id).ToList();
        if (wholeIds.Count > 0)
        {
            await Repository.Database.ConnectedSystemObjects
                .AsTracking()
                .Include(cso => cso.AttributeValues)
                    .ThenInclude(av => av.Attribute)
                .Where(cso => wholeIds.Contains(cso.Id))
                .ToListAsync();
        }

        if (leftOut.Count > 0)
        {
            var parameters = AttributeValuesByRangesParameters(
                leftOut.Select(pair => (pair.Key, (IReadOnlyCollection<int>)pair.Value.Select(a => a.AttributeId).ToList())), []);
            await Repository.Database.ConnectedSystemObjectAttributeValues
                .FromSqlRaw(AttributeValuesByRangesSql, parameters.Cast<object>().ToArray())
                .AsTracking()
                .Include(av => av.Attribute)
                .OrderBy(av => av.Id)
                .ToListAsync();

            foreach (var cso in page.Where(cso => leftOut.ContainsKey(cso.Id)))
            {
                cso.PartiallyLoadedAttributes = leftOut[cso.Id].ToDictionary(a => a.AttributeId, a => (PartiallyLoadedAttribute)a);

                // Relationship fix-up joins tracked values to tracked objects; a value of an attribute left out here
                // would mean the load read what it exists not to read.
                if (cso.AttributeValues.Any(av => cso.PartiallyLoadedAttributes.ContainsKey(av.AttributeId)))
                    throw new InvalidOperationException($"Connected System Object {cso.Id} was loaded with values of an attribute it was to be loaded without.");
            }
        }

        loadSpan?.SetTag("leftOutObjectCount", leftOut.Count);
        loadSpan?.SetTag("leftOutAttributeCount", leftOut.Values.Sum(attributes => attributes.Count));
        loadSpan?.SetTag("loadedWholeAttributeCount", loadedWhole.Values.Sum());
        foreach (var (reason, count) in loadedWhole)
            loadSpan?.SetTag($"loadedWhole.{reason}", count);
        return page;
    }

    /// <summary>
    /// For each of the page's objects, the planned attributes left out of it, and how many planned attributes were kept
    /// whole for each reason.
    /// </summary>
    private async Task<(Dictionary<Guid, List<SynchronisationPartiallyLoadedAttribute>> LeftOut, Dictionary<SynchronisationValueLoadFallbackReason, int> LoadedWhole)>
        DecideAttributesLeftOutAsync(List<ConnectedSystemObject> page, SynchronisationValueLoadPlan plan)
    {
        var leftOut = new Dictionary<Guid, List<SynchronisationPartiallyLoadedAttribute>>();
        var loadedWhole = new Dictionary<SynchronisationValueLoadFallbackReason, int>();
        void LoadWhole(SynchronisationValueLoadFallbackReason reason) => loadedWhole[reason] = loadedWhole.GetValueOrDefault(reason) + 1;

        var sharedMetaverseObjectIds = page
            .Where(cso => cso.MetaverseObjectId.HasValue)
            .GroupBy(cso => cso.MetaverseObjectId!.Value)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet();

        var candidates = new List<(ConnectedSystemObject Cso, ConnectedSystemObjectTypeAttribute Attribute)>();
        foreach (var cso in page)
        {
            if (!plan.AttributesByObjectTypeId.TryGetValue(cso.TypeId, out var attributes))
                continue;

            SynchronisationValueLoadFallbackReason? reason = cso.Status != ConnectedSystemObjectStatus.Normal ? SynchronisationValueLoadFallbackReason.NotNormal
                : cso.MetaverseObjectId == null ? SynchronisationValueLoadFallbackReason.NotJoined
                : sharedMetaverseObjectIds.Contains(cso.MetaverseObjectId.Value) ? SynchronisationValueLoadFallbackReason.SharesMetaverseObject
                : null;
            foreach (var attribute in attributes)
            {
                if (reason is { } fallback)
                    LoadWhole(fallback);
                else
                    candidates.Add((cso, attribute));
            }
        }

        if (candidates.Count == 0)
            return (leftOut, loadedWhole);

        var (counts, referencingPage) = await CountCandidateValuesAndPageReferencesAsync(candidates, page.Select(cso => cso.Id).ToArray(),
            Math.Max(plan.MinimumHeldValueCount, 1));

        foreach (var (cso, attribute) in candidates)
        {
            var count = counts.GetValueOrDefault((cso.Id, attribute.Id));
            if (count < plan.MinimumHeldValueCount)
            {
                LoadWhole(SynchronisationValueLoadFallbackReason.BelowThreshold);
                continue;
            }

            if (referencingPage.Contains((cso.Id, attribute.Id)))
            {
                LoadWhole(SynchronisationValueLoadFallbackReason.ReferencesObjectOnPage);
                continue;
            }

            if (!leftOut.TryGetValue(cso.Id, out var attributes))
                leftOut[cso.Id] = attributes = [];
            attributes.Add(new SynchronisationPartiallyLoadedAttribute(attribute.Id, attribute.Name, HoldsAny: count > 0));
        }

        return (leftOut, loadedWhole);
    }

    /// <summary>
    /// The statement finding which candidates hold a value referencing an object on the page, led by the
    /// <c>ReferenceValueId</c> index, so it reads the page's objects' references and never a group's whole membership.
    /// </summary>
    internal const string CandidatePageReferenceSql =
        @"SELECT DISTINCT av.""ConnectedSystemObjectId"", av.""AttributeId""
          FROM ""ConnectedSystemObjectAttributeValues"" AS av
          WHERE av.""ReferenceValueId"" = ANY(@pageObjectIds)
            AND av.""ConnectedSystemObjectId"" = ANY(@objectIds)";

    /// <summary>
    /// Each candidate's held value count, capped at <paramref name="cap"/> (<see cref="ExportCandidateValueCountSql"/>,
    /// #2816), and the candidates referencing an object on the page, in one round trip.
    /// </summary>
    private Task<(Dictionary<(Guid, int), int> Counts, HashSet<(Guid, int)> ReferencingPage)> CountCandidateValuesAndPageReferencesAsync(
        List<(ConnectedSystemObject Cso, ConnectedSystemObjectTypeAttribute Attribute)> candidates, Guid[] pageObjectIds, int cap)
        => CountHeldValuesAndPageReferencesAsync(candidates.Select(c => (c.Cso.Id, c.Attribute.Id, cap)).ToList(), pageObjectIds,
            candidates.Select(c => c.Cso.Id).Distinct().ToArray());

    /// <summary>
    /// Plans a Delta Synchronisation page whose load may leave attributes out (#2934): the page's candidates, as
    /// <see cref="LoadValueCountPageAsync"/> reads them, each counting the values its load would bring with
    /// <paramref name="plan"/>. A planned attribute counts nothing where the load could leave it out whichever candidates
    /// share the page; otherwise it is counted as the load would bring it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The load decides object by object as the page loads (<see cref="DecideAttributesLeftOutAsync"/>), so the count makes
    /// the same decision for the page's possible company: every candidate counted with these and every one counted before
    /// them and not yet handed out (<paramref name="earlierCandidateIds"/>). A planned attribute is counted when the object
    /// is not Normal, not joined, or shares its Metaverse Object with another of these candidates; when it holds fewer than
    /// the plan's threshold of its values; or when one of its values references a candidate. Otherwise it is left out, and
    /// so are its member rows: only up to the threshold of them is read.
    /// </para>
    /// <para>
    /// Three statements, or four: the candidates with their other values counted, in EF on the page loader's selection;
    /// each planned attribute counted to the threshold, or to <paramref name="valueCountCap"/> where it is counted whole,
    /// and the references to candidates, in one round trip; and a count to the cap of the few attributes that reached the
    /// threshold but reference a candidate.
    /// </para>
    /// </remarks>
    private async Task<List<(Guid Id, int ValueCount)>> LoadValueCountPageLeavingAttributesOutAsync(IQueryable<ConnectedSystemObject> selection,
        int pageSize, Guid afterId, int valueCountCap, SynchronisationValueLoadPlan plan, IReadOnlyCollection<Guid> earlierCandidateIds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(valueCountCap, 1);
        if (pageSize > MaxSyncPageSize)
            pageSize = MaxSyncPageSize;

        using var span = ActivitySource.StartActivity("Cso.Page.CountValuesLeavingAttributesOut");
        var plannedAttributeIds = plan.AttributesByObjectTypeId.Values.SelectMany(attributes => attributes).Select(a => a.Id).Distinct().ToList();
        var rows = await selection
            .OrderBy(cso => cso.Id)
            .Where(cso => cso.Id.CompareTo(afterId) > 0)
            .Take(pageSize)
            .Select(cso => new
            {
                cso.Id,
                cso.TypeId,
                cso.Status,
                cso.MetaverseObjectId,
                OtherValueCount = cso.AttributeValues
                    .Where(av => !plannedAttributeIds.Contains(av.AttributeId))
                    .OrderBy(av => av.AttributeId)
                    .Take(valueCountCap)
                    .Count()
            })
            .AsNoTracking()
            .ToListAsync();
        if (rows.Count == 0)
            return [];

        var sharedMetaverseObjectIds = rows
            .Where(r => r.MetaverseObjectId.HasValue)
            .GroupBy(r => r.MetaverseObjectId!.Value)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet();

        var threshold = Math.Max(plan.MinimumHeldValueCount, 1);
        var planned = rows
            .Where(r => plan.AttributesByObjectTypeId.ContainsKey(r.TypeId))
            .SelectMany(r => plan.AttributesByObjectTypeId[r.TypeId].Select(attribute => (
                ObjectId: r.Id,
                AttributeId: attribute.Id,
                Whole: r.Status != ConnectedSystemObjectStatus.Normal || r.MetaverseObjectId == null ||
                       sharedMetaverseObjectIds.Contains(r.MetaverseObjectId.Value))))
            .ToList();

        var valueCounts = rows.ToDictionary(r => r.Id, r => (long)r.OtherValueCount);
        if (planned.Count > 0)
        {
            var candidateIds = earlierCandidateIds.Concat(rows.Select(r => r.Id)).Distinct().ToArray();
            var (held, referencing) = await CountHeldValuesAndPageReferencesAsync(
                planned.Select(p => (p.ObjectId, p.AttributeId, p.Whole ? valueCountCap : threshold)).ToList(), candidateIds,
                planned.Where(p => !p.Whole).Select(p => p.ObjectId).Distinct().ToArray());

            var referencingCandidate = new List<(Guid ObjectId, int AttributeId, int Cap)>();
            foreach (var (objectId, attributeId, whole) in planned)
            {
                var count = held.GetValueOrDefault((objectId, attributeId));
                if (whole || count < plan.MinimumHeldValueCount)
                    valueCounts[objectId] += count;
                else if (referencing.Contains((objectId, attributeId)))
                    referencingCandidate.Add((objectId, attributeId, valueCountCap));
            }

            if (referencingCandidate.Count > 0)
            {
                var (whole, _) = await CountHeldValuesAndPageReferencesAsync(referencingCandidate, [], []);
                foreach (var (objectId, attributeId, _) in referencingCandidate)
                    valueCounts[objectId] += whole.GetValueOrDefault((objectId, attributeId));
            }
        }

        span?.SetTag("objectCount", rows.Count);
        span?.SetTag("plannedAttributeCount", planned.Count);
        span?.SetTag("valueCount", valueCounts.Values.Sum());
        return rows.Select(r => (r.Id, (int)Math.Min(valueCounts[r.Id], valueCountCap))).ToList();
    }

    /// <summary>
    /// Each object's held values of an attribute, counted up to its own cap (<see cref="ExportCandidateValueCountSql"/>),
    /// and which of <paramref name="referencingObjectIds"/> hold a value referencing one of <paramref name="pageObjectIds"/>
    /// (<see cref="CandidatePageReferenceSql"/>), in one round trip.
    /// </summary>
    private async Task<(Dictionary<(Guid, int), int> Counts, HashSet<(Guid, int)> ReferencingPage)> CountHeldValuesAndPageReferencesAsync(
        List<(Guid ObjectId, int AttributeId, int Cap)> counts, Guid[] pageObjectIds, Guid[] referencingObjectIds)
    {
        var connection = (NpgsqlConnection)Repository.Database.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(connection);
        await using var batch = new NpgsqlBatch(connection)
        {
            Transaction = (NpgsqlTransaction?)Repository.Database.Database.CurrentTransaction?.GetDbTransaction()
        };

        var count = new NpgsqlBatchCommand(ExportCandidateValueCountSql);
        count.Parameters.Add(new NpgsqlParameter("objectIds", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = counts.Select(c => c.ObjectId).ToArray() });
        count.Parameters.Add(new NpgsqlParameter("attributeIds", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = counts.Select(c => c.AttributeId).ToArray() });
        count.Parameters.Add(new NpgsqlParameter("caps", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = counts.Select(c => c.Cap).ToArray() });
        batch.BatchCommands.Add(count);

        var references = new NpgsqlBatchCommand(CandidatePageReferenceSql);
        references.Parameters.Add(new NpgsqlParameter("pageObjectIds", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = pageObjectIds });
        references.Parameters.Add(new NpgsqlParameter("objectIds", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = referencingObjectIds });
        batch.BatchCommands.Add(references);

        var held = new Dictionary<(Guid, int), int>();
        var referencingPage = new HashSet<(Guid, int)>();
        await using var reader = await batch.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            held[(reader.GetGuid(0), reader.GetInt32(1))] = reader.GetInt32(2);
        await reader.NextResultAsync();
        while (await reader.ReadAsync())
            referencingPage.Add((reader.GetGuid(0), reader.GetInt32(1)));

        return (held, referencingPage);
    }
}
