// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.ExampleData;
using JIM.Models.ExampleData.DTOs;
namespace JIM.Data.Repositories;

public interface IExampleDataRepository
{
    public Task<List<ExampleDataSet>> GetExampleDataSetsAsync();
    public Task<List<ExampleDataSetHeader>> GetExampleDataSetHeadersAsync();
    public Task<ExampleDataSet?> GetExampleDataSetAsync(string name, string culture, bool withChangeTracking = false);
    public Task<ExampleDataSet?> GetExampleDataSetAsync(int id);
    public Task CreateExampleDataSetAsync(ExampleDataSet exampleDataSet);
    public Task UpdateExampleDataSetAsync(ExampleDataSet exampleDataSet);
    public Task DeleteExampleDataSetAsync(int exampleDataSetId);

    public Task<List<ExampleDataTemplate>> GetTemplatesAsync();
    public Task<List<ExampleDataTemplateHeader>> GetTemplateHeadersAsync();
    /// <summary>
    /// Retrieves a Data Generation Template.
    /// </summary>
    /// <param name="name">The name of the template to retrieve</param>
    public Task<ExampleDataTemplate?> GetTemplateAsync(string name);
    /// <summary>
    /// Retrieves a Data Generation Template.
    /// </summary>
    /// <param name="id">The id of the template to retrieve</param>
    public Task<ExampleDataTemplate?> GetTemplateAsync(int id);
    public Task<ExampleDataTemplateHeader?> GetTemplateHeaderAsync(int id);
    public Task CreateTemplateAsync(ExampleDataTemplate template);

    /// <summary>
    /// Persists a newly-built template graph whose navigation properties reference entities that already exist in the
    /// database (Metaverse Object Types, Metaverse Attributes, Example Data Sets and their values). Existing entities
    /// (those with a key already set) are tracked as Unchanged so EF inserts only the new template, object type,
    /// attribute and child rows and wires foreign keys to the existing entities rather than trying to re-insert them.
    /// </summary>
    public Task CreateTemplateGraphAsync(ExampleDataTemplate template);

    public Task UpdateTemplateAsync(ExampleDataTemplate template);
    public Task DeleteTemplateAsync(int templateId);

    /// <summary>
    /// Bulk creates Metaverse Objects in the database using batched persistence.
    /// </summary>
    /// <param name="metaverseObjects">The list of MetaverseObjects to persist.</param>
    /// <param name="batchSize">Number of objects to persist per batch. Smaller batches reduce memory pressure.</param>
    /// <param name="cancellationToken">The cancellation token to use to determine if the operation should be cancelled.</param>
    /// <param name="progressCallback">
    /// Optional callback fired once per batch with a <see cref="PersistenceProgress"/> payload describing
    /// total / persisted object counts, batch index / count, and elapsed persistence time so callers
    /// can derive an ETA.
    /// </param>
    /// <returns>The number of objects persisted.</returns>
    public Task<int> CreateMetaverseObjectsAsync(
        List<MetaverseObject> metaverseObjects,
        int batchSize,
        CancellationToken cancellationToken,
        Func<PersistenceProgress, Task>? progressCallback = null);
}