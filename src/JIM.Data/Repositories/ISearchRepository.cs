// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Search;
using JIM.Models.Search.DTOs;
namespace JIM.Data.Repositories;

public interface ISearchRepository
{
    public Task<IList<PredefinedSearchHeader>> GetPredefinedSearchHeadersAsync();

    public Task<PredefinedSearch?> GetPredefinedSearchAsync(int id);

    public Task<PredefinedSearch?> GetPredefinedSearchAsync(string uri);

    public Task<PredefinedSearch?> GetPredefinedSearchAsync(MetaverseObjectType metaverseObjectType);

    /// <summary>
    /// Lightweight retrieval of a predefined search by ID, without the attributes or criteria graph.
    /// Intended for write-path lookups (e.g. a PATCH endpoint loading the entity before mutation).
    /// </summary>
    public Task<PredefinedSearch?> GetPredefinedSearchCoreAsync(int id);

    /// <summary>
    /// Persists changes to a predefined search entity. Attaches detached entities and marks all
    /// scalar fields as modified, matching the repository convention used for Schedules, API Keys, etc.
    /// </summary>
    public Task UpdatePredefinedSearchAsync(PredefinedSearch predefinedSearch);
}
