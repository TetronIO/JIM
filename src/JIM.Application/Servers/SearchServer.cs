// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Search;
using JIM.Models.Search.DTOs;
namespace JIM.Application.Servers;

public class SearchServer
{
    #region accessors
    private JimApplication Application { get; }
    #endregion

    #region constructors
    internal SearchServer(JimApplication application)
    {
        Application = application;
    }
    #endregion

    #region predefined searches
    public async Task<IList<PredefinedSearchHeader>> GetPredefinedSearchHeadersAsync()
    {
        return await Application.Repository.Search.GetPredefinedSearchHeadersAsync();
    }

    /// <summary>
    /// Full retrieval of a predefined search by ID, including the attributes and criteria graph.
    /// Use for read-only display where the full graph is needed (e.g. an API GET).
    /// </summary>
    public async Task<PredefinedSearch?> GetPredefinedSearchAsync(int id)
    {
        var predefinedSearch = await Application.Repository.Search.GetPredefinedSearchAsync(id);
        if (predefinedSearch != null)
            predefinedSearch = PostProcessPredefinedSearch(predefinedSearch);

        return predefinedSearch;
    }

    public async Task<PredefinedSearch?> GetPredefinedSearchAsync(string uri)
    {
        var predefinedSearch = await Application.Repository.Search.GetPredefinedSearchAsync(uri);
        if (predefinedSearch != null)
            predefinedSearch = PostProcessPredefinedSearch(predefinedSearch);

        return predefinedSearch;
    }

    /// <summary>
    /// Attempts to retrieve a default predefined search for a given metaverse object type
    /// </summary>
    public async Task<PredefinedSearch?> GetPredefinedSearchAsync(MetaverseObjectType metaverseObjectType)
    {
        var predefinedSearch = await Application.Repository.Search.GetPredefinedSearchAsync(metaverseObjectType);
        if (predefinedSearch != null)
            predefinedSearch = PostProcessPredefinedSearch(predefinedSearch);

        return predefinedSearch;
    }

    /// <summary>
    /// Lightweight retrieval of a predefined search by ID, without the attributes or criteria graph.
    /// Use for write-path lookups where the expensive graph is not needed.
    /// </summary>
    public async Task<PredefinedSearch?> GetPredefinedSearchCoreAsync(int id)
    {
        return await Application.Repository.Search.GetPredefinedSearchCoreAsync(id);
    }

    /// <summary>
    /// Persists changes to a predefined search entity.
    /// </summary>
    public async Task UpdatePredefinedSearchAsync(PredefinedSearch predefinedSearch)
    {
        await Application.Repository.Search.UpdatePredefinedSearchAsync(predefinedSearch);
    }

    private static PredefinedSearch PostProcessPredefinedSearch(PredefinedSearch predefinedSearch)
    {
        predefinedSearch.Attributes = predefinedSearch.Attributes.OrderBy(q => q.Position).ToList();
        return predefinedSearch;
    }
    #endregion
}