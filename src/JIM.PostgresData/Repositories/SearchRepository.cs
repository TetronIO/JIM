// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Search;
using JIM.Models.Search.DTOs;
using Microsoft.EntityFrameworkCore;
namespace JIM.PostgresData.Repositories;

public class SearchRepository : ISearchRepository
{
    private PostgresDataRepository Repository { get; }

    internal SearchRepository(PostgresDataRepository dataRepository)
    {
        Repository = dataRepository;
    }

    /// <summary>
    /// Guards against silent data loss on a load-mutate-save path. The Blazor DbContext defaults to NoTracking
    /// (see JIM.Web Program.cs), so an entity loaded without an explicit AsTracking() comes back detached and a
    /// subsequent SaveChanges persists nothing while falsely reporting success. Every mutating method here loads
    /// change-tracked; this asserts that contract and fails fast if a future change drops the AsTracking(), rather
    /// than silently discarding the edit. Mirrors the guard in ConnectedSystemRepository.UpdateSyncRuleAsync.
    /// </summary>
    private void GuardTracked<T>(T entity, string operation) where T : class
    {
        if (Repository.Database.Entry(entity).State == EntityState.Detached)
            throw new InvalidOperationException(
                $"{operation} requires a change-tracked {typeof(T).Name}, but the supplied instance is detached " +
                "from this DbContext, so no changes would be persisted. The query that loaded it must call AsTracking().");
    }

    public async Task<IList<PredefinedSearchHeader>> GetPredefinedSearchHeadersAsync()
    {
        var predefinedSearchHeaders = await Repository.Database.PredefinedSearches.OrderBy(d => d.Name).Select(d => new PredefinedSearchHeader
        {
            Name = d.Name,
            Uri = d.Uri,
            BuiltIn = d.BuiltIn,
            Created = d.Created,
            Id = d.Id,
            MetaverseAttributeCount = d.Attributes.Count(),
            MetaverseObjectTypeName = d.MetaverseObjectType.Name,
            IsDefaultForMetaverseObjectType = d.IsDefaultForMetaverseObjectType,
            IsEnabled = d.IsEnabled
        }).ToListAsync();

        return predefinedSearchHeaders;
    }

    public async Task<PredefinedSearch?> GetPredefinedSearchAsync(int id)
    {
        return await Repository.Database.PredefinedSearches.
            AsSplitQuery(). // Use split query to avoid cartesian explosion from multiple collection includes
            Include(q => q.Attributes).
            ThenInclude(q => q.MetaverseAttribute).
            Include(q => q.MetaverseObjectType).
            Include(q => q.CriteriaGroups).
            ThenInclude(cg => cg.Criteria).
            ThenInclude(c => c.MetaverseAttribute).
            Include(q => q.CriteriaGroups).
            ThenInclude(cg => cg.ChildGroups).
            ThenInclude(cg => cg.Criteria).
            ThenInclude(c => c.MetaverseAttribute).
            SingleOrDefaultAsync(q => q.Id == id);
    }

    public async Task<PredefinedSearch?> GetPredefinedSearchAsync(string uri)
    {
        return await Repository.Database.PredefinedSearches.
            AsSplitQuery(). // Use split query to avoid cartesian explosion from multiple collection includes
            Include(q => q.Attributes).
            ThenInclude(q => q.MetaverseAttribute).
            Include(q => q.MetaverseObjectType).
            Include(q => q.CriteriaGroups).
            ThenInclude(cg => cg.Criteria).
            ThenInclude(c => c.MetaverseAttribute).
            Include(q => q.CriteriaGroups).
            ThenInclude(cg => cg.ChildGroups).
            ThenInclude(cg => cg.Criteria).
            ThenInclude(c => c.MetaverseAttribute).
            SingleOrDefaultAsync(q => q.Uri == uri);
    }

    public async Task<PredefinedSearch?> GetPredefinedSearchAsync(MetaverseObjectType metaverseObjectType)
    {
        return await Repository.Database.PredefinedSearches.
            AsSplitQuery(). // Use split query to avoid cartesian explosion from multiple collection includes
            Include(q => q.Attributes).
            ThenInclude(a => a.MetaverseAttribute).
            Include(q => q.MetaverseObjectType).
            Include(q => q.CriteriaGroups).
            ThenInclude(cg => cg.Criteria).
            ThenInclude(c => c.MetaverseAttribute).
            Include(q => q.CriteriaGroups).
            ThenInclude(cg => cg.ChildGroups).
            ThenInclude(cg => cg.Criteria).
            ThenInclude(c => c.MetaverseAttribute).
            SingleOrDefaultAsync(q => q.MetaverseObjectType.Id == metaverseObjectType.Id && q.IsDefaultForMetaverseObjectType && q.IsEnabled);
    }

    public async Task<PredefinedSearch?> GetPredefinedSearchCoreAsync(int id)
    {
        return await Repository.Database.PredefinedSearches
            .AsNoTracking()
            .SingleOrDefaultAsync(ps => ps.Id == id);
    }

    public async Task UpdatePredefinedSearchAsync(PredefinedSearch predefinedSearch)
    {
        Repository.Database.PredefinedSearches.Update(predefinedSearch);
        await Repository.Database.SaveChangesAsync();
    }

    #region predefined search criteria groups

    public async Task<PredefinedSearchCriteriaGroup?> GetPredefinedSearchCriteriaGroupAsync(int groupId)
    {
        return await Repository.Database.PredefinedSearchCriteriaGroups
            .AsSplitQuery()
            .Include(g => g.Criteria)
            .ThenInclude(c => c.MetaverseAttribute)
            .Include(g => g.ChildGroups)
            .Include(g => g.ParentGroup)
            .SingleOrDefaultAsync(g => g.Id == groupId);
    }

    public async Task<PredefinedSearchCriteriaGroup> CreatePredefinedSearchCriteriaGroupAsync(int predefinedSearchId, int? parentGroupId, SearchGroupType type, int position)
    {
        var group = new PredefinedSearchCriteriaGroup { Type = type, Position = position };

        if (parentGroupId.HasValue)
        {
            var parent = await Repository.Database.PredefinedSearchCriteriaGroups
                .AsTracking()
                .Include(g => g.ChildGroups)
                .SingleOrDefaultAsync(g => g.Id == parentGroupId.Value)
                ?? throw new ArgumentException($"Parent criteria group with ID {parentGroupId.Value} not found.");
            GuardTracked(parent, nameof(CreatePredefinedSearchCriteriaGroupAsync));
            parent.ChildGroups.Add(group);
        }
        else
        {
            var search = await Repository.Database.PredefinedSearches
                .AsTracking()
                .Include(s => s.CriteriaGroups)
                .SingleOrDefaultAsync(s => s.Id == predefinedSearchId)
                ?? throw new ArgumentException($"Predefined search with ID {predefinedSearchId} not found.");
            GuardTracked(search, nameof(CreatePredefinedSearchCriteriaGroupAsync));
            search.CriteriaGroups.Add(group);
        }

        await Repository.Database.SaveChangesAsync();
        return group;
    }

    public async Task<PredefinedSearchCriteriaGroup?> UpdatePredefinedSearchCriteriaGroupAsync(int groupId, SearchGroupType type, int position)
    {
        var group = await Repository.Database.PredefinedSearchCriteriaGroups.AsTracking().SingleOrDefaultAsync(g => g.Id == groupId);
        if (group == null)
            return null;

        GuardTracked(group, nameof(UpdatePredefinedSearchCriteriaGroupAsync));
        group.Type = type;
        group.Position = position;
        await Repository.Database.SaveChangesAsync();
        return group;
    }

    public async Task<bool> DeletePredefinedSearchCriteriaGroupAsync(int groupId)
    {
        var exists = await Repository.Database.PredefinedSearchCriteriaGroups.AnyAsync(g => g.Id == groupId);
        if (!exists)
            return false;

        await RemoveCriteriaGroupSubtreeAsync(groupId);
        await Repository.Database.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Recursively removes a criteria group, its nested child groups and all contained criteria from the
    /// change tracker. The caller is responsible for the single SaveChanges. The foreign keys use NO ACTION,
    /// so each child must be removed before its parent.
    /// </summary>
    private async Task RemoveCriteriaGroupSubtreeAsync(int groupId)
    {
        var group = await Repository.Database.PredefinedSearchCriteriaGroups
            .AsTracking()
            .Include(g => g.Criteria)
            .Include(g => g.ChildGroups)
            .SingleOrDefaultAsync(g => g.Id == groupId);
        if (group == null)
            return;

        GuardTracked(group, nameof(DeletePredefinedSearchCriteriaGroupAsync));
        foreach (var child in group.ChildGroups.ToList())
            await RemoveCriteriaGroupSubtreeAsync(child.Id);

        Repository.Database.PredefinedSearchCriteria.RemoveRange(group.Criteria);
        Repository.Database.PredefinedSearchCriteriaGroups.Remove(group);
    }

    #endregion

    #region predefined search criteria

    public async Task<PredefinedSearchCriteria?> GetPredefinedSearchCriterionAsync(int criterionId)
    {
        return await Repository.Database.PredefinedSearchCriteria
            .Include(c => c.MetaverseAttribute)
            .SingleOrDefaultAsync(c => c.Id == criterionId);
    }

    public async Task<PredefinedSearchCriteria?> CreatePredefinedSearchCriterionAsync(int groupId, PredefinedSearchCriteria criterion)
    {
        var group = await Repository.Database.PredefinedSearchCriteriaGroups
            .AsTracking()
            .Include(g => g.Criteria)
            .SingleOrDefaultAsync(g => g.Id == groupId);
        if (group == null)
            return null;

        GuardTracked(group, nameof(CreatePredefinedSearchCriterionAsync));
        // Persist via the FK scalar only; the navigation is ignored so EF does not try to re-insert the attribute.
        criterion.MetaverseAttribute = null!;
        group.Criteria.Add(criterion);
        await Repository.Database.SaveChangesAsync();
        return criterion;
    }

    public async Task<PredefinedSearchCriteria?> UpdatePredefinedSearchCriterionAsync(PredefinedSearchCriteria criterion)
    {
        var existing = await Repository.Database.PredefinedSearchCriteria.AsTracking().SingleOrDefaultAsync(c => c.Id == criterion.Id);
        if (existing == null)
            return null;

        GuardTracked(existing, nameof(UpdatePredefinedSearchCriterionAsync));
        existing.ComparisonType = criterion.ComparisonType;
        existing.MetaverseAttributeId = criterion.MetaverseAttributeId;
        existing.StringValue = criterion.StringValue;
        existing.IntValue = criterion.IntValue;
        existing.LongValue = criterion.LongValue;
        existing.DateTimeValue = criterion.DateTimeValue;
        existing.BoolValue = criterion.BoolValue;
        existing.GuidValue = criterion.GuidValue;
        existing.CaseSensitive = criterion.CaseSensitive;
        existing.ValueMode = criterion.ValueMode;
        existing.RelativeCount = criterion.RelativeCount;
        existing.RelativeUnit = criterion.RelativeUnit;
        existing.RelativeDirection = criterion.RelativeDirection;
        await Repository.Database.SaveChangesAsync();
        return existing;
    }

    public async Task<bool> DeletePredefinedSearchCriterionAsync(int criterionId)
    {
        var criterion = await Repository.Database.PredefinedSearchCriteria.AsTracking().SingleOrDefaultAsync(c => c.Id == criterionId);
        if (criterion == null)
            return false;

        GuardTracked(criterion, nameof(DeletePredefinedSearchCriterionAsync));
        Repository.Database.PredefinedSearchCriteria.Remove(criterion);
        await Repository.Database.SaveChangesAsync();
        return true;
    }

    #endregion
}
