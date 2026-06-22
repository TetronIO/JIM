// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.ExampleData;
using JIM.Models.ExampleData.DTOs;
using Microsoft.EntityFrameworkCore;
using Serilog;
using System.Diagnostics;
namespace JIM.PostgresData.Repositories;

public class ExampleDataRepository : IExampleDataRepository
{
    private PostgresDataRepository Repository { get; }

    internal ExampleDataRepository(PostgresDataRepository dataRepository)
    {
        Repository = dataRepository;
    }

    #region ExampleDataSets
    public async Task<List<ExampleDataSet>> GetExampleDataSetsAsync()
    {
        return await Repository.Database.ExampleDataSets.Include(q => q.Values).OrderBy(q => q.Name).ToListAsync();
    }

    public async Task<List<ExampleDataSetHeader>> GetExampleDataSetHeadersAsync()
    {
        var datasetHeaders = await Repository.Database.ExampleDataSets.OrderBy(d => d.Name).Select(d => new ExampleDataSetHeader
        {
            Name = d.Name,
            BuiltIn = d.BuiltIn,
            Created = d.Created,
            Id = d.Id,
            Culture = d.Culture,
            ValueCount = d.Values.Count()
        }).ToListAsync();

        return datasetHeaders;
    }

    public async Task<ExampleDataSet?> GetExampleDataSetAsync(string name, string culture, bool withChangeTracking = false)
    {
        IQueryable<ExampleDataSet> query = Repository.Database.ExampleDataSets.Include(q => q.Values);
        if (withChangeTracking)
            query = query.AsTracking();

        return await query.SingleOrDefaultAsync(q => q.Name == name && q.Culture == culture);
    }

    public async Task<ExampleDataSet?> GetExampleDataSetAsync(int id)
    {
        return await Repository.Database.ExampleDataSets.Include(q => q.Values).SingleOrDefaultAsync(q => q.Id == id);
    }

    public async Task CreateExampleDataSetAsync(ExampleDataSet exampleDataSet)
    {
        Repository.Database.ExampleDataSets.Add(exampleDataSet);
        await Repository.Database.SaveChangesAsync();
    }

    public async Task UpdateExampleDataSetAsync(ExampleDataSet exampleDataSet)
    {
        await Repository.Database.SaveChangesAsync();
    }

    public async Task DeleteExampleDataSetAsync(int exampleDataSetId)
    {
        var exampleDataSet = await Repository.Database.ExampleDataSets.Include(q => q.Values).AsTracking().SingleOrDefaultAsync(q => q.Id == exampleDataSetId);
        if (exampleDataSet == null)
        {
            Log.Warning("DeleteExampleDataSetAsync: No such ExampleDetaSet found to delete.");
            return;
        }

        Repository.Database.ExampleDataSetValues.RemoveRange(exampleDataSet.Values);
        Repository.Database.ExampleDataSets.Remove(exampleDataSet);
        await Repository.Database.SaveChangesAsync();
    }
    #endregion

    #region ExampleDataTemplates
    public async Task<List<ExampleDataTemplate>> GetTemplatesAsync()
    {
        var templates = await Repository.Database.ExampleDataTemplates.
            AsSplitQuery(). // Use split query to avoid cartesian explosion from multiple collection includes
            Include(t => t.ObjectTypes).
            ThenInclude(ot => ot.MetaverseObjectType).
            Include(t => t.ObjectTypes).
            ThenInclude(o => o.TemplateAttributes).
            ThenInclude(ta => ta.MetaverseAttribute).
            Include(t => t.ObjectTypes).
            ThenInclude(o => o.TemplateAttributes).
            ThenInclude(ta => ta.ConnectedSystemObjectTypeAttribute).
            Include(t => t.ObjectTypes).
            ThenInclude(o => o.TemplateAttributes).
            ThenInclude(ta => ta.ExampleDataSetInstances).
            ThenInclude(edsi => edsi.ExampleDataSet).
            ThenInclude(eds => eds.Values).
            Include(t => t.ObjectTypes).
            ThenInclude(o => o.TemplateAttributes).
            ThenInclude(ta => ta.AttributeDependency).
            ThenInclude(ad => ad!.MetaverseAttribute).
            OrderBy(t => t.Name).ToListAsync();

        foreach (var t in templates)
            SortExampleDataSetInstances(t);

        return templates;
    }

    public async Task<List<ExampleDataTemplateHeader>> GetTemplateHeadersAsync()
    {
        var templates = await Repository.Database.ExampleDataTemplates.OrderBy(t => t.Name).Select(dgt => new ExampleDataTemplateHeader
        {
            Name = dgt.Name,
            BuiltIn = dgt.BuiltIn,
            Created = dgt.Created,
            Id = dgt.Id
        }).ToListAsync();

        return templates;
    }

    public async Task<ExampleDataTemplate?> GetTemplateAsync(string name)
    {
        var q = Repository.Database.ExampleDataTemplates.
            AsSplitQuery(). // Use split query to avoid cartesian explosion from multiple collection includes
            Include(t => t.ObjectTypes).
            ThenInclude(ot => ot.MetaverseObjectType).
            Include(t => t.ObjectTypes).
            ThenInclude(o => o.TemplateAttributes).
            ThenInclude(ta => ta.MetaverseAttribute).
            Include(t => t.ObjectTypes).
            ThenInclude(o => o.TemplateAttributes).
            ThenInclude(ta => ta.ConnectedSystemObjectTypeAttribute).
            Include(t => t.ObjectTypes).
            ThenInclude(o => o.TemplateAttributes).
            ThenInclude(ta => ta.WeightedStringValues).
            Include(t => t.ObjectTypes).
            ThenInclude(o => o.TemplateAttributes).
            ThenInclude(ta => ta.AttributeDependency).
            ThenInclude(ad => ad!.MetaverseAttribute).
            Include(t => t.ObjectTypes).
            ThenInclude(o => o.TemplateAttributes).
            ThenInclude(ta => ta.ExampleDataSetInstances).
            ThenInclude(edsi => edsi.ExampleDataSet);

        var t = await q.SingleOrDefaultAsync(t => t.Name == name);
        if (t == null)
            return null;

        SortExampleDataSetInstances(t);
        return t;
    }

    public async Task<ExampleDataTemplate?> GetTemplateAsync(int id)
    {
        var q = Repository.Database.ExampleDataTemplates.
            AsSplitQuery(). // Use split query to avoid cartesian explosion from multiple collection includes
            Include(t => t.ObjectTypes).
            ThenInclude(ot => ot.MetaverseObjectType).
            Include(t => t.ObjectTypes).
            ThenInclude(o => o.TemplateAttributes).
            ThenInclude(ta => ta.MetaverseAttribute).
            Include(t => t.ObjectTypes).
            ThenInclude(o => o.TemplateAttributes).
            ThenInclude(ta => ta.ConnectedSystemObjectTypeAttribute).
            Include(t => t.ObjectTypes).
            ThenInclude(o => o.TemplateAttributes).
            ThenInclude(ta => ta.ReferenceMetaverseObjectTypes).
            Include(t => t.ObjectTypes).
            ThenInclude(o => o.TemplateAttributes).
            ThenInclude(ta => ta.WeightedStringValues).
            Include(t => t.ObjectTypes).
            ThenInclude(o => o.TemplateAttributes).
            ThenInclude(ta => ta.AttributeDependency).
            ThenInclude(ad => ad!.MetaverseAttribute).
            Include(t => t.ObjectTypes).
            ThenInclude(o => o.TemplateAttributes).
            ThenInclude(ta => ta.ExampleDataSetInstances).
            ThenInclude(edsi => edsi.ExampleDataSet);

        var t = await q.SingleOrDefaultAsync(t => t.Id == id);
        if (t == null)
            return null;

        SortExampleDataSetInstances(t);
        return t;
    }

    public async Task<ExampleDataTemplateHeader?> GetTemplateHeaderAsync(int id)
    {
        await using var db = new JimDbContext();
        return await db.ExampleDataTemplates.Select(dgt => new ExampleDataTemplateHeader
        {
            Name = dgt.Name,
            BuiltIn = dgt.BuiltIn,
            Created = dgt.Created,
            Id = dgt.Id
        }).SingleOrDefaultAsync(q => q.Id == id);
    }

    public async Task CreateTemplateAsync(ExampleDataTemplate template)
    {
        Repository.Database.ExampleDataTemplates.Add(template);
        await Repository.Database.SaveChangesAsync();
    }

    public async Task UpdateTemplateAsync(ExampleDataTemplate template)
    {
        await Repository.Database.SaveChangesAsync();
    }

    public async Task DeleteTemplateAsync(int templateId)
    {
        var template = await Repository.Database.ExampleDataTemplates.
            Include(t => t.ObjectTypes).
            ThenInclude(ot => ot.TemplateAttributes).
            AsTracking().
            SingleOrDefaultAsync(t => t.Id == templateId);
        if (template == null)
        {
            Log.Warning("DeleteTemplateAsync: No such template found to delete.");
            return;
        }

        // Null out the FK reference in Activities to preserve audit history
        await Repository.Database.Database.ExecuteSqlRawAsync(
            @"UPDATE ""Activities"" SET ""ExampleDataTemplateId"" = NULL WHERE ""ExampleDataTemplateId"" = {0}",
            templateId);

        // go through the template tree and remove all descendant template objects
        // cascade delete not used here due to references to non-template objects we definately don't want to delete
        foreach (var objectType in template.ObjectTypes)
            Repository.Database.ExampleDataTemplateAttributes.RemoveRange(objectType.TemplateAttributes);

        Repository.Database.ExampleDataObjectTypes.RemoveRange(template.ObjectTypes);
        Repository.Database.ExampleDataTemplates.Remove(template);
        await Repository.Database.SaveChangesAsync();
    }
    #endregion

    /// <summary>
    /// Bulk creates Metaverse Objects in the database using batched, COPY-based persistence.
    /// Each batch streams MVOs, attribute values, and (if change tracking is enabled) change-history
    /// records to PostgreSQL via Npgsql binary COPY, mirroring the worker hot-path pattern documented
    /// in <c>src/CLAUDE.md</c>. EF Core is bypassed entirely on the write path so neither the change
    /// tracker nor parameterised INSERTs are in the way of throughput at scale.
    /// </summary>
    /// <param name="metaverseObjects">The list of MetaverseObjects to persist.</param>
    /// <param name="batchSize">Number of objects to persist per batch. Smaller batches reduce memory pressure and improve cancellation responsiveness.</param>
    /// <param name="cancellationToken">The cancellation token to use to determine if the operation should be cancelled.</param>
    /// <param name="progressCallback">
    /// Optional callback fired once per batch with a <see cref="PersistenceProgress"/> payload so callers
    /// can render moving "what's happening" messages (batch X of Y, ETA, etc.) on the Activity record.
    /// </param>
    /// <returns>The number of objects persisted.</returns>
    /// <exception cref="ArgumentNullException"></exception>
    /// <exception cref="OperationCanceledException"></exception>
    public async Task<int> CreateMetaverseObjectsAsync(
        List<MetaverseObject> metaverseObjects,
        int batchSize,
        CancellationToken cancellationToken,
        Func<PersistenceProgress, Task>? progressCallback = null)
    {
        if (metaverseObjects == null || metaverseObjects.Count == 0)
            throw new ArgumentNullException(nameof(metaverseObjects));

        if (batchSize <= 0)
            batchSize = 500; // Sensible default

        var totalObjects = metaverseObjects.Count;
        var batchTotal = (totalObjects + batchSize - 1) / batchSize;
        Log.Information("CreateMetaverseObjectsAsync: Starting COPY-based persist of {Count:N0} MetaverseObjects in {BatchTotal:N0} batch(es) of {BatchSize}...",
            totalObjects, batchTotal, batchSize);

        // Reuse the proven COPY-based bulk persistence on SyncRepository. Constructing a peer
        // SyncRepository against the same PostgresDataRepository is the established pattern
        // (see Worker.cs, SyncImportTaskProcessor, etc.). This keeps the per-table COPY logic
        // in one place and gives example-data persistence the same throughput characteristics
        // as the production sync hot path.
        var syncRepo = new SyncRepository(Repository);

        var totalPersisted = 0;
        var batchIndex = 0;
        var stopwatch = Stopwatch.StartNew();

        for (var offset = 0; offset < totalObjects; offset += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            batchIndex++;

            var batchCount = Math.Min(batchSize, totalObjects - offset);
            var batch = metaverseObjects.GetRange(offset, batchCount);

            // Phase 1: COPY the MVOs and their attribute values.
            // CreateMetaverseObjectsBulkAsync internally fixes up ReferenceValueId FKs from
            // navigation properties, so cross-batch references (e.g. manager links from the
            // User template's binary tree) resolve correctly because earlier batches have
            // already committed.
            await syncRepo.CreateMetaverseObjectsBulkAsync(batch);

            // Phase 2: COPY the change-history records, if any. Example data generation either
            // attaches a single Created change per MVO (when MVO change tracking is enabled) or
            // none at all (when disabled), so we just collect whatever the server layer set.
            var changeRecords = batch.SelectMany(mvo => mvo.Changes).ToList();
            if (changeRecords.Count > 0)
                await syncRepo.PersistPendingMvoChangesAsync(changeRecords, []);

            // The bulk persistence reattaches MVOs to the EF change tracker as Unchanged so
            // downstream sync code can discover navigation children. Example data has no
            // downstream EF work after persistence: drop the tracker so memory stays bounded
            // across batches.
            Repository.Database.ChangeTracker.Clear();

            totalPersisted += batchCount;

            Log.Information(
                "CreateMetaverseObjectsAsync: Persisted batch {BatchIndex:N0}/{BatchTotal:N0} ({Persisted:N0}/{Total:N0} objects, {ChangeCount:N0} change records) in {Elapsed}",
                batchIndex, batchTotal, totalPersisted, totalObjects, changeRecords.Count, stopwatch.Elapsed);

            if (progressCallback != null)
            {
                await progressCallback(new PersistenceProgress
                {
                    TotalObjects = totalObjects,
                    ObjectsPersisted = totalPersisted,
                    BatchIndex = batchIndex,
                    BatchCount = batchTotal,
                    Elapsed = stopwatch.Elapsed
                });
            }
        }

        stopwatch.Stop();
        Log.Information("CreateMetaverseObjectsAsync: Done - persisted {Count:N0} objects in {Elapsed}", totalPersisted, stopwatch.Elapsed);
        return totalPersisted;
    }

    #region private methods
    private static void SortExampleDataSetInstances(ExampleDataTemplate template)
    {
        foreach (var ta in template.ObjectTypes.SelectMany(ot => ot.TemplateAttributes))
            if (ta.ExampleDataSetInstances is { Count: > 0 })
                ta.ExampleDataSetInstances = ta.ExampleDataSetInstances.OrderBy(q => q.Order).ToList();
    }
    #endregion
}
