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

    #region predefined search criteria groups

    /// <summary>
    /// Retrieves a single criteria group with its criteria (and their attributes) and immediate child groups.
    /// </summary>
    public Task<PredefinedSearchCriteriaGroup?> GetPredefinedSearchCriteriaGroupAsync(int groupId);

    /// <summary>
    /// Creates a new criteria group, attached either to the predefined search (top-level) when
    /// <paramref name="parentGroupId"/> is null, or to a parent group (nested) otherwise.
    /// Returns the created group with its database identifier populated.
    /// </summary>
    public Task<PredefinedSearchCriteriaGroup> CreatePredefinedSearchCriteriaGroupAsync(int predefinedSearchId, int? parentGroupId, SearchGroupType type, int position);

    /// <summary>
    /// Updates a criteria group's logic type and position. Returns the updated group, or null if it does not exist.
    /// </summary>
    public Task<PredefinedSearchCriteriaGroup?> UpdatePredefinedSearchCriteriaGroupAsync(int groupId, SearchGroupType type, int position);

    /// <summary>
    /// Deletes a criteria group and its entire subtree (nested groups and all contained criteria).
    /// Returns false if the group does not exist. The foreign keys use NO ACTION, so the subtree is removed explicitly.
    /// </summary>
    public Task<bool> DeletePredefinedSearchCriteriaGroupAsync(int groupId);

    #endregion

    #region predefined search criteria

    /// <summary>
    /// Retrieves a single criterion with its Metaverse attribute.
    /// </summary>
    public Task<PredefinedSearchCriteria?> GetPredefinedSearchCriterionAsync(int criterionId);

    /// <summary>
    /// Adds a criterion to a criteria group. The criterion's MetaverseAttributeId must be set; the
    /// MetaverseAttribute navigation is ignored to avoid re-inserting an existing attribute.
    /// Returns the created criterion with its database identifier populated, or null if the group does not exist.
    /// </summary>
    public Task<PredefinedSearchCriteria?> CreatePredefinedSearchCriterionAsync(int groupId, PredefinedSearchCriteria criterion);

    /// <summary>
    /// Updates an existing criterion's comparison operator, attribute and typed value carriers.
    /// Returns the updated criterion, or null if it does not exist.
    /// </summary>
    public Task<PredefinedSearchCriteria?> UpdatePredefinedSearchCriterionAsync(PredefinedSearchCriteria criterion);

    /// <summary>
    /// Deletes a criterion. Returns false if it does not exist.
    /// </summary>
    public Task<bool> DeletePredefinedSearchCriterionAsync(int criterionId);

    #endregion

    #region owning search resolution

    /// <summary>
    /// Resolves the id of the Predefined Search that owns a given criteria group. A top-level group carries its
    /// owning search's id directly; a nested group carries only its parent group's id, so this walks up the
    /// <see cref="PredefinedSearchCriteriaGroup.ParentGroup"/> chain until a group with a
    /// <see cref="PredefinedSearchCriteriaGroup.PredefinedSearchId"/> is found. Used to roll up a nested criteria
    /// group/criterion change to the owning search's configuration change history. Returns null if the group does
    /// not exist.
    /// </summary>
    public Task<int?> GetOwningPredefinedSearchIdForGroupAsync(int groupId);

    /// <summary>
    /// Resolves the id of the Predefined Search that owns a given criterion, by resolving its containing group via
    /// <see cref="GetOwningPredefinedSearchIdForGroupAsync"/>. Returns null if the criterion does not exist.
    /// </summary>
    public Task<int?> GetOwningPredefinedSearchIdForCriterionAsync(int criterionId);

    #endregion
}
