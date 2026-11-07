// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace JIM.PostgresData.Repositories;

public partial class ConnectedSystemRepository
{
    /// <summary>
    /// Loads Pending Exports by id for execution (#2813): the one load export execution uses, for a batch run
    /// sequentially, a batch re-loaded on a parallel worker's own context, and a chunk of the deferred pass. A large
    /// group's member attribute is loaded in part (#2816).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The graph is everything execution reads: each change's attribute (reference resolution, the Connectors); the
    /// object's type (LdapConnectorExport's object class fallback, and the type it gives the placeholder changes it
    /// synthesises); and the object's attribute values WITH their attributes and their object.
    /// </para>
    /// <para>
    /// Not every value (#2816). Loading every member of every group in a batch to change a few cost a 500,000-user leaver
    /// step 39 s of loading and 8.6 s of optimistic apply. A multi-valued reference attribute the batch's changes can all
    /// be asked about, and that holds at least the cap <see cref="ExportValueLoadPlanner"/> sets above anything they could
    /// remove, is loaded with only the rows they could match, beside its count; everything else is loaded whole. The
    /// object then says which attributes were loaded in part (<see cref="ConnectedSystemObject.PartiallyLoadedAttributes"/>), and is
    /// never saved back (<see cref="ConnectedSystemObject.EnsureWhollyLoaded"/>).
    /// </para>
    /// <para>
    /// Five statements, none reading more than the changes name: the exports without their objects' values; the
    /// candidate attributes' definitions; each candidate's count, stopping at its cap; the rows the changes could match
    /// (<see cref="SyncRepository.GetReconciliationProbeValuesAsync"/>); and the objects' other values, by ranges of
    /// attribute id that leave the trimmed attributes out, so each is an index range scan on (object, attribute).
    /// </para>
    /// <para>
    /// Untracked, whatever the context's default: everything execution writes is raw SQL, so tracking bought nothing but
    /// cost. With identity resolution, so the graph holds one instance of each attribute and type however many values
    /// share it.
    /// </para>
    /// </remarks>
    public async Task<List<PendingExport>> GetPendingExportsByIdsAsync(IReadOnlyCollection<Guid> pendingExportIds)
    {
        if (pendingExportIds.Count == 0)
            return [];

        // The EF test doubles run no SQL; they are handed every value, as before.
        if (Repository.Database.Database?.IsRelational() != true)
            return await ExportBatchQuery(pendingExportIds, withObjectValues: true).ToListAsync();

        var exports = await ExportBatchQuery(pendingExportIds, withObjectValues: false).ToListAsync();
        var objects = exports
            .Select(pe => pe.ConnectedSystemObject)
            .OfType<ConnectedSystemObject>()
            .DistinctBy(cso => cso.Id)
            .ToList();
        if (objects.Count == 0)
            return exports;

        var candidates = await PlanExportValueLoadAsync(exports, objects);
        var counts = await CountCandidateValuesAsync(candidates);
        var loads = ExportValueLoadPlanner.BuildValueLoads(candidates, counts);

        var trimmed = candidates
            .Where(candidate => loads.TryGetValue(candidate.ConnectedSystemObjectId, out var load) &&
                                load.PartiallyLoadedAttributes.ContainsKey(candidate.Attribute.Id))
            .ToList();
        var candidateValueIds = await GetCandidateValueIdsAsync(trimmed);

        await LoadObjectValuesAsync(exports, objects, loads, candidateValueIds);
        return exports;
    }

    /// <summary>
    /// The exports, their changes with their attributes, and their objects with their Object Types; with every value of
    /// each object when <paramref name="withObjectValues"/> is set.
    /// </summary>
    private IQueryable<PendingExport> ExportBatchQuery(IReadOnlyCollection<Guid> pendingExportIds, bool withObjectValues)
    {
        var query = Repository.Database.PendingExports
            .AsNoTrackingWithIdentityResolution()
            .AsSplitQuery()
            .Include(pe => pe.AttributeValueChanges)
                .ThenInclude(avc => avc.Attribute)
            .Include(pe => pe.ConnectedSystemObject)
                .ThenInclude(cso => cso!.Type)
            .AsQueryable();

        if (withObjectValues)
        {
            query = query.Include(pe => pe.ConnectedSystemObject)
                .ThenInclude(cso => cso!.AttributeValues)
                    .ThenInclude(av => av.Attribute);
        }

        return query
            .Where(pe => pendingExportIds.Contains(pe.Id))
            .OrderBy(pe => pe.CreatedAt)
            .ThenBy(pe => pe.Id);
    }

    /// <summary>
    /// The batch's candidates (<see cref="ExportValueLoadPlanner.PlanCandidates"/>), from its objects' Object Types'
    /// multi-valued reference attributes. The database's case folding is checked only where a question would compare
    /// ignoring case.
    /// </summary>
    private async Task<List<ExportValueTrimCandidate>> PlanExportValueLoadAsync(List<PendingExport> exports, List<ConnectedSystemObject> objects)
    {
        var typeIds = objects.Select(cso => cso.TypeId).Distinct().ToList();
        var attributes = await Repository.Database.ConnectedSystemAttributes
            .AsNoTracking()
            .Where(attribute => typeIds.Contains(attribute.ConnectedSystemObjectType.Id)
                                && attribute.Type == AttributeDataType.Reference
                                && attribute.AttributePlurality == AttributePlurality.MultiValued)
            .Select(attribute => new { TypeId = attribute.ConnectedSystemObjectType.Id, Attribute = attribute })
            .ToListAsync();
        if (attributes.Count == 0)
            return [];

        var attributesByObjectTypeId = attributes
            .GroupBy(a => a.TypeId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ConnectedSystemObjectTypeAttribute>)g.Select(a => a.Attribute).ToList());

        var candidates = ExportValueLoadPlanner.PlanCandidates(exports, attributesByObjectTypeId, databaseFoldsCaseLikeIdentifierKey: true);
        if (candidates.Any(candidate => candidate.Question is { CaseInsensitiveValues.Count: > 0 }) &&
            !await Repository.Sync.DoesDatabaseFoldCaseLikeIdentifierKeyAsync())
        {
            candidates = ExportValueLoadPlanner.PlanCandidates(exports, attributesByObjectTypeId, databaseFoldsCaseLikeIdentifierKey: false);
        }

        return candidates;
    }

    /// <summary>
    /// The statement behind <see cref="CountCandidateValuesAsync"/>: per object and attribute, how many values it holds,
    /// stopping at that pair's cap, so a group's count reads at most the cap's rows of its (object, attribute) index
    /// however many members it has.
    /// </summary>
    internal const string ExportCandidateValueCountSql =
        @"SELECT p.object_id, p.attribute_id, c.value_count
          FROM unnest(@objectIds, @attributeIds, @caps) AS p(object_id, attribute_id, cap)
          CROSS JOIN LATERAL (
              SELECT count(*)::integer AS value_count
              FROM (
                  SELECT 1
                  FROM ""ConnectedSystemObjectAttributeValues"" AS av
                  WHERE av.""ConnectedSystemObjectId"" = p.object_id
                    AND av.""AttributeId"" = p.attribute_id
                  LIMIT p.cap
              ) AS capped
          ) AS c";

    /// <summary>
    /// Each candidate's held value count, counted up to its cap.
    /// </summary>
    private async Task<Dictionary<(Guid, int), int>> CountCandidateValuesAsync(List<ExportValueTrimCandidate> candidates)
    {
        var counts = new Dictionary<(Guid, int), int>();
        if (candidates.Count == 0)
            return counts;

        var connection = Repository.Database.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = ExportCandidateValueCountSql;
        command.Transaction = Repository.Database.Database.CurrentTransaction?.GetDbTransaction();
        command.Parameters.Add(new NpgsqlParameter("objectIds", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
            { Value = candidates.Select(c => c.ConnectedSystemObjectId).ToArray() });
        command.Parameters.Add(new NpgsqlParameter("attributeIds", NpgsqlDbType.Array | NpgsqlDbType.Integer)
            { Value = candidates.Select(c => c.Attribute.Id).ToArray() });
        command.Parameters.Add(new NpgsqlParameter("caps", NpgsqlDbType.Array | NpgsqlDbType.Integer)
            { Value = candidates.Select(c => c.Cap).ToArray() });

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            counts[(reader.GetGuid(0), reader.GetInt32(1))] = reader.GetInt32(2);
        return counts;
    }

    /// <summary>
    /// The ids of the rows the trimmed attributes' changes could match, as reconciliation's probe finds them (#2669).
    /// </summary>
    private async Task<HashSet<Guid>> GetCandidateValueIdsAsync(List<ExportValueTrimCandidate> trimmed)
    {
        var questions = trimmed.Select(candidate => candidate.Question!).Where(question => question.TupleCount > 0).ToList();
        if (questions.Count == 0)
            return [];

        var answers = await Repository.Sync.GetReconciliationProbeValuesAsync(questions);
        return answers.SelectMany(answer => answer.CandidateValues).Select(value => value.Id).ToHashSet();
    }

    /// <summary>
    /// Loads the objects' values (all but the trimmed attributes', and the trimmed attributes' candidate rows, through
    /// <see cref="GetAttributeValuesExcludingAsync"/>), each with its attribute and its object, and records on each object how
    /// its values were loaded. An attribute the batch's changes loaded is the same instance here; one only a value names is
    /// joined to the object's Object Type, as the whole load's identity resolution joined it.
    /// </summary>
    private async Task LoadObjectValuesAsync(List<PendingExport> exports, List<ConnectedSystemObject> objects,
        Dictionary<Guid, ExportValueLoad> loads, HashSet<Guid> candidateValueIds)
    {
        var rows = await GetAttributeValuesExcludingAsync(
            objects.Select(cso => (cso.Id, loads.TryGetValue(cso.Id, out var load)
                ? (IReadOnlyCollection<int>)load.PartiallyLoadedAttributes.Keys.ToList()
                : [])),
            candidateValueIds);

        var attributes = exports
            .SelectMany(pe => pe.AttributeValueChanges)
            .Select(change => change.Attribute)
            .Where(attribute => attribute != null)
            .DistinctBy(attribute => attribute.Id)
            .ToDictionary(attribute => attribute.Id);
        var objectsById = objects.ToDictionary(cso => cso.Id);

        foreach (var (row, cso) in rows.Select(r => (r, objectsById[r.ObjectId])))
        {
            if (!attributes.TryGetValue(row.Attribute.Id, out var attribute))
            {
                attribute = row.Attribute;
                attributes[attribute.Id] = attribute;
                if (row.AttributeTypeId == cso.TypeId)
                {
                    attribute.ConnectedSystemObjectType = cso.Type;
                    if (cso.Type.Attributes.All(a => a.Id != attribute.Id))
                        cso.Type.Attributes.Add(attribute);
                }
            }

            row.Value.Attribute = attribute;
            row.Value.ConnectedSystemObject = cso;
            cso.AttributeValues.Add(row.Value);
        }

        foreach (var (objectId, load) in loads)
            load.ApplyTo(objectsById[objectId]);
    }
}
