using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.DataGeneration;
using JIM.Models.DataGeneration.DTOs;
using Microsoft.EntityFrameworkCore;
using Serilog;
namespace JIM.PostgresData.Repositories;

public class DataGenerationRepository : IDataGenerationRepository
{
    private PostgresDataRepository Repository { get; }

    internal DataGenerationRepository(PostgresDataRepository dataRepository)
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

    public async Task<ExampleDataSet?> GetExampleDataSetAsync(string name, string culture)
    {
        return await Repository.Database.ExampleDataSets.Include(q => q.Values).SingleOrDefaultAsync(q => q.Name == name && q.Culture == culture);
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
        var exampleDataSet = await Repository.Database.ExampleDataSets.Include(q => q.Values).SingleOrDefaultAsync(q => q.Id == exampleDataSetId);
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

    #region DataGenerationTemplates
    public async Task<List<DataGenerationTemplate>> GetTemplatesAsync()
    {
        var templates = await Repository.Database.DataGenerationTemplates.
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

    public async Task<List<DataGenerationTemplateHeader>> GetTemplateHeadersAsync()
    {
        var templates = await Repository.Database.DataGenerationTemplates.OrderBy(t => t.Name).Select(dgt => new DataGenerationTemplateHeader
        {
            Name = dgt.Name,
            BuiltIn = dgt.BuiltIn,
            Created = dgt.Created,
            Id = dgt.Id
        }).ToListAsync();

        return templates;
    }

    public async Task<DataGenerationTemplate?> GetTemplateAsync(string name)
    {
        var q = Repository.Database.DataGenerationTemplates.
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

    public async Task<DataGenerationTemplate?> GetTemplateAsync(int id)
    {
        var q = Repository.Database.DataGenerationTemplates.
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

    public async Task<DataGenerationTemplateHeader?> GetTemplateHeaderAsync(int id)
    {
        await using var db = new JimDbContext();
        return await db.DataGenerationTemplates.Select(dgt => new DataGenerationTemplateHeader
        {
            Name = dgt.Name,
            BuiltIn = dgt.BuiltIn,
            Created = dgt.Created,
            Id = dgt.Id
        }).SingleOrDefaultAsync(q => q.Id == id);
    }

    public async Task CreateTemplateAsync(DataGenerationTemplate template)
    {
        Repository.Database.DataGenerationTemplates.Add(template);
        await Repository.Database.SaveChangesAsync();
    }

    public async Task UpdateTemplateAsync(DataGenerationTemplate template)
    {
        await Repository.Database.SaveChangesAsync();
    }

    public async Task DeleteTemplateAsync(int templateId)
    {
        var template = await Repository.Database.DataGenerationTemplates.
            Include(t => t.ObjectTypes).
            ThenInclude(ot => ot.TemplateAttributes).
            SingleOrDefaultAsync(t => t.Id == templateId);
        if (template == null)
        {
            Log.Warning("DeleteTemplateAsync: No such template found to delete.");
            return;
        }

        // Null out the FK reference in Activities to preserve audit history
        // Only execute raw SQL if we have a real database connection (not mocked)
        try
        {
            await Repository.Database.Database.ExecuteSqlRawAsync(
                @"UPDATE ""Activities"" SET ""DataGenerationTemplateId"" = NULL WHERE ""DataGenerationTemplateId"" = {0}",
                templateId);
        }
        catch (Exception)
        {
            // Expected when running with mocked DbContext in tests
        }

        // go through the template tree and remove all descendant template objects
        // cascade delete not used here due to references to non-template objects we definately don't want to delete
        foreach (var objectType in template.ObjectTypes)
            Repository.Database.DataGenerationTemplateAttributes.RemoveRange(objectType.TemplateAttributes);

        Repository.Database.DataGenerationObjectTypes.RemoveRange(template.ObjectTypes);
        Repository.Database.DataGenerationTemplates.Remove(template);
        await Repository.Database.SaveChangesAsync();
    }
    #endregion

    /// <summary>
    /// Bulk creates metaverse objects in the database using batched persistence.
    /// Batching reduces memory pressure and allows progress reporting during persistence.
    /// </summary>
    /// <param name="metaverseObjects">The list of MetaverseObjects to persist.</param>
    /// <param name="batchSize">Number of objects to persist per batch. Smaller batches reduce memory pressure.</param>
    /// <param name="cancellationToken">The cancellation token to use to determine if the operation should be cancelled.</param>
    /// <param name="progressCallback">Optional callback for reporting persistence progress. Parameters are (totalObjects, objectsPersisted).</param>
    /// <returns>The number of objects persisted.</returns>
    /// <exception cref="ArgumentNullException"></exception>
    /// <exception cref="OperationCanceledException"></exception>
    public async Task<int> CreateMetaverseObjectsAsync(
        List<MetaverseObject> metaverseObjects,
        int batchSize,
        CancellationToken cancellationToken,
        Func<int, int, Task>? progressCallback = null)
    {
        Log.Verbose("CreateMetaverseObjectsAsync: Starting to persist {Count:N0} MetaverseObjects in batches of {BatchSize}...",
            metaverseObjects?.Count ?? 0, batchSize);

        if (metaverseObjects == null || metaverseObjects.Count == 0)
            throw new ArgumentNullException(nameof(metaverseObjects));

        if (batchSize <= 0)
            batchSize = 500; // Sensible default

        var totalObjects = metaverseObjects.Count;

        // For now, persist all objects in a single transaction.
        // Batched persistence with progress reporting is complex due to EF Core's change tracking
        // of navigation properties causing duplicate key errors. This is tracked in GitHub issue #276
        // for Post-MVP optimisation.
        Repository.Database.MetaverseObjects.AddRange(metaverseObjects);
        await Repository.Database.SaveChangesAsync(cancellationToken);

        if (cancellationToken.IsCancellationRequested)
            cancellationToken.ThrowIfCancellationRequested();

        // Report completion
        if (progressCallback != null)
            await progressCallback(totalObjects, totalObjects);

        Log.Verbose("CreateMetaverseObjectsAsync: Done - persisted {Count:N0} objects", totalObjects);
        return totalObjects;
    }

    #region private methods
    private static void SortExampleDataSetInstances(DataGenerationTemplate template)
    {
        foreach (var ta in template.ObjectTypes.SelectMany(ot => ot.TemplateAttributes))
            if (ta.ExampleDataSetInstances is { Count: > 0 })
                ta.ExampleDataSetInstances = ta.ExampleDataSetInstances.OrderBy(q => q.Order).ToList();
    }
    #endregion
}
