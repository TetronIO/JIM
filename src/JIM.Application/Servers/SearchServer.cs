// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Search;
using JIM.Models.Search.DTOs;
using JIM.Models.Security;
using JIM.Utilities;
using Serilog;
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
    /// Persists changes to a predefined search entity and records a configuration-change Activity.
    /// </summary>
    public async Task UpdatePredefinedSearchAsync(PredefinedSearch predefinedSearch, MetaverseObject? initiatedBy = null, string? changeReason = null)
    {
        await UpdatePredefinedSearchCoreAsync(predefinedSearch, changeReason,
            activity => CreatePredefinedSearchActivityAsync(activity, initiatedBy));
    }

    /// <summary>
    /// Persists changes to a predefined search entity. API-key initiator overload.
    /// </summary>
    public async Task UpdatePredefinedSearchAsync(PredefinedSearch predefinedSearch, ApiKey initiatedByApiKey, string? changeReason = null)
    {
        await UpdatePredefinedSearchCoreAsync(predefinedSearch, changeReason,
            activity => Application.Activities.CreateActivityAsync(activity, initiatedByApiKey));
    }

    private async Task UpdatePredefinedSearchCoreAsync(PredefinedSearch predefinedSearch, string? changeReason, Func<Activity, Task> createActivityAsync)
    {
        var activity = new Activity
        {
            TargetName = predefinedSearch.Name,
            TargetType = ActivityTargetType.PredefinedSearch,
            TargetOperationType = ActivityTargetOperationType.Update,
            Message = $"Updating Predefined Search '{predefinedSearch.Name}'"
        };
        await createActivityAsync(activity);

        try
        {
            Log.Information("Updating Predefined Search '{Name}' (ID: {Id})", LogSanitiser.Sanitise(predefinedSearch.Name), predefinedSearch.Id);
            await Application.Repository.Search.UpdatePredefinedSearchAsync(predefinedSearch);

            await CapturePredefinedSearchConfigurationChangeAsync(activity, predefinedSearch.Id, changeReason);
            activity.Message = $"Updated Predefined Search '{predefinedSearch.Name}'";
            await Application.Activities.CompleteActivityAsync(activity);
        }
        catch (Exception ex)
        {
            await Application.Activities.FailActivityWithErrorAsync(activity, ex);
            throw;
        }
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
    /// Creates a new criteria group, attached to the predefined search (top-level) or to a parent group (nested),
    /// and records a configuration-change Activity against the owning search.
    /// </summary>
    public async Task<PredefinedSearchCriteriaGroup> CreatePredefinedSearchCriteriaGroupAsync(int predefinedSearchId, int? parentGroupId, SearchGroupType type, int position, MetaverseObject? initiatedBy = null, string? changeReason = null)
    {
        return await CreatePredefinedSearchCriteriaGroupCoreAsync(predefinedSearchId, parentGroupId, type, position, changeReason,
            activity => CreatePredefinedSearchActivityAsync(activity, initiatedBy));
    }

    /// <summary>
    /// Creates a new criteria group. API-key initiator overload.
    /// </summary>
    public async Task<PredefinedSearchCriteriaGroup> CreatePredefinedSearchCriteriaGroupAsync(int predefinedSearchId, int? parentGroupId, SearchGroupType type, int position, ApiKey initiatedByApiKey, string? changeReason = null)
    {
        return await CreatePredefinedSearchCriteriaGroupCoreAsync(predefinedSearchId, parentGroupId, type, position, changeReason,
            activity => Application.Activities.CreateActivityAsync(activity, initiatedByApiKey));
    }

    private async Task<PredefinedSearchCriteriaGroup> CreatePredefinedSearchCriteriaGroupCoreAsync(int predefinedSearchId, int? parentGroupId, SearchGroupType type, int position, string? changeReason, Func<Activity, Task> createActivityAsync)
    {
        // The owning search id is supplied by the caller directly (it is required regardless of nesting depth), so
        // no resolution walk is needed here, unlike the update/delete paths below which are keyed by group/criterion id.
        var searchName = await ResolvePredefinedSearchNameAsync(predefinedSearchId);
        var activity = new Activity
        {
            TargetName = searchName,
            TargetType = ActivityTargetType.PredefinedSearch,
            TargetOperationType = ActivityTargetOperationType.Update,
            Message = $"Adding a criteria group to Predefined Search '{searchName}'"
        };
        await createActivityAsync(activity);

        try
        {
            Log.Information("Adding a criteria group to Predefined Search '{Name}' (ID: {Id})", LogSanitiser.Sanitise(searchName), predefinedSearchId);
            var result = await Application.Repository.Search.CreatePredefinedSearchCriteriaGroupAsync(predefinedSearchId, parentGroupId, type, position);

            await CapturePredefinedSearchConfigurationChangeAsync(activity, predefinedSearchId, changeReason);
            activity.Message = $"Added a criteria group to Predefined Search '{searchName}'";
            await Application.Activities.CompleteActivityAsync(activity);

            return result;
        }
        catch (Exception ex)
        {
            await Application.Activities.FailActivityWithErrorAsync(activity, ex);
            throw;
        }
    }

    /// <summary>
    /// Updates a criteria group's logic type and position, rolling the configuration-change Activity up to the
    /// owning Predefined Search (resolved by walking the group's parent chain; see
    /// <see cref="JIM.Data.Repositories.ISearchRepository.GetOwningPredefinedSearchIdForGroupAsync"/>). Returns null
    /// if the group does not exist; no Activity is recorded in that case.
    /// </summary>
    public async Task<PredefinedSearchCriteriaGroup?> UpdatePredefinedSearchCriteriaGroupAsync(int groupId, SearchGroupType type, int position, MetaverseObject? initiatedBy = null, string? changeReason = null)
    {
        return await UpdatePredefinedSearchCriteriaGroupCoreAsync(groupId, type, position, changeReason,
            activity => CreatePredefinedSearchActivityAsync(activity, initiatedBy));
    }

    /// <summary>
    /// Updates a criteria group's logic type and position. API-key initiator overload.
    /// </summary>
    public async Task<PredefinedSearchCriteriaGroup?> UpdatePredefinedSearchCriteriaGroupAsync(int groupId, SearchGroupType type, int position, ApiKey initiatedByApiKey, string? changeReason = null)
    {
        return await UpdatePredefinedSearchCriteriaGroupCoreAsync(groupId, type, position, changeReason,
            activity => Application.Activities.CreateActivityAsync(activity, initiatedByApiKey));
    }

    private async Task<PredefinedSearchCriteriaGroup?> UpdatePredefinedSearchCriteriaGroupCoreAsync(int groupId, SearchGroupType type, int position, string? changeReason, Func<Activity, Task> createActivityAsync)
    {
        // A criteria group has no Activity target type of its own; a change to it is recorded as a configuration
        // change to the Predefined Search it belongs to (see Activity.PredefinedSearchId). When the group cannot be
        // resolved (does not exist), fall through to the plain repository call so behaviour is unchanged and no
        // Activity is recorded for a not-found update.
        var owningSearchId = await Application.Repository.Search.GetOwningPredefinedSearchIdForGroupAsync(groupId);
        if (owningSearchId == null)
            return await Application.Repository.Search.UpdatePredefinedSearchCriteriaGroupAsync(groupId, type, position);

        var owningSearchIdValue = owningSearchId.Value;
        var searchName = await ResolvePredefinedSearchNameAsync(owningSearchIdValue);
        var activity = new Activity
        {
            TargetName = searchName,
            TargetType = ActivityTargetType.PredefinedSearch,
            TargetOperationType = ActivityTargetOperationType.Update,
            Message = $"Updating a criteria group on Predefined Search '{searchName}'"
        };
        await createActivityAsync(activity);

        try
        {
            Log.Information("Updating criteria group {GroupId} on Predefined Search '{Name}' (ID: {Id})", groupId, LogSanitiser.Sanitise(searchName), owningSearchIdValue);
            var result = await Application.Repository.Search.UpdatePredefinedSearchCriteriaGroupAsync(groupId, type, position);
            if (result != null)
                await CapturePredefinedSearchConfigurationChangeAsync(activity, owningSearchIdValue, changeReason);

            activity.Message = $"Updated a criteria group on Predefined Search '{searchName}'";
            await Application.Activities.CompleteActivityAsync(activity);

            return result;
        }
        catch (Exception ex)
        {
            await Application.Activities.FailActivityWithErrorAsync(activity, ex);
            throw;
        }
    }

    /// <summary>
    /// Deletes a criteria group and its entire subtree, capturing a configuration-change Activity against the
    /// owning Predefined Search (resolved before the delete, since the group is gone afterwards). Returns false if
    /// the group does not exist; no Activity is recorded in that case.
    /// </summary>
    public async Task<bool> DeletePredefinedSearchCriteriaGroupAsync(int groupId, MetaverseObject? initiatedBy = null, string? changeReason = null)
    {
        return await DeletePredefinedSearchCriteriaGroupCoreAsync(groupId, changeReason,
            activity => CreatePredefinedSearchActivityAsync(activity, initiatedBy));
    }

    /// <summary>
    /// Deletes a criteria group and its entire subtree. API-key initiator overload.
    /// </summary>
    public async Task<bool> DeletePredefinedSearchCriteriaGroupAsync(int groupId, ApiKey initiatedByApiKey, string? changeReason = null)
    {
        return await DeletePredefinedSearchCriteriaGroupCoreAsync(groupId, changeReason,
            activity => Application.Activities.CreateActivityAsync(activity, initiatedByApiKey));
    }

    private async Task<bool> DeletePredefinedSearchCriteriaGroupCoreAsync(int groupId, string? changeReason, Func<Activity, Task> createActivityAsync)
    {
        var owningSearchId = await Application.Repository.Search.GetOwningPredefinedSearchIdForGroupAsync(groupId);
        if (owningSearchId == null)
            return await Application.Repository.Search.DeletePredefinedSearchCriteriaGroupAsync(groupId);

        var owningSearchIdValue = owningSearchId.Value;
        var searchName = await ResolvePredefinedSearchNameAsync(owningSearchIdValue);
        var activity = new Activity
        {
            TargetName = searchName,
            TargetType = ActivityTargetType.PredefinedSearch,
            TargetOperationType = ActivityTargetOperationType.Update,
            Message = $"Deleting a criteria group from Predefined Search '{searchName}'"
        };
        await createActivityAsync(activity);

        try
        {
            Log.Information("Deleting criteria group {GroupId} from Predefined Search '{Name}' (ID: {Id})", groupId, LogSanitiser.Sanitise(searchName), owningSearchIdValue);
            var deleted = await Application.Repository.Search.DeletePredefinedSearchCriteriaGroupAsync(groupId);
            if (deleted)
            {
                await CapturePredefinedSearchConfigurationChangeAsync(activity, owningSearchIdValue, changeReason);
                activity.Message = $"Deleted a criteria group from Predefined Search '{searchName}'";
            }
            else
            {
                activity.Message = $"No criteria group found to delete on Predefined Search '{searchName}'";
            }

            await Application.Activities.CompleteActivityAsync(activity);
            return deleted;
        }
        catch (Exception ex)
        {
            await Application.Activities.FailActivityWithErrorAsync(activity, ex);
            throw;
        }
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
    /// Adds a criterion to a criteria group, rolling the configuration-change Activity up to the owning Predefined
    /// Search. Returns null if the group does not exist; no Activity is recorded in that case.
    /// </summary>
    public async Task<PredefinedSearchCriteria?> CreatePredefinedSearchCriterionAsync(int groupId, PredefinedSearchCriteria criterion, MetaverseObject? initiatedBy = null, string? changeReason = null)
    {
        return await CreatePredefinedSearchCriterionCoreAsync(groupId, criterion, changeReason,
            activity => CreatePredefinedSearchActivityAsync(activity, initiatedBy));
    }

    /// <summary>
    /// Adds a criterion to a criteria group. API-key initiator overload.
    /// </summary>
    public async Task<PredefinedSearchCriteria?> CreatePredefinedSearchCriterionAsync(int groupId, PredefinedSearchCriteria criterion, ApiKey initiatedByApiKey, string? changeReason = null)
    {
        return await CreatePredefinedSearchCriterionCoreAsync(groupId, criterion, changeReason,
            activity => Application.Activities.CreateActivityAsync(activity, initiatedByApiKey));
    }

    private async Task<PredefinedSearchCriteria?> CreatePredefinedSearchCriterionCoreAsync(int groupId, PredefinedSearchCriteria criterion, string? changeReason, Func<Activity, Task> createActivityAsync)
    {
        var owningSearchId = await Application.Repository.Search.GetOwningPredefinedSearchIdForGroupAsync(groupId);
        if (owningSearchId == null)
            return await Application.Repository.Search.CreatePredefinedSearchCriterionAsync(groupId, criterion);

        var owningSearchIdValue = owningSearchId.Value;
        var searchName = await ResolvePredefinedSearchNameAsync(owningSearchIdValue);
        var activity = new Activity
        {
            TargetName = searchName,
            TargetType = ActivityTargetType.PredefinedSearch,
            TargetOperationType = ActivityTargetOperationType.Update,
            Message = $"Adding a criterion to Predefined Search '{searchName}'"
        };
        await createActivityAsync(activity);

        try
        {
            Log.Information("Adding a criterion to criteria group {GroupId} on Predefined Search '{Name}' (ID: {Id})", groupId, LogSanitiser.Sanitise(searchName), owningSearchIdValue);
            var result = await Application.Repository.Search.CreatePredefinedSearchCriterionAsync(groupId, criterion);
            if (result != null)
                await CapturePredefinedSearchConfigurationChangeAsync(activity, owningSearchIdValue, changeReason);

            activity.Message = $"Added a criterion to Predefined Search '{searchName}'";
            await Application.Activities.CompleteActivityAsync(activity);

            return result;
        }
        catch (Exception ex)
        {
            await Application.Activities.FailActivityWithErrorAsync(activity, ex);
            throw;
        }
    }

    /// <summary>
    /// Updates an existing criterion, rolling the configuration-change Activity up to the owning Predefined Search
    /// (resolved via its containing group; see
    /// <see cref="JIM.Data.Repositories.ISearchRepository.GetOwningPredefinedSearchIdForCriterionAsync"/>). Returns
    /// null if it does not exist; no Activity is recorded in that case.
    /// </summary>
    public async Task<PredefinedSearchCriteria?> UpdatePredefinedSearchCriterionAsync(PredefinedSearchCriteria criterion, MetaverseObject? initiatedBy = null, string? changeReason = null)
    {
        return await UpdatePredefinedSearchCriterionCoreAsync(criterion, changeReason,
            activity => CreatePredefinedSearchActivityAsync(activity, initiatedBy));
    }

    /// <summary>
    /// Updates an existing criterion. API-key initiator overload.
    /// </summary>
    public async Task<PredefinedSearchCriteria?> UpdatePredefinedSearchCriterionAsync(PredefinedSearchCriteria criterion, ApiKey initiatedByApiKey, string? changeReason = null)
    {
        return await UpdatePredefinedSearchCriterionCoreAsync(criterion, changeReason,
            activity => Application.Activities.CreateActivityAsync(activity, initiatedByApiKey));
    }

    private async Task<PredefinedSearchCriteria?> UpdatePredefinedSearchCriterionCoreAsync(PredefinedSearchCriteria criterion, string? changeReason, Func<Activity, Task> createActivityAsync)
    {
        var owningSearchId = await Application.Repository.Search.GetOwningPredefinedSearchIdForCriterionAsync(criterion.Id);
        if (owningSearchId == null)
            return await Application.Repository.Search.UpdatePredefinedSearchCriterionAsync(criterion);

        var owningSearchIdValue = owningSearchId.Value;
        var searchName = await ResolvePredefinedSearchNameAsync(owningSearchIdValue);
        var activity = new Activity
        {
            TargetName = searchName,
            TargetType = ActivityTargetType.PredefinedSearch,
            TargetOperationType = ActivityTargetOperationType.Update,
            Message = $"Updating a criterion on Predefined Search '{searchName}'"
        };
        await createActivityAsync(activity);

        try
        {
            Log.Information("Updating criterion {CriterionId} on Predefined Search '{Name}' (ID: {Id})", criterion.Id, LogSanitiser.Sanitise(searchName), owningSearchIdValue);
            var result = await Application.Repository.Search.UpdatePredefinedSearchCriterionAsync(criterion);
            if (result != null)
                await CapturePredefinedSearchConfigurationChangeAsync(activity, owningSearchIdValue, changeReason);

            activity.Message = $"Updated a criterion on Predefined Search '{searchName}'";
            await Application.Activities.CompleteActivityAsync(activity);

            return result;
        }
        catch (Exception ex)
        {
            await Application.Activities.FailActivityWithErrorAsync(activity, ex);
            throw;
        }
    }

    /// <summary>
    /// Deletes a criterion, capturing a configuration-change Activity against the owning Predefined Search
    /// (resolved before the delete, since the criterion is gone afterwards). Returns false if it does not exist;
    /// no Activity is recorded in that case.
    /// </summary>
    public async Task<bool> DeletePredefinedSearchCriterionAsync(int criterionId, MetaverseObject? initiatedBy = null, string? changeReason = null)
    {
        return await DeletePredefinedSearchCriterionCoreAsync(criterionId, changeReason,
            activity => CreatePredefinedSearchActivityAsync(activity, initiatedBy));
    }

    /// <summary>
    /// Deletes a criterion. API-key initiator overload.
    /// </summary>
    public async Task<bool> DeletePredefinedSearchCriterionAsync(int criterionId, ApiKey initiatedByApiKey, string? changeReason = null)
    {
        return await DeletePredefinedSearchCriterionCoreAsync(criterionId, changeReason,
            activity => Application.Activities.CreateActivityAsync(activity, initiatedByApiKey));
    }

    private async Task<bool> DeletePredefinedSearchCriterionCoreAsync(int criterionId, string? changeReason, Func<Activity, Task> createActivityAsync)
    {
        var owningSearchId = await Application.Repository.Search.GetOwningPredefinedSearchIdForCriterionAsync(criterionId);
        if (owningSearchId == null)
            return await Application.Repository.Search.DeletePredefinedSearchCriterionAsync(criterionId);

        var owningSearchIdValue = owningSearchId.Value;
        var searchName = await ResolvePredefinedSearchNameAsync(owningSearchIdValue);
        var activity = new Activity
        {
            TargetName = searchName,
            TargetType = ActivityTargetType.PredefinedSearch,
            TargetOperationType = ActivityTargetOperationType.Update,
            Message = $"Deleting a criterion from Predefined Search '{searchName}'"
        };
        await createActivityAsync(activity);

        try
        {
            Log.Information("Deleting criterion {CriterionId} from Predefined Search '{Name}' (ID: {Id})", criterionId, LogSanitiser.Sanitise(searchName), owningSearchIdValue);
            var deleted = await Application.Repository.Search.DeletePredefinedSearchCriterionAsync(criterionId);
            if (deleted)
            {
                await CapturePredefinedSearchConfigurationChangeAsync(activity, owningSearchIdValue, changeReason);
                activity.Message = $"Deleted a criterion from Predefined Search '{searchName}'";
            }
            else
            {
                activity.Message = $"No criterion found to delete on Predefined Search '{searchName}'";
            }

            await Application.Activities.CompleteActivityAsync(activity);
            return deleted;
        }
        catch (Exception ex)
        {
            await Application.Activities.FailActivityWithErrorAsync(activity, ex);
            throw;
        }
    }
    #endregion

    #region shared helpers

    /// <summary>
    /// Creates the audit Activity for a Predefined Search change attributed to <paramref name="initiatedBy"/>, or to
    /// the System principal when no user is supplied. Mirrors <c>SecurityServer.CreateRoleActivityAsync</c>.
    /// </summary>
    private Task CreatePredefinedSearchActivityAsync(Activity activity, MetaverseObject? initiatedBy) =>
        initiatedBy == null
            ? Application.Activities.CreateSystemActivityAsync(activity)
            : Application.Activities.CreateActivityAsync(activity, initiatedBy);

    /// <summary>
    /// Resolves a Predefined Search's name for use in an Activity's target/message, falling back to a placeholder
    /// when the search cannot be found (the subsequent repository mutation call still surfaces the real failure via
    /// its own exception, which fails the Activity). Mirrors <c>SecurityServer.ResolveRoleNameAsync</c>.
    /// </summary>
    private async Task<string> ResolvePredefinedSearchNameAsync(int predefinedSearchId)
    {
        var search = await Application.Repository.Search.GetPredefinedSearchCoreAsync(predefinedSearchId);
        return search?.Name ?? $"Unknown (ID: {predefinedSearchId})";
    }

    /// <summary>
    /// Captures a versioned, metadata-only configuration snapshot of a Predefined Search (its definition, result
    /// attributes and full criteria graph) onto its audit Activity via the shared
    /// ConfigurationChangeCaptureService (which owns the toggle, dedupe-guard, versioning and best-effort
    /// behaviours). The search is reloaded with its full graph so the snapshot reflects persisted truth; call it
    /// after the change has been persisted. Shared by the search's own definition update and every criteria
    /// group/criterion mutation, since all roll up into the same search's configuration change history (see
    /// <see cref="Activity.PredefinedSearchId"/>).
    /// </summary>
    private async Task CapturePredefinedSearchConfigurationChangeAsync(Activity activity, int predefinedSearchId, string? changeReason)
    {
        await Application.ConfigurationChangeCapture.CaptureChangeAsync(activity, changeReason,
            ActivityTargetType.PredefinedSearch, predefinedSearchId,
            async hashKey =>
            {
                var persisted = await Application.Repository.Search.GetPredefinedSearchAsync(predefinedSearchId); // Full graph load
                return persisted == null ? null : Application.ConfigurationSnapshots.CreateSnapshot(persisted, hashKey);
            },
            $"Predefined Search {predefinedSearchId}");
    }

    #endregion
}
