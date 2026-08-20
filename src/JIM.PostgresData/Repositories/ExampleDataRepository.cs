// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.ExampleData;
using JIM.Models.ExampleData.DTOs;
using JIM.Models.Staging;
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
    public async Task<List<ExampleDataSet>> GetExampleDataSetsAsync(bool withChangeTracking = false)
    {
        IQueryable<ExampleDataSet> query = Repository.Database.ExampleDataSets.Include(q => q.Values);
        if (withChangeTracking)
            query = query.AsTracking();

        return await query.OrderBy(q => q.Name).ToListAsync();
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
        var exampleDataSet = await Repository.Database.ExampleDataSets.AsTracking().SingleOrDefaultAsync(q => q.Id == exampleDataSetId);
        if (exampleDataSet == null)
        {
            Log.Warning("DeleteExampleDataSetAsync: No such ExampleDetaSet found to delete.");
            return;
        }

        // The values go with the set: the foreign key cascades (#1477), so they need neither loading nor
        // removing by hand.
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
        // Reads through the repository's own context. This used to open a JimDbContext of its own,
        // which takes a second pooled connection and configures it from environment variables
        // rather than from whatever the caller was already working against.
        var db = Repository.Database;
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
        // A submitted template graph always references already-persisted entities (Metaverse Object Types, Metaverse
        // Attributes, Connected System Object Type Attributes and Example Data Sets), so creation is graph-safe by
        // definition; there is no separate "plain insert" case to preserve.
        await CreateTemplateGraphAsync(template);
    }

    public async Task CreateTemplateGraphAsync(ExampleDataTemplate template)
    {
        // Insert the new template subtree while treating any already-persisted entities it references (Metaverse Object
        // Types, Metaverse Attributes, Example Data Sets and their values) as existing rather than new. TrackGraph walks
        // the whole graph (including the many-to-many reference rows) and, keyed on whether the entity already has its
        // primary key set, marks existing entities Unchanged and new ones Added. This lets EF wire foreign keys to the
        // existing entities instead of attempting to re-insert them (which would violate their primary keys).
        Repository.Database.ChangeTracker.TrackGraph(template, node =>
            node.Entry.State = node.Entry.IsKeySet ? EntityState.Unchanged : EntityState.Added);
        await Repository.Database.SaveChangesAsync();
    }

    public async Task UpdateTemplateAsync(ExampleDataTemplate template, bool replaceObjectTypes)
    {
        // The hosts that serve the portal, REST API and PowerShell all run the context NoTracking, and no template
        // retrieval returns a tracked entity, so the incoming template is always detached: mutating it and calling
        // SaveChangesAsync would write nothing at all. Load the persisted template tracked and copy onto it instead.
        var trackedTemplate = await Repository.Database.ExampleDataTemplates.
            Include(t => t.ObjectTypes).
            ThenInclude(ot => ot.TemplateAttributes).
            ThenInclude(ta => ta.AttributeDependency).
            AsTracking().
            SingleOrDefaultAsync(t => t.Id == template.Id);

        if (trackedTemplate == null)
            throw new InvalidOperationException($"UpdateTemplateAsync: No Data Generation Template exists with id {template.Id}.");

        trackedTemplate.Name = template.Name;
        trackedTemplate.LastUpdated = template.LastUpdated;
        trackedTemplate.LastUpdatedByType = template.LastUpdatedByType;
        trackedTemplate.LastUpdatedById = template.LastUpdatedById;
        trackedTemplate.LastUpdatedByName = template.LastUpdatedByName;

        if (replaceObjectTypes)
            await ReplaceTemplateObjectTypesAsync(trackedTemplate, template);

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
    /// <summary>
    /// Replaces a tracked Data Generation Template's Object Types (and everything below them) with the submitted graph:
    /// the superseded template-owned rows are explicitly deleted, and the incoming subtree is re-pointed at the
    /// persisted entities it references so those are never re-inserted or modified.
    /// </summary>
    private async Task ReplaceTemplateObjectTypesAsync(ExampleDataTemplate trackedTemplate, ExampleDataTemplate incomingTemplate)
    {
        var database = Repository.Database;

        // Go through the superseded template tree and remove all descendant template objects. Cascade delete is
        // deliberately not used here (as in DeleteTemplateAsync) because the tree references non-template objects we
        // definately don't want to delete. Attribute dependencies are principals of the attribute row, so removing
        // the attribute alone would orphan them.
        foreach (var objectType in trackedTemplate.ObjectTypes)
        {
            var dependencies = objectType.TemplateAttributes.Where(ta => ta.AttributeDependency != null).Select(ta => ta.AttributeDependency!);
            database.ExampleDataTemplateAttributeDependencies.RemoveRange(dependencies);
            database.ExampleDataTemplateAttributes.RemoveRange(objectType.TemplateAttributes);
        }

        database.ExampleDataObjectTypes.RemoveRange(trackedTemplate.ObjectTypes);
        trackedTemplate.ObjectTypes.Clear();

        // Re-point the incoming subtree at freshly-loaded, tracked instances of the entities it references. The
        // submitted graph's own instances are detached copies loaded elsewhere, often carrying navigation collections
        // of their own (a Metaverse Object Type usually arrives with its Attributes loaded), and attaching those would
        // have EF re-insert the many-to-many bindings between them. Loading each referenced entity by key gives one
        // canonical tracked instance per key, so an entity referenced twice (a Metaverse Object Type that is both an
        // Object Type's target and a reference attribute's permitted type) attaches exactly once.
        var references = new PersistedTemplateReferences(database);
        foreach (var objectType in incomingTemplate.ObjectTypes)
        {
            // the template-owned rows are all new inserts; any ids the submitted graph carries belong to the rows just
            // removed above, and reusing them would collide with those deletions.
            objectType.Id = 0;
            objectType.MetaverseObjectType = await references.ResolveMetaverseObjectTypeAsync(objectType.MetaverseObjectType);

            foreach (var attribute in objectType.TemplateAttributes)
            {
                attribute.Id = 0;

                if (attribute.MetaverseAttribute != null)
                    attribute.MetaverseAttribute = await references.ResolveMetaverseAttributeAsync(attribute.MetaverseAttribute);

                if (attribute.ConnectedSystemObjectTypeAttribute != null)
                    attribute.ConnectedSystemObjectTypeAttribute = await references.ResolveConnectedSystemObjectTypeAttributeAsync(attribute.ConnectedSystemObjectTypeAttribute);

                foreach (var instance in attribute.ExampleDataSetInstances)
                {
                    instance.Id = 0;
                    instance.ExampleDataSet = await references.ResolveExampleDataSetAsync(instance.ExampleDataSet);
                }

                if (attribute.WeightedStringValues != null)
                    foreach (var weightedValue in attribute.WeightedStringValues)
                        weightedValue.Id = 0;

                if (attribute.ReferenceMetaverseObjectTypes != null)
                    for (var i = 0; i < attribute.ReferenceMetaverseObjectTypes.Count; i++)
                        attribute.ReferenceMetaverseObjectTypes[i] = await references.ResolveMetaverseObjectTypeAsync(attribute.ReferenceMetaverseObjectTypes[i]);

                if (attribute.AttributeDependency != null)
                {
                    attribute.AttributeDependency.Id = 0;
                    attribute.AttributeDependency.MetaverseAttribute = await references.ResolveMetaverseAttributeAsync(attribute.AttributeDependency.MetaverseAttribute);
                }
            }

            // Adding the new subtree to the tracked template's collection is what marks it for insertion: EF's change
            // detection walks it and stops at every entity already tracked (the referenced entities resolved above),
            // so only the genuinely new template-owned rows are inserted.
            trackedTemplate.ObjectTypes.Add(objectType);
        }
    }

    /// <summary>
    /// Loads and caches the persisted entities a submitted Data Generation Template graph references, so each is
    /// tracked exactly once (as Unchanged) however many times the graph mentions it.
    /// </summary>
    private sealed class PersistedTemplateReferences
    {
        private readonly JimDbContext _database;
        private readonly Dictionary<int, MetaverseObjectType> _metaverseObjectTypes = new();
        private readonly Dictionary<int, MetaverseAttribute> _metaverseAttributes = new();
        private readonly Dictionary<int, ConnectedSystemObjectTypeAttribute> _connectedSystemObjectTypeAttributes = new();
        private readonly Dictionary<int, ExampleDataSet> _exampleDataSets = new();

        internal PersistedTemplateReferences(JimDbContext database)
        {
            _database = database;
        }

        internal async Task<MetaverseObjectType> ResolveMetaverseObjectTypeAsync(MetaverseObjectType incoming) =>
            await ResolveAsync(_metaverseObjectTypes, incoming.Id, "Metaverse Object Type",
                id => _database.MetaverseObjectTypes.AsTracking().SingleOrDefaultAsync(t => t.Id == id));

        internal async Task<MetaverseAttribute> ResolveMetaverseAttributeAsync(MetaverseAttribute incoming) =>
            await ResolveAsync(_metaverseAttributes, incoming.Id, "Metaverse Attribute",
                id => _database.MetaverseAttributes.AsTracking().SingleOrDefaultAsync(a => a.Id == id));

        internal async Task<ConnectedSystemObjectTypeAttribute> ResolveConnectedSystemObjectTypeAttributeAsync(ConnectedSystemObjectTypeAttribute incoming) =>
            await ResolveAsync(_connectedSystemObjectTypeAttributes, incoming.Id, "Connected System Object Type Attribute",
                id => _database.ConnectedSystemAttributes.AsTracking().SingleOrDefaultAsync(a => a.Id == id));

        internal async Task<ExampleDataSet> ResolveExampleDataSetAsync(ExampleDataSet incoming) =>
            await ResolveAsync(_exampleDataSets, incoming.Id, "Example Data Set",
                id => _database.ExampleDataSets.AsTracking().SingleOrDefaultAsync(s => s.Id == id));

        private static async Task<T> ResolveAsync<T>(Dictionary<int, T> cache, int id, string entityDescription, Func<int, Task<T?>> loadAsync) where T : class
        {
            if (cache.TryGetValue(id, out var cached))
                return cached;

            var resolved = await loadAsync(id) ??
                throw new InvalidOperationException($"UpdateTemplateAsync: The submitted Data Generation Template references a {entityDescription} with id {id} that does not exist.");

            cache[id] = resolved;
            return resolved;
        }
    }

    private static void SortExampleDataSetInstances(ExampleDataTemplate template)
    {
        foreach (var ta in template.ObjectTypes.SelectMany(ot => ot.TemplateAttributes))
            if (ta.ExampleDataSetInstances is { Count: > 0 })
                ta.ExampleDataSetInstances = ta.ExampleDataSetInstances.OrderBy(q => q.Order).ToList();
    }
    #endregion
}
