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
    /// Attempts to retrieve a default predefined search for a given Metaverse Object Type
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

    #region predefined search criteria groups
    /// <summary>
    /// Retrieves a single criteria group with its criteria (and their attributes) and immediate child groups.
    /// </summary>
    public async Task<PredefinedSearchCriteriaGroup?> GetPredefinedSearchCriteriaGroupAsync(int groupId)
    {
        return await Application.Repository.Search.GetPredefinedSearchCriteriaGroupAsync(groupId);
    }

    /// <summary>
    /// Creates a new criteria group, attached to the predefined search (top-level) or to a parent group (nested).
    /// </summary>
    public async Task<PredefinedSearchCriteriaGroup> CreatePredefinedSearchCriteriaGroupAsync(int predefinedSearchId, int? parentGroupId, SearchGroupType type, int position)
    {
        return await Application.Repository.Search.CreatePredefinedSearchCriteriaGroupAsync(predefinedSearchId, parentGroupId, type, position);
    }

    /// <summary>
    /// Updates a criteria group's logic type and position. Returns null if the group does not exist.
    /// </summary>
    public async Task<PredefinedSearchCriteriaGroup?> UpdatePredefinedSearchCriteriaGroupAsync(int groupId, SearchGroupType type, int position)
    {
        return await Application.Repository.Search.UpdatePredefinedSearchCriteriaGroupAsync(groupId, type, position);
    }

    /// <summary>
    /// Deletes a criteria group and its entire subtree. Returns false if the group does not exist.
    /// </summary>
    public async Task<bool> DeletePredefinedSearchCriteriaGroupAsync(int groupId)
    {
        return await Application.Repository.Search.DeletePredefinedSearchCriteriaGroupAsync(groupId);
    }
    #endregion

    #region predefined search criteria
    /// <summary>
    /// Retrieves a single criterion with its Metaverse attribute.
    /// </summary>
    public async Task<PredefinedSearchCriteria?> GetPredefinedSearchCriterionAsync(int criterionId)
    {
        return await Application.Repository.Search.GetPredefinedSearchCriterionAsync(criterionId);
    }

    /// <summary>
    /// Adds a criterion to a criteria group. Returns null if the group does not exist.
    /// </summary>
    public async Task<PredefinedSearchCriteria?> CreatePredefinedSearchCriterionAsync(int groupId, PredefinedSearchCriteria criterion)
    {
        return await Application.Repository.Search.CreatePredefinedSearchCriterionAsync(groupId, criterion);
    }

    /// <summary>
    /// Updates an existing criterion. Returns null if it does not exist.
    /// </summary>
    public async Task<PredefinedSearchCriteria?> UpdatePredefinedSearchCriterionAsync(PredefinedSearchCriteria criterion)
    {
        return await Application.Repository.Search.UpdatePredefinedSearchCriterionAsync(criterion);
    }

    /// <summary>
    /// Deletes a criterion. Returns false if it does not exist.
    /// </summary>
    public async Task<bool> DeletePredefinedSearchCriterionAsync(int criterionId)
    {
        return await Application.Repository.Search.DeletePredefinedSearchCriterionAsync(criterionId);
    }
    #endregion
}