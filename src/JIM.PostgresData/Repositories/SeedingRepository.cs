// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.ExampleData;
using JIM.Models.Search;
using JIM.Models.Security;
using Microsoft.EntityFrameworkCore;
using Serilog;
namespace JIM.PostgresData.Repositories;

public class SeedingRepository : ISeedingRepository
{
    private PostgresDataRepository Repository { get; }

    internal SeedingRepository(PostgresDataRepository dataRepository)
    {
        Repository = dataRepository;
    }

    /// <summary>
    /// Creates all seed data in a single transaction.
    /// ServiceSettings is created LAST to ensure atomicity - if the process crashes during seeding,
    /// the absence of ServiceSettings will trigger a fresh seeding attempt on restart.
    /// Every list must hold only objects that do not exist yet; the caller performs those existence checks. The one
    /// exception is ServiceSettings, which is guarded here so a re-run of the whole seed cannot create a second one.
    /// </summary>
    public async Task SeedDataAsync(
        List<MetaverseAttribute> metaverseAttributes,
        List<MetaverseObjectType> metaverseObjectTypes,
        List<PredefinedSearch> predefinedSearches,
        List<ExampleDataSet> exampleDataSets,
        List<ExampleDataTemplate> dataGenerationTemplates)
    {
        if (metaverseAttributes.Count > 0)
        {
            Repository.Database.MetaverseAttributes.AddRange(metaverseAttributes);
            Log.Information($"SeedDataAsync: Created {metaverseAttributes.Count} MetaverseAttributes");
        }

        if (metaverseObjectTypes.Count > 0)
        {
            Repository.Database.MetaverseObjectTypes.AddRange(metaverseObjectTypes);
            Log.Information($"SeedDataAsync: Created {metaverseObjectTypes.Count} MetaverseObjectTypes");
        }

        if (predefinedSearches.Count > 0)
        {
            Repository.Database.PredefinedSearches.AddRange(predefinedSearches);
            Log.Information($"SeedDataAsync: Created {predefinedSearches.Count} PredefinedSearches");
        }

        if (exampleDataSets.Count > 0)
        {
            Repository.Database.ExampleDataSets.AddRange(exampleDataSets);
            Log.Information($"SeedDataAsync: Created {exampleDataSets.Count} ExampleDataSets");
        }

        if (dataGenerationTemplates.Count > 0)
        {
            Repository.Database.ExampleDataTemplates.AddRange(dataGenerationTemplates);
            Log.Information($"SeedDataAsync: Created {dataGenerationTemplates.Count} ExampleDataTemplates");
        }

        // CRITICAL: ServiceSettings is created LAST in the same transaction.
        // This ensures that if the process crashes during seeding, ServiceSettings won't exist,
        // and the next startup will retry seeding from scratch.
        // This prevents a race condition where JIM.Web sees ServiceSettings exists but MetaverseAttributes don't.
        // Guarded rather than unconditional so this method is safe to re-run against a database that already holds
        // it, keeping the whole seed idempotent (issue #1287) rather than relying on the caller's short-circuit.
        if (!await Repository.Database.ServiceSettings.AnyAsync())
        {
            Repository.Database.ServiceSettings.Add(new ServiceSettings());
            Log.Information("SeedDataAsync: Created ServiceSettings");
        }

        await Repository.Database.SaveChangesAsync();
        Log.Information("SeedDataAsync: All seed data committed successfully");
    }

    /// <summary>
    /// Persists the built-in schema synchronisation pass's changes in a single transaction: the given
    /// newly-created built-in Metaverse Attributes, plus any pending modifications to change-tracked entities the
    /// pass loaded (new Object Type bindings, Standard Mapping additions and removals).
    /// </summary>
    public async Task SaveBuiltInSchemaChangesAsync(List<MetaverseAttribute> newMetaverseAttributes)
    {
        if (newMetaverseAttributes.Count > 0)
        {
            Repository.Database.MetaverseAttributes.AddRange(newMetaverseAttributes);
            Log.Information($"SaveBuiltInSchemaChangesAsync: Creating {newMetaverseAttributes.Count} built-in MetaverseAttributes");
        }

        await Repository.Database.SaveChangesAsync();
        Log.Information("SaveBuiltInSchemaChangesAsync: Built-in schema changes committed successfully");
    }
}
