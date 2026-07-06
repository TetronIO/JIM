// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Enums;
using JIM.Models.Exceptions;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Utility;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Serilog;
using System.Diagnostics;
namespace JIM.PostgresData.Repositories;

public class MetaverseRepository : IMetaverseRepository
{
    // Picked up by the JIM diagnostic listener via the "JIM." prefix; named distinctly
    // from the Application-layer "JIM.Database" source to keep tooling traces unambiguous.
    private static readonly ActivitySource ActivitySource = new("JIM.PostgresData.Metaverse");

    #region accessors
    private PostgresDataRepository Repository { get; }
    #endregion

    #region constructors
    internal MetaverseRepository(PostgresDataRepository dataRepository)
    {
        Repository = dataRepository;
    }
    #endregion

    #region Metaverse Object Types

    public async Task<List<MetaverseObjectType>> GetMetaverseObjectTypesAsync(bool includeChildObjects)
    {
        if (includeChildObjects)
            return await Repository.Database.MetaverseObjectTypes
                .Include(q => q.Attributes.OrderBy(a => a.Name))
                .Include(q => q.PredefinedSearches)
                .OrderBy(x => x.Name)
                .ToListAsync();

        return await Repository.Database.MetaverseObjectTypes
            .Include(q => q.PredefinedSearches)
            .OrderBy(x => x.Name)
            .ToListAsync();
    }

    public async Task<List<MetaverseObjectTypeHeader>> GetMetaverseObjectTypeHeadersAsync()
    {
        var metaverseObjectTypeHeaders = await Repository.Database.MetaverseObjectTypes.OrderBy(t => t.Name).Select(t => new MetaverseObjectTypeHeader
        {
            Id = t.Id,
            Name = t.Name,
            PluralName = t.PluralName,
            Created = t.Created,
            AttributesCount = t.Attributes.Count,
            BuiltIn = t.BuiltIn,
            Icon = t.Icon,
            HasPredefinedSearches = t.PredefinedSearches.Count > 0,
            DeletionRule = t.DeletionRule,
            DeletionGracePeriod = t.DeletionGracePeriod
        }).ToListAsync();

        return metaverseObjectTypeHeaders;
    }

    public async Task<MetaverseObjectType?> GetMetaverseObjectTypeAsync(int id, bool includeChildObjects)
    {
        if (includeChildObjects)
            return await Repository.Database.MetaverseObjectTypes.Include(q => q.Attributes).SingleOrDefaultAsync(x => x.Id == id);

        return await Repository.Database.MetaverseObjectTypes.SingleOrDefaultAsync(x => x.Id == id);
    }

    public async Task<MetaverseObjectType?> GetMetaverseObjectTypeAsync(string name, bool includeChildObjects)
    {
        var result = Repository.Database.MetaverseObjectTypes;
        if (includeChildObjects)
            result.Include(q => q.Attributes);

        return await result.SingleOrDefaultAsync(q => EF.Functions.ILike(q.Name, name));
    }

    public async Task<MetaverseObjectType?> GetMetaverseObjectTypeByPluralNameAsync(string pluralName, bool includeChildObjects)
    {
        var result = Repository.Database.MetaverseObjectTypes;
        if (includeChildObjects)
            result.Include(q => q.Attributes);

        return await result.SingleOrDefaultAsync(q => EF.Functions.ILike(q.PluralName, pluralName));
    }

    public async Task CreateMetaverseObjectTypeAsync(MetaverseObjectType metaverseObjectType)
    {
        // Attach existing MetaverseAttributes so EF recognises them as existing entities
        // and only creates join-table entries (not duplicate attribute rows). Mirrors the
        // attach-then-Added pattern used in CreateMetaverseAttributeAsync.
        foreach (var attribute in metaverseObjectType.Attributes.Where(a => Repository.Database.Entry(a).State == EntityState.Detached))
        {
            Repository.Database.MetaverseAttributes.Attach(attribute);
        }

        Repository.Database.Entry(metaverseObjectType).State = EntityState.Added;
        await Repository.Database.SaveChangesAsync();
    }

    public async Task UpdateMetaverseObjectTypeAsync(MetaverseObjectType metaverseObjectType)
    {
        Repository.Database.MetaverseObjectTypes.Update(metaverseObjectType);
        await Repository.Database.SaveChangesAsync();
    }
    #endregion

    #region metaverse attributes
    public async Task<IList<MetaverseAttribute>?> GetMetaverseAttributesAsync()
    {
        return await Repository.Database.MetaverseAttributes.OrderBy(x => x.Name).ToListAsync();
    }

    public async Task<IList<MetaverseAttributeHeader>?> GetMetaverseAttributeHeadersAsync()
    {
        return await Repository.Database.MetaverseAttributes.OrderBy(a => a.Name).Select(a => new MetaverseAttributeHeader
        {
            Id = a.Id,
            Created = a.Created,
            Name = a.Name,
            BuiltIn = a.BuiltIn,
            Type = a.Type,
            AttributePlurality = a.AttributePlurality,
            MetaverseObjectTypes = a.MetaverseObjectTypes.Select(t => new KeyValuePair<int, string>(t.Id, t.Name))
        }).ToListAsync();
    }

    public async Task<PagedResultSet<MetaverseAttributeHeader>> GetMetaverseAttributeHeadersAsync(
        int page,
        int pageSize,
        string? searchQuery = null,
        string? sortBy = null,
        bool sortDescending = false)
    {
        if (pageSize < 1)
            throw new ArgumentOutOfRangeException(nameof(pageSize), "pageSize must be a positive number");

        if (page < 1)
            page = 1;

        // Limit page size to avoid increasing latency unnecessarily
        if (pageSize > 100)
            pageSize = 100;

        var query = Repository.Database.MetaverseAttributes
            .Include(a => a.MetaverseObjectTypes)
            .AsQueryable();

        // Apply search filter
        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            var searchPattern = $"%{searchQuery}%";
            query = query.Where(a => EF.Functions.ILike(a.Name, searchPattern));
        }

        // Apply sorting
        query = sortBy?.ToLower() switch
        {
            "name" => sortDescending
                ? query.OrderByDescending(a => a.Name)
                : query.OrderBy(a => a.Name),
            "type" => sortDescending
                ? query.OrderByDescending(a => a.Type)
                : query.OrderBy(a => a.Type),
            "plurality" => sortDescending
                ? query.OrderByDescending(a => a.AttributePlurality)
                : query.OrderBy(a => a.AttributePlurality),
            "builtin" => sortDescending
                ? query.OrderByDescending(a => a.BuiltIn)
                : query.OrderBy(a => a.BuiltIn),
            "created" => sortDescending
                ? query.OrderByDescending(a => a.Created)
                : query.OrderBy(a => a.Created),
            _ => sortDescending
                ? query.OrderByDescending(a => a.Name)
                : query.OrderBy(a => a.Name)
        };

        var totalResults = await query.CountAsync();

        var results = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new MetaverseAttributeHeader
            {
                Id = a.Id,
                Created = a.Created,
                Name = a.Name,
                BuiltIn = a.BuiltIn,
                Type = a.Type,
                AttributePlurality = a.AttributePlurality,
                MetaverseObjectTypes = a.MetaverseObjectTypes.Select(t => new KeyValuePair<int, string>(t.Id, t.Name))
            })
            .ToListAsync();

        return new PagedResultSet<MetaverseAttributeHeader>
        {
            Results = results,
            TotalResults = totalResults,
            PageSize = pageSize,
            CurrentPage = page
        };
    }

    public async Task<MetaverseAttribute?> GetMetaverseAttributeAsync(int id, bool withChangeTracking = false)
    {
        var query = Repository.Database.MetaverseAttributes.AsQueryable();
        if (withChangeTracking)
            query = query.AsTracking();

        return await query.SingleOrDefaultAsync(x => x.Id == id);
    }

    public async Task<MetaverseAttribute?> GetMetaverseAttributeWithObjectTypesAsync(int id, bool withChangeTracking = false)
    {
        var query = Repository.Database.MetaverseAttributes
            .Include(a => a.MetaverseObjectTypes)
            .AsQueryable();

        if (withChangeTracking)
            query = query.AsTracking();

        return await query.SingleOrDefaultAsync(x => x.Id == id);
    }

    public async Task<MetaverseAttribute?> GetMetaverseAttributeAsync(string name, bool withChangeTracking = false)
    {
        var query = Repository.Database.MetaverseAttributes.AsQueryable();
        if (withChangeTracking)
            query = query.AsTracking();

        return await query.SingleOrDefaultAsync(x => x.Name == name);
    }

    public async Task CreateMetaverseAttributeAsync(MetaverseAttribute attribute)
    {
        // Attach existing MetaverseObjectTypes so EF recognises them as existing entities
        // and only creates join table entries (not duplicate object type rows).
        foreach (var objectType in attribute.MetaverseObjectTypes)
        {
            if (Repository.Database.Entry(objectType).State == EntityState.Detached)
                Repository.Database.MetaverseObjectTypes.Attach(objectType);
        }

        // Use Entry().State instead of Add() to avoid graph traversal that would
        // override the Unchanged state of the attached object types back to Added.
        Repository.Database.Entry(attribute).State = EntityState.Added;
        await Repository.Database.SaveChangesAsync();
    }

    public async Task UpdateMetaverseAttributeAsync(MetaverseAttribute attribute)
    {
        Repository.Database.Update(attribute);
        await Repository.Database.SaveChangesAsync();
    }

    public async Task DeleteMetaverseAttributeAsync(MetaverseAttribute attribute)
    {
        Repository.Database.MetaverseAttributes.Remove(attribute);
        await Repository.Database.SaveChangesAsync();
    }

    public async Task<int> GetAttributeValueObjectCountAsync(int attributeId)
    {
        return await Repository.Database.MetaverseObjectAttributeValues
            .Where(v => v.AttributeId == attributeId)
            .Select(v => v.MetaverseObject)
            .Distinct()
            .CountAsync();
    }

    public async Task<int> GetAttributeValueObjectCountByTypeAsync(int attributeId, int metaverseObjectTypeId)
    {
        return await Repository.Database.MetaverseObjectAttributeValues
            .Where(v => v.AttributeId == attributeId && v.MetaverseObject.Type.Id == metaverseObjectTypeId)
            .Select(v => v.MetaverseObject)
            .Distinct()
            .CountAsync();
    }

    public async Task<List<SyncRuleReference>> GetSyncRulesReferencingAttributeAsync(int attributeId)
    {
        // Synchronisation Rule mappings where this attribute is the target (import rules)
        var fromMappings = Repository.Database.SyncRuleMappings
            .Where(m => m.TargetMetaverseAttributeId == attributeId && m.SyncRule != null)
            .Select(m => new SyncRuleReference { Id = m.SyncRule!.Id, Name = m.SyncRule.Name });

        // Synchronisation Rule mapping sources where this attribute is the source (export rules)
        // SyncRuleMappingSource has no navigation to SyncRuleMapping, so join through the parent
        var fromMappingSources = Repository.Database.SyncRuleMappings
            .Where(m => m.Sources.Any(s => s.MetaverseAttributeId == attributeId) && m.SyncRule != null)
            .Select(m => new SyncRuleReference { Id = m.SyncRule!.Id, Name = m.SyncRule.Name });

        // Object Matching Rules where this attribute is the target
        var fromMatchingRules = Repository.Database.ObjectMatchingRules
            .Where(r => r.TargetMetaverseAttributeId == attributeId && r.SyncRule != null)
            .Select(r => new SyncRuleReference { Id = r.SyncRule!.Id, Name = r.SyncRule.Name });

        // Scoping criteria where this attribute is referenced (navigate from SyncRule down)
        var fromScopingCriteria = Repository.Database.SyncRules
            .Where(sr => sr.ObjectScopingCriteriaGroups
                .Any(g => g.Criteria.Any(c => c.MetaverseAttribute != null && c.MetaverseAttribute.Id == attributeId)
                       || g.ChildGroups.Any(cg => cg.Criteria.Any(c => c.MetaverseAttribute != null && c.MetaverseAttribute.Id == attributeId))))
            .Select(sr => new SyncRuleReference { Id = sr.Id, Name = sr.Name });

        // Union all sources and deduplicate by Synchronisation Rule ID
        var allReferences = await fromMappings
            .Union(fromMappingSources)
            .Union(fromMatchingRules)
            .Union(fromScopingCriteria)
            .GroupBy(r => r.Id)
            .Select(g => g.First())
            .ToListAsync();

        return allReferences;
    }
    #endregion

    #region Metaverse Objects
    public async Task<List<MetaverseObject>> GetMetaverseObjectsByIdsNoTrackingAsync(IEnumerable<Guid> ids)
    {
        var idList = ids as IReadOnlyCollection<Guid> ?? ids.ToList();
        if (idList.Count == 0)
            return new List<MetaverseObject>();

        return await Repository.Database.MetaverseObjects
            .AsNoTracking()
            .Include(mvo => mvo.Type)
            .Include(mvo => mvo.AttributeValues)
            .ThenInclude(av => av.Attribute)
            .Where(mvo => idList.Contains(mvo.Id))
            .ToListAsync();
    }

    public async Task<List<Guid>> GetMetaverseObjectIdsWithScopeReviewPendingAsync(int maxResults)
    {
        // O(transitions) via the partial index on ScopeReviewPending. Ordered by Id for stable paging across
        // successive sync runs that each drain a batch of flagged Metaverse Objects (#892).
        return await Repository.Database.MetaverseObjects
            .AsNoTracking()
            .Where(mvo => mvo.ScopeReviewPending)
            .OrderBy(mvo => mvo.Id)
            .Select(mvo => mvo.Id)
            .Take(maxResults)
            .ToListAsync();
    }

    public async Task<List<MvoReferenceRecallCandidate>> GetMetaverseObjectReferenceRecallCandidatesAsync(
        IReadOnlyCollection<Guid> referencedMetaverseObjectIds)
    {
        if (referencedMetaverseObjectIds.Count == 0)
            return new List<MvoReferenceRecallCandidate>();

        // Raw SQL into a flat DTO (Summary-tier, sync hot path): a burst deletion can have tens of
        // thousands of inbound references and entity materialisation cost is unwarranted here.
        // Rows whose owning Metaverse Object is itself being deleted are excluded: there is nothing
        // to export for a referencing object that is also going away.
        var ids = referencedMetaverseObjectIds.ToArray();
        var candidates = new List<MvoReferenceRecallCandidate>();

        var connection = Repository.Database.Database.GetDbConnection();
        var wasOpen = connection.State == System.Data.ConnectionState.Open;
        if (!wasOpen)
            await connection.OpenAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                @"SELECT ""MetaverseObjectId"", ""Id"", ""AttributeId"", ""ReferenceValueId""
                  FROM ""MetaverseObjectAttributeValues""
                  WHERE ""ReferenceValueId"" = ANY(@referencedIds)
                    AND NOT (""MetaverseObjectId"" = ANY(@referencedIds))";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "referencedIds";
            parameter.Value = ids;
            command.Parameters.Add(parameter);

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                candidates.Add(new MvoReferenceRecallCandidate
                {
                    ReferencingMetaverseObjectId = reader.GetGuid(0),
                    AttributeValueId = reader.GetGuid(1),
                    MetaverseAttributeId = reader.GetInt32(2),
                    ReferencedMetaverseObjectId = reader.GetGuid(3)
                });
            }
        }
        finally
        {
            if (!wasOpen)
                await connection.CloseAsync();
        }

        return candidates;
    }

    public async Task ClearMetaverseObjectScopeReviewPendingAsync(IReadOnlyCollection<Guid> ids)
    {
        if (ids.Count == 0)
            return;

        // Clear the reconciler flag once the sync engine has re-evaluated these Metaverse Objects' export scope
        // (#892). A single bulk UPDATE over the O(flagged) rows processed keeps this off the per-object write path.
        await Repository.Database.Database.ExecuteSqlRawAsync(
            @"UPDATE ""MetaverseObjects"" SET ""ScopeReviewPending"" = false WHERE ""Id"" = ANY({0})",
            ids.ToArray());
    }

    public async Task MarkMetaverseObjectsScopeEvaluatedAsync(IReadOnlyCollection<Guid> evaluatedIds, IReadOnlyCollection<Guid> flaggedIds, DateTime nowUtc)
    {
        if (evaluatedIds.Count == 0)
            return;

        // Single bulk UPDATE over O(transitions) rows on the reconciler schedule (#892). ScopeReviewPending is
        // set to true for flagged ids and false for the rest of the evaluated set, so a stale flag self-clears
        // once the object is back in agreement; LastScopeEvaluatedAt advances for every evaluated object.
        await Repository.Database.Database.ExecuteSqlRawAsync(
            @"UPDATE ""MetaverseObjects""
              SET ""LastScopeEvaluatedAt"" = {2},
                  ""ScopeReviewPending"" = (""Id"" = ANY({1}))
              WHERE ""Id"" = ANY({0})",
            evaluatedIds.ToArray(), flaggedIds.ToArray(), nowUtc);
    }

    public async Task<MetaverseObject?> GetMetaverseObjectAsync(Guid id)
    {
        return await Repository.Database.MetaverseObjects.
            AsSplitQuery(). // Use split query to avoid cartesian explosion from multiple collection includes
            Include(mo => mo.Type).
            Include(mo => mo.AttributeValues).
            ThenInclude(av => av.Attribute).
            Include(mo => mo.AttributeValues).
            ThenInclude(av => av.ReferenceValue).
            ThenInclude(rv => rv!.AttributeValues.Where(rvav => rvav.Attribute.Name == Constants.BuiltInAttributes.DisplayName)).
            ThenInclude(rvav => rvav.Attribute).
            Include(mo => mo.AttributeValues).
            ThenInclude(av => av.ReferenceValue).
            ThenInclude(rv => rv!.Type).
            // Provenance navigations: the API and Metaverse Object views surface which Connected System
            // and Synchronisation Rule contributed each value (#931). In-memory tests auto-track these
            // navigations, so only real PostgreSQL exercises these includes.
            Include(mo => mo.AttributeValues).
            ThenInclude(av => av.ContributedBySystem).
            Include(mo => mo.AttributeValues).
            ThenInclude(av => av.ContributedBySyncRule).
            SingleOrDefaultAsync(mo => mo.Id == id);
    }

    public async Task<MetaverseObject?> GetMetaverseObjectWithChangeHistoryAsync(Guid id)
    {
        return await Repository.Database.MetaverseObjects.
            AsSplitQuery(). // Use split query to avoid cartesian explosion from multiple collection includes
            Include(mo => mo.Type).
            Include(mo => mo.AttributeValues).
            ThenInclude(av => av.Attribute).
            Include(mo => mo.AttributeValues).
            ThenInclude(av => av.ReferenceValue).
            ThenInclude(rv => rv!.AttributeValues.Where(rvav => rvav.Attribute.Name == Constants.BuiltInAttributes.DisplayName)).
            ThenInclude(rvav => rvav.Attribute).
            Include(mo => mo.AttributeValues).
            ThenInclude(av => av.ReferenceValue).
            ThenInclude(rv => rv!.Type).
            Include(mo => mo.Changes).
            ThenInclude(c => c.AttributeChanges).
            ThenInclude(ac => ac.Attribute).
            Include(mo => mo.Changes).
            ThenInclude(c => c.AttributeChanges).
            ThenInclude(ac => ac.ValueChanges).
            ThenInclude(vc => vc.ReferenceValue).
            ThenInclude(rv => rv!.Type).
            Include(mo => mo.Changes).
            ThenInclude(c => c.AttributeChanges).
            ThenInclude(ac => ac.ValueChanges).
            ThenInclude(vc => vc.ReferenceValue).
            ThenInclude(rv => rv!.AttributeValues.Where(rvav => rvav.Attribute.Name == Constants.BuiltInAttributes.DisplayName)).
            ThenInclude(rvav => rvav.Attribute).
            Include(mo => mo.Changes).
            ThenInclude(c => c.SyncRule).
            Include(mo => mo.Changes).
            ThenInclude(c => c.ActivityRunProfileExecutionItem).
            ThenInclude(rpei => rpei!.Activity).
            SingleOrDefaultAsync(mo => mo.Id == id);
    }

    private const int CappedMvaLimit = 10;

    public async Task<MvoDetailResult?> GetMetaverseObjectDetailAsync(Guid id, MvoAttributeLoadStrategy loadStrategy)
    {
        if (loadStrategy == MvoAttributeLoadStrategy.All)
        {
            var mvo = await GetMetaverseObjectWithChangeHistoryAsync(id);
            return mvo == null ? null : new MvoDetailResult { MetaverseObject = mvo };
        }

        // CappedMva strategy: load the MVO shell only. Change history is intentionally NOT
        // eager-loaded here; the original Include chain dominated the page load (~1.5s on
        // chatty groups). Change rows are paged separately via GetMvoChangeHistoryAsync.
        MetaverseObject? entity;
        using (ActivitySource.StartActivity("Mvo.LoadShell"))
        {
            entity = await Repository.Database.MetaverseObjects
                .Include(mo => mo.Type)
                .SingleOrDefaultAsync(mo => mo.Id == id);
        }

        if (entity == null)
            return null;

        // Total change-history count, surfaced on the result so the UI can render a badge
        // without eager-loading the change rows themselves.
        int changeCount;
        MvoChangeInitiatorSummary? earliestInitiator = null;
        MvoChangeInitiatorSummary? latestInitiator = null;
        using (ActivitySource.StartActivity("Mvo.LoadChangeCountAndInitiators"))
        {
            var changeQuery = Repository.Database.Set<MetaverseObjectChange>()
                .AsNoTracking()
                .Where(c => c.MetaverseObject != null && c.MetaverseObject.Id == id);

            changeCount = await changeQuery.CountAsync();
            if (changeCount > 0)
            {
                earliestInitiator = await changeQuery
                    .OrderBy(c => c.ChangeTime)
                    .Select(c => new MvoChangeInitiatorSummary
                    {
                        ChangeTime = c.ChangeTime,
                        InitiatedByType = c.InitiatedByType,
                        InitiatedById = c.InitiatedById,
                        InitiatedByName = c.InitiatedByName
                    })
                    .FirstOrDefaultAsync();

                latestInitiator = await changeQuery
                    .OrderByDescending(c => c.ChangeTime)
                    .Select(c => new MvoChangeInitiatorSummary
                    {
                        ChangeTime = c.ChangeTime,
                        InitiatedByType = c.InitiatedByType,
                        InitiatedById = c.InitiatedById,
                        InitiatedByName = c.InitiatedByName
                    })
                    .FirstOrDefaultAsync();
            }
        }

        // Step 2: Get per-attribute value counts for this MVO
        Dictionary<string, int> totalCounts;
        using (ActivitySource.StartActivity("Mvo.LoadAttributeCounts"))
        {
            var attributeValueCounts = await Repository.Database.Set<MetaverseObjectAttributeValue>()
                .Where(av => av.MetaverseObject.Id == id)
                .GroupBy(av => av.Attribute.Name)
                .Select(g => new { AttributeName = g.Key, Count = g.Count() })
                .ToListAsync();

            totalCounts = attributeValueCounts.ToDictionary(x => x.AttributeName, x => x.Count);
        }

        // Step 3: Load SVA values (all of them) and MVA values (capped)
        HashSet<int> multiValuedAttributeIds;
        using (ActivitySource.StartActivity("Mvo.LoadAttributePluralities"))
        {
            var mvaAttributeIds = await Repository.Database.Set<MetaverseObjectAttributeValue>()
                .Where(av => av.MetaverseObject.Id == id)
                .Select(av => new { av.AttributeId, av.Attribute.AttributePlurality })
                .Distinct()
                .ToListAsync();

            multiValuedAttributeIds = mvaAttributeIds
                .Where(a => a.AttributePlurality == AttributePlurality.MultiValued)
                .Select(a => a.AttributeId)
                .ToHashSet();
        }

        // Load all SVA values
        List<MetaverseObjectAttributeValue> svaValues;
        using (var svaSpan = ActivitySource.StartActivity("Mvo.LoadSvaValues"))
        {
            // AsTracking required: Include path AttributeValue -> ReferenceValue(MVO) -> AttributeValues creates a cycle.
            svaValues = await Repository.Database.Set<MetaverseObjectAttributeValue>()
                .AsTracking()
                .AsSplitQuery()
                .Where(av => av.MetaverseObject.Id == id && !multiValuedAttributeIds.Contains(av.AttributeId))
                .Include(av => av.Attribute)
                .Include(av => av.ReferenceValue)
                .ThenInclude(rv => rv!.Type)
                .Include(av => av.ReferenceValue)
                .ThenInclude(rv => rv!.AttributeValues.Where(rvav => rvav.Attribute.Name == Constants.BuiltInAttributes.DisplayName))
                .ThenInclude(rvav => rvav.Attribute)
                .ToListAsync();
            svaSpan?.SetTag("count", svaValues.Count);
        }

        // Load capped MVA values per attribute
        var cappedMvaValues = new List<MetaverseObjectAttributeValue>();
        using (var mvaSpan = ActivitySource.StartActivity("Mvo.LoadCappedMvaValues"))
        {
            mvaSpan?.SetTag("attributeCount", multiValuedAttributeIds.Count);
            mvaSpan?.SetTag("cap", CappedMvaLimit);
            foreach (var attrId in multiValuedAttributeIds)
            {
                using var iterSpan = ActivitySource.StartActivity("Mvo.LoadCappedMvaValues.Attribute");
                iterSpan?.SetTag("attributeId", attrId);
                // AsTracking required: Include path AttributeValue -> ReferenceValue(MVO) -> AttributeValues creates a cycle.
                var values = await Repository.Database.Set<MetaverseObjectAttributeValue>()
                    .AsTracking()
                    .AsSplitQuery()
                    .Where(av => av.MetaverseObject.Id == id && av.AttributeId == attrId)
                    .OrderBy(av => av.Id)
                    .Take(CappedMvaLimit)
                    .Include(av => av.Attribute)
                    .Include(av => av.ReferenceValue)
                    .ThenInclude(rv => rv!.Type)
                    .Include(av => av.ReferenceValue)
                    .ThenInclude(rv => rv!.AttributeValues.Where(rvav => rvav.Attribute.Name == Constants.BuiltInAttributes.DisplayName))
                    .ThenInclude(rvav => rvav.Attribute)
                    .ToListAsync();
                iterSpan?.SetTag("returned", values.Count);

                cappedMvaValues.AddRange(values);
            }
        }

        // Combine and attach to entity
        entity.AttributeValues = svaValues.Concat(cappedMvaValues).ToList();

        return new MvoDetailResult
        {
            MetaverseObject = entity,
            AttributeValueTotalCounts = totalCounts,
            ChangeCount = changeCount,
            EarliestChangeInitiator = earliestInitiator,
            LatestChangeInitiator = latestInitiator
        };
    }

    public async Task<(List<MvoChangeHistoryDto> Items, int TotalCount)> GetMvoChangeHistoryAsync(Guid metaverseObjectId, int page, int pageSize)
    {
        if (page < 1)
            page = 1;
        if (pageSize < 1)
            pageSize = 1;

        using var span = ActivitySource.StartActivity("Mvo.LoadChangeHistoryPage");
        span?.SetTag("page", page);
        span?.SetTag("pageSize", pageSize);

        // Run count and page in parallel; both hit the same indexed FK column, so the cost is
        // dominated by the page projection rather than the aggregate.
        var baseQuery = Repository.Database.Set<MetaverseObjectChange>()
            .AsNoTracking()
            .Where(c => c.MetaverseObject != null && c.MetaverseObject.Id == metaverseObjectId);

        var totalCount = await baseQuery.CountAsync();
        if (totalCount == 0)
            return (new List<MvoChangeHistoryDto>(), 0);

        // Flat projection. No Includes; EF generates the joins from the Select.
        // Reference target display names come from MetaverseObject.CachedDisplayName,
        // which avoids the AttributeValues filter-Include trick that the old query used.
        var items = await baseQuery
            .OrderByDescending(c => c.ChangeTime)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new MvoChangeHistoryDto
            {
                Id = c.Id,
                ChangeType = c.ChangeType,
                ChangeTime = c.ChangeTime,
                InitiatedByType = c.InitiatedByType,
                InitiatedById = c.InitiatedById,
                InitiatedByName = c.InitiatedByName,
                ChangeInitiatorType = c.ChangeInitiatorType,
                SyncRuleId = c.SyncRuleId,
                SyncRuleName = c.SyncRuleName,
                ActivityRunProfileExecutionItemId = c.ActivityRunProfileExecutionItemId,
                CsoId = c.ActivityRunProfileExecutionItem != null ? c.ActivityRunProfileExecutionItem.ConnectedSystemObjectId : null,
                CsoExternalId = c.ActivityRunProfileExecutionItem != null ? c.ActivityRunProfileExecutionItem.ExternalIdSnapshot : null,
                ConnectedSystemId = c.ActivityRunProfileExecutionItem != null && c.ActivityRunProfileExecutionItem.Activity != null
                    ? c.ActivityRunProfileExecutionItem.Activity.ConnectedSystemId
                    : null,
                ConnectedSystemName = c.ActivityRunProfileExecutionItem != null && c.ActivityRunProfileExecutionItem.Activity != null
                    ? c.ActivityRunProfileExecutionItem.Activity.TargetContext
                    : null,
                RunProfileName = c.ActivityRunProfileExecutionItem != null && c.ActivityRunProfileExecutionItem.Activity != null
                    ? c.ActivityRunProfileExecutionItem.Activity.TargetName
                    : null,
                ConnectedSystemRunType = c.ActivityRunProfileExecutionItem != null && c.ActivityRunProfileExecutionItem.Activity != null
                    ? c.ActivityRunProfileExecutionItem.Activity.ConnectedSystemRunType
                    : (ConnectedSystemRunType?)null,
                AttributeChanges = c.AttributeChanges
                    .OrderBy(ac => ac.AttributeName)
                    .Select(ac => new MvoAttributeChangeDto
                    {
                        AttributeName = ac.AttributeName,
                        AttributeType = ac.AttributeType,
                        AttributePlurality = ac.Attribute != null ? ac.Attribute.AttributePlurality : AttributePlurality.SingleValued,
                        ValueChanges = ac.ValueChanges
                            .Select(vc => new MvoValueChangeDto
                            {
                                ValueChangeType = vc.ValueChangeType,
                                StringValue = vc.StringValue,
                                DateTimeValue = vc.DateTimeValue,
                                IntValue = vc.IntValue,
                                ByteValueLength = vc.ByteValueLength,
                                GuidValue = vc.GuidValue,
                                BoolValue = vc.BoolValue,
                                ReferenceValue = vc.ReferenceValue == null
                                    ? null
                                    : new MvoChangeReferenceDto
                                    {
                                        Id = vc.ReferenceValue.Id,
                                        DisplayName = vc.ReferenceValue.CachedDisplayName,
                                        TypeName = vc.ReferenceValue.Type.Name,
                                        TypePluralName = vc.ReferenceValue.Type.PluralName
                                    }
                            })
                            .ToList()
                    })
                    .ToList()
            })
            .ToListAsync();

        span?.SetTag("returned", items.Count);
        span?.SetTag("totalCount", totalCount);
        return (items, totalCount);
    }

    public async Task<MetaverseObjectHeader?> GetMetaverseObjectHeaderAsync(Guid id)
    {
        // Materialise the full entity so Include chains are honoured (Include is ignored
        // when projecting via .Select(), leaving navigation properties null).
        var entity = await Repository.Database.MetaverseObjects
            .AsSplitQuery()
            .Include(mo => mo.Type)
            .Include(mo => mo.AttributeValues)
                .ThenInclude(av => av.Attribute)
            .Include(mo => mo.AttributeValues)
                .ThenInclude(av => av.ReferenceValue)
                    .ThenInclude(rv => rv!.AttributeValues.Where(rvav => rvav.Attribute.Name == Constants.BuiltInAttributes.DisplayName))
                        .ThenInclude(rvav => rvav.Attribute)
            .SingleOrDefaultAsync(mo => mo.Id == id);

        if (entity == null)
            return null;

        return new MetaverseObjectHeader
        {
            Id = entity.Id,
            Created = entity.Created,
            Status = entity.Status,
            TypeId = entity.Type.Id,
            TypeName = entity.Type.Name,
            TypePluralName = entity.Type.PluralName,
            AttributeValues = entity.AttributeValues.ToList()
        };
    }

    public async Task UpdateMetaverseObjectAsync(MetaverseObject metaverseObject)
    {
        Repository.Database.Update(metaverseObject);
        await Repository.Database.SaveChangesAsync();
    }

    public async Task CreateMetaverseObjectAsync(MetaverseObject metaverseObject)
    {
        // Attach existing entities referenced by nav properties so EF recognises them
        // as existing and doesn't attempt to insert them as new rows.
        if (metaverseObject.Type != null && Repository.Database.Entry(metaverseObject.Type).State == EntityState.Detached)
            Repository.Database.MetaverseObjectTypes.Attach(metaverseObject.Type);

        foreach (var av in metaverseObject.AttributeValues)
        {
            if (av.Attribute != null && Repository.Database.Entry(av.Attribute).State == EntityState.Detached)
                Repository.Database.MetaverseAttributes.Attach(av.Attribute);
        }

        // Attach existing MetaverseAttribute entities referenced by change records so EF
        // recognises them as existing (same pattern as attribute values above).
        foreach (var change in metaverseObject.Changes)
        {
            foreach (var ac in change.AttributeChanges)
            {
                if (ac.Attribute != null && Repository.Database.Entry(ac.Attribute).State == EntityState.Detached)
                    Repository.Database.MetaverseAttributes.Attach(ac.Attribute);
            }
        }

        // Use Entry().State instead of Add() to avoid graph traversal that would
        // override the Unchanged state of attached entities (Type, Attributes) to Added.
        Repository.Database.Entry(metaverseObject).State = EntityState.Added;
        foreach (var av in metaverseObject.AttributeValues)
            Repository.Database.Entry(av).State = EntityState.Added;

        // Mark change history records as Added so they are persisted alongside the MVO.
        foreach (var change in metaverseObject.Changes)
        {
            Repository.Database.Entry(change).State = EntityState.Added;
            foreach (var ac in change.AttributeChanges)
            {
                Repository.Database.Entry(ac).State = EntityState.Added;
                foreach (var vc in ac.ValueChanges)
                    Repository.Database.Entry(vc).State = EntityState.Added;
            }
        }

        await Repository.Database.SaveChangesAsync();
    }

    /// <summary>
    /// Creates multiple Metaverse Objects in a single batch operation.
    /// This is more efficient than calling CreateMetaverseObjectAsync for each object.
    /// </summary>
    /// <param name="metaverseObjects">The list of Metaverse Objects to create.</param>
    public async Task CreateMetaverseObjectsAsync(IEnumerable<MetaverseObject> metaverseObjects)
    {
        var objectList = metaverseObjects.ToList();
        if (objectList.Count == 0)
            return;

        Repository.Database.MetaverseObjects.AddRange(objectList);
        await Repository.Database.SaveChangesAsync();
    }

    /// <summary>
    /// Updates multiple Metaverse Objects in a single batch operation.
    /// This is more efficient than calling UpdateMetaverseObjectAsync for each object.
    /// </summary>
    /// <param name="metaverseObjects">The list of Metaverse Objects to update.</param>
    public async Task UpdateMetaverseObjectsAsync(IEnumerable<MetaverseObject> metaverseObjects)
    {
        var objectList = metaverseObjects.ToList();
        if (objectList.Count == 0)
            return;

        // Always use explicit per-entity state management for MVOs and their attribute values.
        // This avoids three problems:
        // 1. When entities are detached (after ClearChangeTracker), UpdateRange traverses the
        //    full object graph and hits identity conflicts on shared MetaverseAttribute entities.
        // 2. When AutoDetectChangesEnabled is disabled (during cross-page reference resolution),
        //    UpdateRange marks the MVO as Modified but does NOT detect newly added attribute
        //    values in the collection, causing them to silently not be persisted.
        // 3. When AutoDetectChangesEnabled is disabled, attribute values removed from the
        //    MVO.AttributeValues collection are not detected by SaveChangesAsync, causing
        //    stale values to remain in the database (e.g., single-valued attributes accumulating
        //    multiple values when replaced).
        //
        // By explicitly setting Entry().State on each attribute value, we ensure new attribute
        // values (IsKeySet=false → Added) are always persisted regardless of change detection.
        // By explicitly marking removed attribute values as Deleted, we ensure they are cleaned up.
        foreach (var mvo in objectList)
        {
            Repository.UpdateDetachedSafe(mvo);

            // Detect attribute values that were removed from the collection but are still tracked.
            // When AutoDetectChangesEnabled is disabled, EF does not scan collections for removals
            // during SaveChangesAsync, so removed values silently persist in the database.
            var currentAvIds = new HashSet<Guid>(mvo.AttributeValues.Where(av => av.Id != Guid.Empty).Select(av => av.Id));
            var trackedAvEntries = Repository.Database.ChangeTracker.Entries<MetaverseObjectAttributeValue>()
                .Where(e => e.Entity.MetaverseObject == mvo &&
                            e.Entity.Id != Guid.Empty &&
                            e.State is not EntityState.Deleted and not EntityState.Detached &&
                            !currentAvIds.Contains(e.Entity.Id))
                .ToList();

            foreach (var removedEntry in trackedAvEntries)
                removedEntry.State = EntityState.Deleted;

            foreach (var av in mvo.AttributeValues)
            {
                var avEntry = Repository.Database.Entry(av);
                if (avEntry.State is EntityState.Detached or EntityState.Unchanged)
                    avEntry.State = avEntry.IsKeySet ? EntityState.Modified : EntityState.Added;
            }
        }

        await Repository.Database.SaveChangesAsync();
    }

    public async Task<MetaverseObject?> GetMetaverseObjectByTypeAndAttributeAsync(MetaverseObjectType metaverseObjectType, MetaverseAttribute metaverseAttribute, string attributeValue)
    {
        // AsTracking required: the Include path MetaverseObjectAttributeValue -> MetaverseObject -> AttributeValues
        // creates a cycle that EF Core forbids in no-tracking queries (no identity resolution to break the cycle).
        var av = await Repository.Database.MetaverseObjectAttributeValues
            .AsTracking()
            .Include(q => q.MetaverseObject)
            .ThenInclude(mo => mo.AttributeValues)
            .ThenInclude(av => av.Attribute)
            .SingleOrDefaultAsync(av =>
                av.Attribute.Id == metaverseAttribute.Id &&
                av.StringValue != null && av.StringValue == attributeValue &&
                av.MetaverseObject.Type.Id == metaverseObjectType.Id);

        return av?.MetaverseObject;
    }

    public async Task<int> GetMetaverseObjectCountAsync()
    {
        return await Repository.Database.MetaverseObjects.CountAsync();
    }

    public async Task<int> GetMetaverseObjectOfTypeCountAsync(int metaverseObjectTypeId)
    {
        return await Repository.Database.MetaverseObjects.Where(x => x.Type.Id == metaverseObjectTypeId).CountAsync();
    }

    /// <summary>
    /// Recursively builds a parenthesised SQL boolean expression for a predefined-search criteria group:
    /// its criteria and nested child groups combined with AND (group type All) or OR (group type Any).
    /// An empty group (no criteria and no child groups) is always-true and renders as <c>TRUE</c>, matching
    /// the in-memory semantics of <see cref="JIM.Application"/>'s scoping evaluator. The shared
    /// <paramref name="criteriaIndex"/> is incremented per criterion so parameter names stay unique across the tree.
    /// </summary>
    private static string BuildPredefinedSearchGroupSql(PredefinedSearchCriteriaGroup group, ref int criteriaIndex, List<NpgsqlParameter> parameters, DateTime nowUtc)
    {
        var clauses = new List<string>();

        foreach (var criteria in group.Criteria)
        {
            clauses.Add(BuildPredefinedSearchCriterionSql(criteria, criteriaIndex, parameters, nowUtc));
            criteriaIndex++;
        }

        foreach (var childGroup in group.ChildGroups)
            clauses.Add(BuildPredefinedSearchGroupSql(childGroup, ref criteriaIndex, parameters, nowUtc));

        // An empty group matches everything (parity with ScopingEvaluationServer's empty-group handling).
        if (clauses.Count == 0)
            return "TRUE";

        var joiner = group.Type == SearchGroupType.All ? " AND " : " OR ";
        return $"({string.Join(joiner, clauses)})";
    }

    /// <summary>
    /// Builds a parameterised EXISTS / NOT EXISTS SQL fragment for a single predefined-search criterion.
    /// The attribute-value column is selected to match the attribute's data type (Text, Number, LongNumber,
    /// DateTime, Boolean, Guid) so the per-column indexes on MetaverseObjectAttributeValues stay usable, and
    /// the requested comparison operator is validated against that data type. Adds the attribute-id and value
    /// parameters to <paramref name="parameters"/>. Throws <see cref="NotSupportedException"/> for an operator
    /// that does not apply to the attribute's data type (callers validate at the API boundary before reaching here).
    /// </summary>
    private static string BuildPredefinedSearchCriterionSql(PredefinedSearchCriteria criteria, int index, List<NpgsqlParameter> parameters, DateTime nowUtc)
    {
        var attrParam = $"@criteriaAttrId{index}";
        var valParamName = $"criteriaVal{index}";
        var valParam = $"@{valParamName}";
        parameters.Add(new NpgsqlParameter($"criteriaAttrId{index}", criteria.MetaverseAttributeId));

        var dataType = criteria.GetAttributeDataType()
            ?? throw new NotSupportedException("Predefined search criterion has no resolvable attribute data type.");

        // EXISTS: at least one of the object's values for this attribute satisfies the predicate.
        string Exists(string predicate) =>
            $"""EXISTS (SELECT 1 FROM "MetaverseObjectAttributeValues" cav WHERE cav."MetaverseObjectId" = m."Id" AND cav."AttributeId" = {attrParam} AND {predicate})""";
        // NOT EXISTS: none of the object's values for this attribute satisfies the predicate (negative text operators).
        string NotExists(string predicate) =>
            $"""NOT EXISTS (SELECT 1 FROM "MetaverseObjectAttributeValues" cav WHERE cav."MetaverseObjectId" = m."Id" AND cav."AttributeId" = {attrParam} AND {predicate})""";

        NotSupportedException Unsupported() =>
            new($"SearchComparisonType.{criteria.ComparisonType} is not supported for {dataType} attributes.");

        switch (dataType)
        {
            case AttributeDataType.Text:
            {
                parameters.Add(new NpgsqlParameter(valParamName, NpgsqlDbType.Text) { Value = (object?)criteria.StringValue ?? DBNull.Value });
                const string col = "cav.\"StringValue\"";
                // ILIKE is case-insensitive; LIKE / = are case-sensitive. lower() keeps equality case-insensitive.
                return criteria.ComparisonType switch
                {
                    SearchComparisonType.Equals => criteria.CaseSensitive
                        ? Exists($"{col} = {valParam}")
                        : Exists($"lower({col}) = lower({valParam})"),
                    SearchComparisonType.NotEquals => criteria.CaseSensitive
                        ? Exists($"{col} <> {valParam}")
                        : Exists($"lower({col}) <> lower({valParam})"),
                    SearchComparisonType.StartsWith => Exists(criteria.CaseSensitive
                        ? $"{col} IS NOT NULL AND {col} LIKE {valParam} || '%'"
                        : $"{col} IS NOT NULL AND {col} ILIKE {valParam} || '%'"),
                    SearchComparisonType.NotStartsWith => NotExists(criteria.CaseSensitive
                        ? $"{col} IS NOT NULL AND {col} LIKE {valParam} || '%'"
                        : $"{col} IS NOT NULL AND {col} ILIKE {valParam} || '%'"),
                    SearchComparisonType.EndsWith => Exists(criteria.CaseSensitive
                        ? $"{col} IS NOT NULL AND {col} LIKE '%' || {valParam}"
                        : $"{col} IS NOT NULL AND {col} ILIKE '%' || {valParam}"),
                    SearchComparisonType.NotEndsWith => NotExists(criteria.CaseSensitive
                        ? $"{col} IS NOT NULL AND {col} LIKE '%' || {valParam}"
                        : $"{col} IS NOT NULL AND {col} ILIKE '%' || {valParam}"),
                    SearchComparisonType.Contains => Exists(criteria.CaseSensitive
                        ? $"{col} IS NOT NULL AND {col} LIKE '%' || {valParam} || '%'"
                        : $"{col} IS NOT NULL AND {col} ILIKE '%' || {valParam} || '%'"),
                    SearchComparisonType.NotContains => NotExists(criteria.CaseSensitive
                        ? $"{col} IS NOT NULL AND {col} LIKE '%' || {valParam} || '%'"
                        : $"{col} IS NOT NULL AND {col} ILIKE '%' || {valParam} || '%'"),
                    _ => throw Unsupported()
                };
            }
            case AttributeDataType.Number:
                parameters.Add(new NpgsqlParameter(valParamName, NpgsqlDbType.Integer) { Value = (object?)criteria.IntValue ?? DBNull.Value });
                return BuildOrderedComparisonSql(criteria.ComparisonType, "cav.\"IntValue\"", valParam, Exists, Unsupported);
            case AttributeDataType.LongNumber:
                parameters.Add(new NpgsqlParameter(valParamName, NpgsqlDbType.Bigint) { Value = (object?)criteria.LongValue ?? DBNull.Value });
                return BuildOrderedComparisonSql(criteria.ComparisonType, "cav.\"LongValue\"", valParam, Exists, Unsupported);
            case AttributeDataType.DateTime:
                // Resolve a relative criterion to a literal boundary before binding, so the SQL sees a constant
                // and the DateTimeValue index stays usable. Absolute criteria use their stored value.
                var dateBoundary = criteria.ValueMode == DateCriteriaValueMode.Relative && criteria.RelativeCount.HasValue && criteria.RelativeUnit.HasValue && criteria.RelativeDirection.HasValue
                    ? RelativeDateResolver.Resolve(criteria.RelativeCount.Value, criteria.RelativeUnit.Value, criteria.RelativeDirection.Value, nowUtc)
                    : NormaliseToUtc(criteria.DateTimeValue);
                parameters.Add(new NpgsqlParameter(valParamName, NpgsqlDbType.TimestampTz) { Value = (object?)dateBoundary ?? DBNull.Value });
                return BuildOrderedComparisonSql(criteria.ComparisonType, "cav.\"DateTimeValue\"", valParam, Exists, Unsupported);
            case AttributeDataType.Boolean:
                parameters.Add(new NpgsqlParameter(valParamName, NpgsqlDbType.Boolean) { Value = (object?)criteria.BoolValue ?? DBNull.Value });
                return criteria.ComparisonType switch
                {
                    SearchComparisonType.Equals => Exists($"cav.\"BoolValue\" = {valParam}"),
                    SearchComparisonType.NotEquals => Exists($"cav.\"BoolValue\" <> {valParam}"),
                    _ => throw Unsupported()
                };
            case AttributeDataType.Guid:
                parameters.Add(new NpgsqlParameter(valParamName, NpgsqlDbType.Uuid) { Value = (object?)criteria.GuidValue ?? DBNull.Value });
                return criteria.ComparisonType switch
                {
                    SearchComparisonType.Equals => Exists($"cav.\"GuidValue\" = {valParam}"),
                    SearchComparisonType.NotEquals => Exists($"cav.\"GuidValue\" <> {valParam}"),
                    _ => throw Unsupported()
                };
            default:
                throw new NotSupportedException($"Predefined search criteria are not supported for {dataType} attributes.");
        }
    }

    /// <summary>
    /// Builds the SQL predicate for an ordered (Number / LongNumber / DateTime) comparison, supporting
    /// equality and the four ordering operators. Throws for any operator that does not apply.
    /// </summary>
    private static string BuildOrderedComparisonSql(SearchComparisonType comparisonType, string column, string valParam, Func<string, string> exists, Func<NotSupportedException> unsupported)
    {
        return comparisonType switch
        {
            SearchComparisonType.Equals => exists($"{column} = {valParam}"),
            SearchComparisonType.NotEquals => exists($"{column} <> {valParam}"),
            SearchComparisonType.LessThan => exists($"{column} < {valParam}"),
            SearchComparisonType.LessThanOrEquals => exists($"{column} <= {valParam}"),
            SearchComparisonType.GreaterThan => exists($"{column} > {valParam}"),
            SearchComparisonType.GreaterThanOrEquals => exists($"{column} >= {valParam}"),
            _ => throw unsupported()
        };
    }

    /// <summary>
    /// Ensures a DateTime is expressed as UTC before it is bound to a 'timestamp with time zone' parameter.
    /// Values stored by JIM are UTC (see DateTime handling in src/CLAUDE.md); Unspecified-kind values are treated as UTC.
    /// </summary>
    private static DateTime? NormaliseToUtc(DateTime? value)
    {
        if (!value.HasValue)
            return null;

        return value.Value.Kind switch
        {
            DateTimeKind.Utc => value.Value,
            DateTimeKind.Local => value.Value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
        };
    }

    /// <inheritdoc/>
    public async Task<PagedResultSet<MetaverseObjectHeader>> GetMetaverseObjectHeadersPagedAsync(
        PredefinedSearch predefinedSearch,
        int page,
        int pageSize,
        string? searchQuery = null,
        string? sortBy = null,
        bool sortDescending = true)
    {
        if (pageSize < 1)
            throw new ArgumentOutOfRangeException(nameof(pageSize), "pageSize must be a positive number");

        if (page < 1)
            page = 1;

        // limit page size to avoid increasing latency unnecessarily
        if (pageSize > 100)
            pageSize = 100;

        // Extract the attribute IDs to project from the PredefinedSearch
        var returnAttributeIds = predefinedSearch.Attributes
            .Select(a => a.MetaverseAttribute.Id)
            .ToList();

        // Use raw SQL for all queries — EF Core's query pipeline adds significant overhead
        // at 100k+ scale, while the underlying SQL executes in ~10-30ms.
        var connection = Repository.Database.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync();

        var typeId = predefinedSearch.MetaverseObjectType.Id;
        var offset = (page - 1) * pageSize;

        // Build shared WHERE clause fragments and parameters for count + page queries
        var whereClause = """m."TypeId" = @typeId""";
        var sharedParams = new List<NpgsqlParameter> { new("typeId", typeId) };

        // Search filter — case-insensitive search across all string attribute values
        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            whereClause += """
                 AND EXISTS (
                    SELECT 1 FROM "MetaverseObjectAttributeValues" sav
                    WHERE sav."MetaverseObjectId" = m."Id"
                      AND sav."StringValue" IS NOT NULL
                      AND sav."StringValue" ILIKE @searchPattern)
                """;
            sharedParams.Add(new NpgsqlParameter("searchPattern", $"%{searchQuery}%"));
        }

        // Criteria group filters.
        // Each criterion compares the typed attribute-value column that matches the attribute's data type
        // (so the per-column indexes on MetaverseObjectAttributeValues remain usable). Within a group, criteria
        // and nested child groups are combined with AND (group type All) or OR (group type Any); the top-level
        // groups are OR-ed together, matching the in-memory semantics of ScopingEvaluationServer. An empty group
        // is always-true. No criteria groups means no filter (all objects of the type).
        if (predefinedSearch.CriteriaGroups.Count > 0)
        {
            var criteriaIdx = 0;
            // Resolve "now" once for the whole query so every relative date criterion shares one boundary.
            var nowUtc = DateTime.UtcNow;
            var groupClauses = new List<string>();
            foreach (var group in predefinedSearch.CriteriaGroups)
                groupClauses.Add(BuildPredefinedSearchGroupSql(group, ref criteriaIdx, sharedParams, nowUtc));

            // Top-level groups are OR-ed. (A single seeded group reduces to just that group's clause.)
            whereClause += $" AND ({string.Join(" OR ", groupClauses)})";
        }

        // Count query — lean, no joins
        int grossCount;
        await using (var countCmd = connection.CreateCommand())
        {
            countCmd.CommandText = $"""SELECT COUNT(*)::int FROM "MetaverseObjects" m WHERE {whereClause}""";
            foreach (var p in sharedParams)
                countCmd.Parameters.Add(p.Clone());
            grossCount = (int)(await countCmd.ExecuteScalarAsync())!;
        }

        // Page ID query — get the IDs for the current page with sorting
        string orderClause;
        NpgsqlParameter? sortParam = null;
        if (!string.IsNullOrWhiteSpace(sortBy))
        {
            // DisplayName sort uses the denormalised CachedDisplayName column directly,
            // avoiding the correlated subquery that causes ~300ms latency at 100k scale.
            if (string.Equals(sortBy, Constants.BuiltInAttributes.DisplayName, StringComparison.OrdinalIgnoreCase))
            {
                var direction = sortDescending ? "DESC" : "ASC";
                orderClause = $"""m."CachedDisplayName" {direction} NULLS LAST""";
            }
            else
            {
                // Other attributes: pre-resolve name to ID, use correlated subquery
                var sortAttrId = predefinedSearch.Attributes
                    .FirstOrDefault(a => string.Equals(a.MetaverseAttribute.Name, sortBy, StringComparison.OrdinalIgnoreCase))
                    ?.MetaverseAttribute.Id;

                if (sortAttrId == null)
                {
                    await using var lookupCmd = connection.CreateCommand();
                    lookupCmd.CommandText = """SELECT "Id" FROM "MetaverseAttributes" WHERE "Name" = @name LIMIT 1""";
                    lookupCmd.Parameters.Add(new NpgsqlParameter("name", sortBy));
                    var result = await lookupCmd.ExecuteScalarAsync();
                    sortAttrId = result as int?;
                }

                if (sortAttrId != null)
                {
                    var direction = sortDescending ? "DESC" : "ASC";
                    orderClause = $"""
                        (SELECT av."StringValue" FROM "MetaverseObjectAttributeValues" av
                         WHERE av."MetaverseObjectId" = m."Id" AND av."AttributeId" = @sortAttrId
                         LIMIT 1) {direction} NULLS LAST
                        """;
                    sortParam = new NpgsqlParameter("sortAttrId", sortAttrId.Value);
                }
                else
                {
                    // Unknown attribute name — fall back to default sort
                    orderClause = sortDescending
                        ? """m."Created" DESC"""
                        : """m."Created" ASC""";
                }
            }
        }
        else
        {
            orderClause = sortDescending
                ? """m."Created" DESC"""
                : """m."Created" ASC""";
        }

        var pageObjectIds = new List<Guid>();
        await using (var pageCmd = connection.CreateCommand())
        {
            pageCmd.CommandText = $"""
                SELECT m."Id"
                FROM "MetaverseObjects" m
                WHERE {whereClause}
                ORDER BY {orderClause}
                OFFSET @offset LIMIT @pageSize
                """;
            foreach (var p in sharedParams)
                pageCmd.Parameters.Add(p.Clone());
            pageCmd.Parameters.Add(new NpgsqlParameter("offset", offset));
            pageCmd.Parameters.Add(new NpgsqlParameter("pageSize", pageSize));
            if (sortParam != null) pageCmd.Parameters.Add(sortParam);

            await using var reader = await pageCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                pageObjectIds.Add(reader.GetGuid(0));
        }

        // Object header query — fetch the base fields for the page objects
        var headerMap = new Dictionary<Guid, MetaverseObjectHeader>();
        await using var headerCmd = connection.CreateCommand();
        headerCmd.CommandText = """
            SELECT m."Id", m."Created", m."Status", m."TypeId", t."Name" AS "TypeName", t."PluralName" AS "TypePluralName", m."CachedDisplayName"
            FROM "MetaverseObjects" m
            INNER JOIN "MetaverseObjectTypes" t ON m."TypeId" = t."Id"
            WHERE m."Id" = ANY(@objectIds)
            """;
        headerCmd.Parameters.Add(new NpgsqlParameter("objectIds", pageObjectIds.ToArray()));

        await using (var reader = await headerCmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var header = new MetaverseObjectHeader
                {
                    Id = reader.GetGuid(0),
                    Created = reader.GetDateTime(1),
                    Status = (MetaverseObjectStatus)reader.GetInt32(2),
                    TypeId = reader.GetInt32(3),
                    TypeName = reader.GetString(4),
                    TypePluralName = reader.GetString(5),
                    CachedDisplayName = reader.IsDBNull(6) ? null : reader.GetString(6),
                    AttributeValues = []
                };
                headerMap[header.Id] = header;
            }
        }

        // Attribute value query — fetch and attach to headers
        await using var attrCmd = connection.CreateCommand();
        attrCmd.CommandText = """
            SELECT av."Id", av."MetaverseObjectId", av."AttributeId",
                   ma."Id" AS "AttrId", ma."Name" AS "AttrName", ma."Type" AS "AttrType", ma."AttributePlurality" AS "AttrPlurality",
                   av."StringValue", av."DateTimeValue", av."IntValue", av."LongValue", av."BoolValue", av."GuidValue"
            FROM "MetaverseObjectAttributeValues" av
            INNER JOIN "MetaverseAttributes" ma ON av."AttributeId" = ma."Id"
            WHERE av."MetaverseObjectId" = ANY(@objectIds) AND av."AttributeId" = ANY(@attrIds)
            """;
        attrCmd.Parameters.Add(new NpgsqlParameter("objectIds", pageObjectIds.ToArray()));
        attrCmd.Parameters.Add(new NpgsqlParameter("attrIds", returnAttributeIds.ToArray()));

        await using (var reader = await attrCmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var mvoId = reader.GetGuid(1);
                if (!headerMap.TryGetValue(mvoId, out var header))
                    continue;

                header.AttributeValues.Add(new MetaverseObjectAttributeValue
                {
                    Id = reader.GetGuid(0),
                    AttributeId = reader.GetInt32(2),
                    Attribute = new MetaverseAttribute
                    {
                        Id = reader.GetInt32(3),
                        Name = reader.GetString(4),
                        Type = (AttributeDataType)reader.GetInt32(5),
                        AttributePlurality = (AttributePlurality)reader.GetInt32(6)
                    },
                    StringValue = reader.IsDBNull(7) ? null : reader.GetString(7),
                    DateTimeValue = reader.IsDBNull(8) ? null : reader.GetDateTime(8),
                    IntValue = reader.IsDBNull(9) ? null : reader.GetInt32(9),
                    LongValue = reader.IsDBNull(10) ? null : reader.GetInt64(10),
                    BoolValue = reader.IsDBNull(11) ? null : reader.GetBoolean(11),
                    GuidValue = reader.IsDBNull(12) ? null : reader.GetGuid(12)
                });
            }
        }

        // Preserve the sort order from the page query
        var results = pageObjectIds
            .Where(id => headerMap.ContainsKey(id))
            .Select(id => headerMap[id])
            .ToList();

        var pagedResultSet = new PagedResultSet<MetaverseObjectHeader>
        {
            PageSize = pageSize,
            TotalResults = grossCount,
            CurrentPage = page,
            Results = results
        };

        if (page == 1 && pagedResultSet.TotalPages == 0)
            return pagedResultSet;

        // don't let users try and request a page that doesn't exist
        if (page <= pagedResultSet.TotalPages)
            return pagedResultSet;

        pagedResultSet.TotalResults = 0;
        pagedResultSet.Results.Clear();
        return pagedResultSet;
    }

    /// <summary>
    /// Gets a paginated list of Metaverse Objects with optional filtering by type, search query, or specific attribute value.
    /// </summary>
    /// <param name="page">The page number to retrieve (1-based).</param>
    /// <param name="pageSize">The number of items per page (max 100).</param>
    /// <param name="objectTypeId">Optional filter by object type ID.</param>
    /// <param name="searchQuery">Optional search query that filters by display name (case-insensitive, supports partial match).</param>
    /// <param name="sortDescending">Sort by created date descending (true) or ascending (false).</param>
    /// <param name="attributes">Optional list of attribute names to include in the response. Use "*" for all attributes.</param>
    /// <param name="filterAttributeName">Optional attribute name to filter by (must be used with filterAttributeValue).</param>
    /// <param name="filterAttributeValue">Optional attribute value to filter by (exact match, case-insensitive).</param>
    public async Task<PagedResultSet<MetaverseObjectHeader>> GetMetaverseObjectsAsync(
        int page,
        int pageSize,
        int? objectTypeId = null,
        string? searchQuery = null,
        bool sortDescending = true,
        IEnumerable<string>? attributes = null,
        string? filterAttributeName = null,
        string? filterAttributeValue = null)
    {
        if (pageSize < 1)
            throw new ArgumentOutOfRangeException(nameof(pageSize), "pageSize must be a positive number");

        if (page < 1)
            page = 1;

        // limit page size to avoid increasing latency unnecessarily
        if (pageSize > 100)
            pageSize = 100;

        // Build the set of attribute names to include - always include DisplayName
        // Use "*" wildcard to include all attributes
        var includeAllAttributes = attributes?.Contains("*") == true;
        HashSet<string>? attributeNames = null;
        if (!includeAllAttributes)
        {
            attributeNames = new HashSet<string> { Constants.BuiltInAttributes.DisplayName };
            if (attributes != null)
            {
                foreach (var attr in attributes)
                {
                    if (!string.IsNullOrWhiteSpace(attr))
                        attributeNames.Add(attr);
                }
            }
        }

        // construct the base query
        var objects = Repository.Database.MetaverseObjects
            .AsSplitQuery()
            .Include(mo => mo.Type)
            .Include(mo => mo.AttributeValues)
            .ThenInclude(av => av.Attribute)
            .AsQueryable();

        // filter by object type if specified
        if (objectTypeId.HasValue)
        {
            objects = objects.Where(q => q.Type.Id == objectTypeId.Value);
        }

        // filter by search query (searches display name attribute)
        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            objects = objects.Where(q =>
                q.AttributeValues.Any(av =>
                    av.Attribute.Name == Constants.BuiltInAttributes.DisplayName &&
                    av.StringValue != null &&
                    EF.Functions.ILike(av.StringValue, $"%{searchQuery}%")));
        }

        // filter by specific attribute name and value (exact match, case-insensitive)
        if (!string.IsNullOrWhiteSpace(filterAttributeName) && filterAttributeValue != null)
        {
            objects = objects.Where(q =>
                q.AttributeValues.Any(av =>
                    av.Attribute.Name == filterAttributeName &&
                    av.StringValue != null &&
                    EF.Functions.ILike(av.StringValue, filterAttributeValue)));
        }

        // apply sorting
        objects = sortDescending
            ? objects.OrderByDescending(q => q.Created)
            : objects.OrderBy(q => q.Created);

        // get total count
        var grossCount = await objects.CountAsync();

        // apply pagination
        var offset = (page - 1) * pageSize;
        var results = await objects
            .Skip(offset)
            .Take(pageSize)
            .Select(d => new MetaverseObjectHeader
            {
                Id = d.Id,
                Created = d.Created,
                Status = d.Status,
                TypeId = d.Type.Id,
                TypeName = d.Type.Name,
                TypePluralName = d.Type.PluralName,
                AttributeValues = includeAllAttributes
                    ? d.AttributeValues.ToList()
                    : d.AttributeValues
                        .Where(av => attributeNames!.Contains(av.Attribute.Name))
                        .ToList()
            })
            .ToListAsync();

        var pagedResultSet = new PagedResultSet<MetaverseObjectHeader>
        {
            PageSize = pageSize,
            TotalResults = grossCount,
            CurrentPage = page,
            Results = results
        };

        if (page == 1 && pagedResultSet.TotalPages == 0)
            return pagedResultSet;

        // don't let users try and request a page that doesn't exist
        if (page <= pagedResultSet.TotalPages)
            return pagedResultSet;

        pagedResultSet.TotalResults = 0;
        pagedResultSet.Results.Clear();
        return pagedResultSet;
    }

    /// <summary>
    /// Attempts to find a single Metaverse Object using criteria from a SyncRuleMapping object and attribute values from a Connected System Object.
    /// This is to help the process of joining a CSO to an MVO.
    /// </summary>
    /// <param name="connectedSystemObject">The source object to try and find a matching Metaverse Object for.</param>
    /// <param name="metaverseObjectType">The type of Metaverse Object to search for.</param>
    /// <param name="syncRuleMapping">The Synchronisation Rule Mapping contains the logic needed to construct a Metaverse Object query.</param>
    /// <returns>A Metaverse Object if a single result is found, otherwise null.</returns>
    /// <exception cref="NotImplementedException">Will be thrown if more than one source is specified (advanced matching). This is not yet supported.</exception>
    /// <exception cref="ArgumentNullException">Will be thrown if the Synchronisation Rule mapping source Connected System attribute is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Will be thrown if an unsupported attribute type is specified.</exception>
    /// <exception cref="MultipleMatchesException">Will be thrown if there's more than one Metaverse Object that matches the matching rule criteria.</exception>
    public async Task<MetaverseObject?> FindMetaverseObjectUsingMatchingRuleAsync(ConnectedSystemObject connectedSystemObject, MetaverseObjectType metaverseObjectType, ObjectMatchingRule objectMatchingRule)
    {
        if (objectMatchingRule.Sources.Count > 1)
            throw new NotImplementedException("Object Matching Rules with more than one source are not yet supported (advanced matching).");

        // at this point in development, we expect and can process a single source.
        var source = objectMatchingRule.Sources[0];
        if (source.ConnectedSystemAttribute == null)
            throw new InvalidDataException("objectMatchingRule.Sources[0].ConnectedSystemAttribute is null");

        // get the source attribute value(s)
        var csoAttributeValues = connectedSystemObject.AttributeValues.Where(q => q.AttributeId == source.ConnectedSystemAttribute.Id);

        // try and find a match for any of the source attribute values.
        // this enables an MVA such as 'CN' to be used as a matching attribute.
        foreach (var csoAttributeValue in csoAttributeValues)
        {
            // Skip null values - null is always a non-match
            var hasValue = source.ConnectedSystemAttribute.Type switch
            {
                AttributeDataType.Text => !string.IsNullOrEmpty(csoAttributeValue.StringValue),
                AttributeDataType.Number => csoAttributeValue.IntValue.HasValue,
                AttributeDataType.Guid => csoAttributeValue.GuidValue.HasValue,
                _ => false
            };

            if (!hasValue)
            {
                Log.Debug("FindMetaverseObjectUsingMatchingRuleAsync: Skipping null/empty attribute value for {AttributeName}",
                    source.ConnectedSystemAttribute.Name);
                continue;
            }

            // Phase 1: Lightweight match — query only MVO IDs (no Include chains, no entity materialisation).
            // This avoids the expensive AsSplitQuery + Include overhead when there's no match (common case).
            var matchQuery = Repository.Database.MetaverseObjects
                .Where(mvo => mvo.Type.Id == metaverseObjectType.Id);

            // Apply attribute-type-specific filter
            switch (source.ConnectedSystemAttribute.Type)
            {
                case AttributeDataType.Text:
                    // Check case sensitivity setting on the matching rule
                    if (objectMatchingRule.CaseSensitive)
                    {
                        matchQuery = matchQuery.Where(mvo =>
                            mvo.AttributeValues.Any(av =>
                                objectMatchingRule.TargetMetaverseAttribute != null &&
                                av.Attribute.Id == objectMatchingRule.TargetMetaverseAttribute.Id &&
                                av.StringValue != null &&
                                av.StringValue == csoAttributeValue.StringValue));
                    }
                    else
                    {
                        matchQuery = matchQuery.Where(mvo =>
                            mvo.AttributeValues.Any(av =>
                                objectMatchingRule.TargetMetaverseAttribute != null &&
                                av.Attribute.Id == objectMatchingRule.TargetMetaverseAttribute.Id &&
                                av.StringValue != null &&
                                EF.Functions.ILike(av.StringValue, csoAttributeValue.StringValue!)));
                    }
                    break;
                case AttributeDataType.Number:
                    matchQuery = matchQuery.Where(mvo =>
                        mvo.AttributeValues.Any(av =>
                            objectMatchingRule.TargetMetaverseAttribute != null &&
                            av.Attribute.Id == objectMatchingRule.TargetMetaverseAttribute.Id &&
                            av.IntValue != null &&
                            av.IntValue == csoAttributeValue.IntValue));
                    break;
                case AttributeDataType.DateTime:
                    throw new NotSupportedException("DateTime attributes are not supported in Object Matching Rules.");
                case AttributeDataType.Binary:
                    throw new NotSupportedException("Binary attributes are not supported in Object Matching Rules.");
                case AttributeDataType.Reference:
                    throw new NotSupportedException("Reference attributes are not supported in Object Matching Rules.");
                case AttributeDataType.Guid:
                    matchQuery = matchQuery.Where(mvo =>
                        mvo.AttributeValues.Any(av =>
                            objectMatchingRule.TargetMetaverseAttribute != null &&
                            av.Attribute.Id == objectMatchingRule.TargetMetaverseAttribute.Id &&
                            av.GuidValue != null &&
                            av.GuidValue == csoAttributeValue.GuidValue));
                    break;
                case AttributeDataType.Boolean:
                    throw new NotSupportedException("Boolean attributes are not supported in Object Matching Rules.");
                case AttributeDataType.NotSet:
                default:
                    throw new InvalidDataException("Unexpected Connected System Attribute Type");
            }

            // Only select IDs — avoids materialising full entity graphs with all attribute values.
            // Take(2) to detect ambiguous matches without loading more than needed.
            var matchingIds = await matchQuery.OrderBy(mvo => mvo.Id).Select(mvo => mvo.Id).Take(2).ToListAsync();

            switch (matchingIds.Count)
            {
                case 0:
                    continue;
                case > 1:
                    throw new MultipleMatchesException(
                        "Multiple Metaverse Objects were found to match the source attribute.",
                        matchingIds);
                default:
                {
                    // Phase 2: Load the full MVO entity by PK with all navigation properties needed for sync.
                    var mvo = await GetMetaverseObjectAsync(matchingIds[0]);
                    return mvo;
                }
            }
        }

        // no match
        return null;
    }

    /// <summary>
    /// Deletes a Metaverse Object from the database.
    /// </summary>
    /// <param name="metaverseObject">The Metaverse Object to delete.</param>
    public async Task DeleteMetaverseObjectAsync(MetaverseObject metaverseObject)
    {
        // Null out the FK references in related tables to preserve audit history before deletion.

        // Null out FK reference in Activities to preserve audit history
        await Repository.Database.Database.ExecuteSqlRawAsync(
            @"UPDATE ""Activities"" SET ""MetaverseObjectId"" = NULL WHERE ""MetaverseObjectId"" = {0}",
            metaverseObject.Id);

        // Stamp DeletedMetaverseObjectId on all prior change records for this MVO so that
        // GetDeletedMvoChangeHistoryAsync can correlate them after the FK is nulled.
        // Then null the FK to allow the MVO to be deleted without constraint violations.
        await Repository.Database.Database.ExecuteSqlRawAsync(
            @"UPDATE ""MetaverseObjectChanges"" SET ""DeletedMetaverseObjectId"" = {0}, ""MetaverseObjectId"" = NULL WHERE ""MetaverseObjectId"" = {0}",
            metaverseObject.Id);

        // Null out FK reference in ConnectedSystemObjects to detach any CSOs still joined
        // to this MVO. Without this, SaveChangesAsync may try to flush tracked CSO modifications
        // that reference the now-deleted MVO, causing FK constraint violations when multiple
        // MVOs are deleted in the same batch.
        // Also update tracked entities in EF Core's change tracker to match the DB state,
        // otherwise SaveChangesAsync will try to write the stale FK value.
        await Repository.Database.Database.ExecuteSqlRawAsync(
            @"UPDATE ""ConnectedSystemObjects"" SET ""MetaverseObjectId"" = NULL WHERE ""MetaverseObjectId"" = {0}",
            metaverseObject.Id);
        foreach (var trackedCso in Repository.Database.ChangeTracker.Entries<ConnectedSystemObject>()
            .Where(e => e.Entity.MetaverseObjectId == metaverseObject.Id))
        {
            trackedCso.Entity.MetaverseObjectId = null;
            trackedCso.Entity.MetaverseObject = null;
        }

        // Null out reference attribute values on other MVOs that point to this MVO.
        // Without this, deleting an MVO that is referenced (e.g., as a Manager) by other
        // MVOs would violate the FK constraint on MetaverseObjectAttributeValues.ReferenceValueId.
        await Repository.Database.Database.ExecuteSqlRawAsync(
            @"UPDATE ""MetaverseObjectAttributeValues"" SET ""ReferenceValueId"" = NULL WHERE ""ReferenceValueId"" = {0}",
            metaverseObject.Id);

        // Null out reference values in change tracking attribute records that point to this MVO.
        // Change history records may reference this MVO (e.g., "Manager was set to Alice")
        // and must be preserved with a null reference rather than blocking deletion.
        await Repository.Database.Database.ExecuteSqlRawAsync(
            @"UPDATE ""MetaverseObjectChangeAttributeValues"" SET ""ReferenceValueId"" = NULL WHERE ""ReferenceValueId"" = {0}",
            metaverseObject.Id);

        Repository.Database.MetaverseObjects.Remove(metaverseObject);
        await Repository.Database.SaveChangesAsync();
    }

    /// <summary>
    /// Explicitly loads the AttributeValues (and their Attribute navigation) for an MVO
    /// that was queried without them. Used to capture final attribute state before deletion.
    /// </summary>
    /// <param name="metaverseObject">The MVO to load attribute values for.</param>
    public async Task LoadMetaverseObjectAttributeValuesAsync(MetaverseObject metaverseObject)
    {
        await Repository.Database.Entry(metaverseObject)
            .Collection(mvo => mvo.AttributeValues)
            .Query()
            .Include(av => av.Attribute)
            .LoadAsync();
    }

    /// <inheritdoc />
    public async Task SetDeletedMetaverseObjectIdAsync(Guid changeId, Guid metaverseObjectId)
    {
        await Repository.Database.Database.ExecuteSqlRawAsync(
            @"UPDATE ""MetaverseObjectChanges"" SET ""DeletedMetaverseObjectId"" = {0} WHERE ""Id"" = {1}",
            metaverseObjectId, changeId);
    }

    /// <summary>
    /// Gets Metaverse Objects that are eligible for automatic deletion based on deletion rules.
    /// Returns MVOs where:
    /// - Origin = Projected (not Internal - protects admin accounts)
    /// - Type.DeletionRule = WhenLastConnectorDisconnected (requires no remaining CSOs)
    ///   OR Type.DeletionRule = WhenAuthoritativeSourceDisconnected (may still have CSOs)
    /// - LastConnectorDisconnectedDate + DeletionGracePeriod less than or equal to now
    /// </summary>
    public async Task<List<MetaverseObject>> GetMetaverseObjectsEligibleForDeletionAsync(int maxResults = 100)
    {
        var now = DateTime.UtcNow;

        var eligibleObjects = await Repository.Database.MetaverseObjects
            .AsSplitQuery()
            .Include(mvo => mvo.Type)
            .Include(mvo => mvo.ConnectedSystemObjects)
            .Where(mvo =>
                // Must be a projected object (not internal like admin accounts)
                mvo.Origin == MetaverseObjectOrigin.Projected &&
                mvo.Type != null &&
                // Must have been marked for deletion (has a last connector disconnected date)
                mvo.LastConnectorDisconnectedDate != null &&
                // Must match a supported automatic deletion rule
                (
                    // WhenLastConnectorDisconnected: all CSOs must be gone
                    (mvo.Type.DeletionRule == MetaverseObjectDeletionRule.WhenLastConnectorDisconnected &&
                     !mvo.ConnectedSystemObjects.Any()) ||
                    // WhenAuthoritativeSourceDisconnected: authoritative source triggered deletion,
                    // MVO may still have remaining target CSOs (housekeeping will handle their export deletion)
                    mvo.Type.DeletionRule == MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected
                ) &&
                // Grace period must have elapsed (or no grace period configured)
                (mvo.Type.DeletionGracePeriod == null ||
                 mvo.Type.DeletionGracePeriod == TimeSpan.Zero ||
                 mvo.LastConnectorDisconnectedDate.Value + mvo.Type.DeletionGracePeriod.Value <= now))
            .OrderBy(mvo => mvo.LastConnectorDisconnectedDate)
            .Take(maxResults)
            .ToListAsync();

        return eligibleObjects;
    }

    public async Task<List<MetaverseObject>> GetMvosOrphanedByConnectedSystemDeletionAsync(int connectedSystemId)
    {
        // Find MVOs that:
        // 1. Are projected (not internal admin accounts)
        // 2. Have deletion rule WhenLastConnectorDisconnected
        // 3. Have ALL their CSOs in the specified Connected System (will become orphaned)
        var orphanedMvos = await Repository.Database.MetaverseObjects
            .AsSplitQuery()
            .Include(mvo => mvo.Type)
            .Include(mvo => mvo.ConnectedSystemObjects)
            .Where(mvo =>
                // Must be a projected object (not internal like admin accounts)
                mvo.Origin == MetaverseObjectOrigin.Projected &&
                // Must have a type with WhenLastConnectorDisconnected deletion rule
                mvo.Type != null &&
                mvo.Type.DeletionRule == MetaverseObjectDeletionRule.WhenLastConnectorDisconnected &&
                // Must have at least one CSO in the system being deleted
                mvo.ConnectedSystemObjects.Any(cso => cso.ConnectedSystemId == connectedSystemId) &&
                // Must NOT have any CSOs in OTHER Connected Systems (would become orphaned)
                !mvo.ConnectedSystemObjects.Any(cso => cso.ConnectedSystemId != connectedSystemId))
            .ToListAsync();

        return orphanedMvos;
    }

    public async Task<int> MarkMvosAsDisconnectedAsync(IEnumerable<Guid> mvoIds)
    {
        var mvoIdList = mvoIds.ToList();
        if (mvoIdList.Count == 0)
            return 0;

        var now = DateTime.UtcNow;

        // Use raw SQL for efficiency with large numbers of MVOs
        var rowsAffected = await Repository.Database.Database.ExecuteSqlRawAsync(
            @"UPDATE ""MetaverseObjects""
              SET ""LastConnectorDisconnectedDate"" = {0}
              WHERE ""Id"" = ANY({1})
                AND ""LastConnectorDisconnectedDate"" IS NULL",
            now, mvoIdList.ToArray());

        return rowsAffected;
    }

    public async Task<PagedResultSet<MetaverseObject>> GetMetaverseObjectsPendingDeletionAsync(
        int page,
        int pageSize,
        int? objectTypeId = null)
    {
        if (pageSize < 1)
            throw new ArgumentOutOfRangeException(nameof(pageSize), "pageSize must be a positive number");

        if (page < 1)
            page = 1;

        // limit page size to avoid increasing latency unnecessarily
        if (pageSize > 100)
            pageSize = 100;

        // Build base query for MVOs pending deletion
        var query = Repository.Database.MetaverseObjects
            .AsSplitQuery()
            .Include(mvo => mvo.Type)
            .Include(mvo => mvo.AttributeValues)
            .ThenInclude(av => av.Attribute)
            .Include(mvo => mvo.ConnectedSystemObjects)
            .Where(mvo =>
                // Must have LastConnectorDisconnectedDate set (pending deletion)
                mvo.LastConnectorDisconnectedDate != null &&
                // Must be projected (not internal admin accounts)
                mvo.Origin == MetaverseObjectOrigin.Projected &&
                // Must have deletion rule WhenLastConnectorDisconnected
                mvo.Type != null &&
                mvo.Type.DeletionRule == MetaverseObjectDeletionRule.WhenLastConnectorDisconnected);

        // Apply object type filter if specified
        if (objectTypeId.HasValue)
        {
            query = query.Where(mvo => mvo.Type.Id == objectTypeId.Value);
        }

        // Order by deletion eligible date (soonest first)
        query = query.OrderBy(mvo => mvo.LastConnectorDisconnectedDate);

        // Get total count
        var totalCount = await query.CountAsync();

        // Apply pagination
        var offset = (page - 1) * pageSize;
        var results = await query
            .Skip(offset)
            .Take(pageSize)
            .ToListAsync();

        return new PagedResultSet<MetaverseObject>
        {
            PageSize = pageSize,
            TotalResults = totalCount,
            CurrentPage = page,
            Results = results
        };
    }

    /// <inheritdoc />
    public async Task<int> GetMetaverseObjectsCountAsync(
        int? objectTypeId = null,
        string? searchQuery = null,
        string? filterAttributeName = null,
        string? filterAttributeValue = null)
    {
        // Build a lean count query: no Includes, no AsSplitQuery, no Select projections.
        var query = Repository.Database.MetaverseObjects.AsQueryable();

        if (objectTypeId.HasValue)
        {
            var typeId = objectTypeId.Value;
            query = query.Where(mo => mo.Type.Id == typeId);
        }

        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            query = query.Where(mo =>
                mo.AttributeValues.Any(av =>
                    av.Attribute.Name == Constants.BuiltInAttributes.DisplayName &&
                    av.StringValue != null &&
                    EF.Functions.ILike(av.StringValue, $"%{searchQuery}%")));
        }

        if (!string.IsNullOrWhiteSpace(filterAttributeName) && filterAttributeValue != null)
        {
            query = query.Where(mo =>
                mo.AttributeValues.Any(av =>
                    av.Attribute.Name == filterAttributeName &&
                    av.StringValue != null &&
                    EF.Functions.ILike(av.StringValue, filterAttributeValue)));
        }

        return await query.CountAsync();
    }

    public async Task<int> GetMetaverseObjectsPendingDeletionCountAsync(int? objectTypeId = null)
    {
        var query = Repository.Database.MetaverseObjects
            .Where(mvo =>
                // Must have LastConnectorDisconnectedDate set (pending deletion)
                mvo.LastConnectorDisconnectedDate != null &&
                // Must be projected (not internal admin accounts)
                mvo.Origin == MetaverseObjectOrigin.Projected &&
                // Must have deletion rule WhenLastConnectorDisconnected
                mvo.Type != null &&
                mvo.Type.DeletionRule == MetaverseObjectDeletionRule.WhenLastConnectorDisconnected);

        // Apply object type filter if specified
        if (objectTypeId.HasValue)
        {
            query = query.Where(mvo => mvo.Type.Id == objectTypeId.Value);
        }

        return await query.CountAsync();
    }

    /// <inheritdoc />
    public async Task CreateMetaverseObjectChangeAsync(MetaverseObjectChange change)
    {
        Repository.Database.MetaverseObjectChanges.Add(change);
        await Repository.Database.SaveChangesAsync();
    }

    /// <summary>
    /// Inserts a MetaverseObjectChange and its attribute changes via raw SQL,
    /// bypassing the EF Core change tracker entirely. This avoids premature flushes
    /// of other tracked entities (e.g., CSOs with stale FK references to a deleted MVO)
    /// that would cause FK constraint violations during SaveChangesAsync.
    /// </summary>
    public async Task CreateMetaverseObjectChangeDirectAsync(MetaverseObjectChange change)
    {
        var changeId = Guid.NewGuid();
        change.Id = changeId;

        await Repository.Database.Database.ExecuteSqlRawAsync(
            @"INSERT INTO ""MetaverseObjectChanges"" (""Id"", ""ChangeType"", ""ChangeTime"", ""InitiatedByType"", ""InitiatedById"", ""InitiatedByName"", ""ChangeInitiatorType"", ""DeletedMetaverseObjectId"", ""DeletedObjectTypeId"", ""DeletedObjectDisplayName"")
              VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {8}, {9})",
            changeId,
            (int)change.ChangeType,
            change.ChangeTime,
            (int)change.InitiatedByType,
            BulkSqlHelpers.NullableParam(change.InitiatedById, NpgsqlTypes.NpgsqlDbType.Uuid),
            BulkSqlHelpers.NullableParam(change.InitiatedByName, NpgsqlTypes.NpgsqlDbType.Text),
            (int)change.ChangeInitiatorType,
            BulkSqlHelpers.NullableParam(change.DeletedMetaverseObjectId, NpgsqlTypes.NpgsqlDbType.Uuid),
            BulkSqlHelpers.NullableParam(change.DeletedObjectTypeId, NpgsqlTypes.NpgsqlDbType.Integer),
            BulkSqlHelpers.NullableParam(change.DeletedObjectDisplayName, NpgsqlTypes.NpgsqlDbType.Text));

        // Insert attribute changes and their values
        foreach (var attrChange in change.AttributeChanges)
        {
            var attrChangeId = Guid.NewGuid();
            attrChange.Id = attrChangeId;

            await Repository.Database.Database.ExecuteSqlRawAsync(
                @"INSERT INTO ""MetaverseObjectChangeAttributes"" (""Id"", ""MetaverseObjectChangeId"", ""AttributeId"", ""AttributeName"", ""AttributeType"")
                  VALUES ({0}, {1}, {2}, {3}, {4})",
                attrChangeId, changeId, attrChange.Attribute!.Id, attrChange.AttributeName, (int)attrChange.AttributeType);

            foreach (var valueChange in attrChange.ValueChanges)
            {
                var valueChangeId = Guid.NewGuid();
                valueChange.Id = valueChangeId;

                await Repository.Database.Database.ExecuteSqlRawAsync(
                    @"INSERT INTO ""MetaverseObjectChangeAttributeValues"" (""Id"", ""MetaverseObjectChangeAttributeId"", ""ValueChangeType"", ""StringValue"", ""IntValue"", ""GuidValue"", ""BoolValue"", ""DateTimeValue"", ""ByteValueLength"", ""ReferenceValueId"")
                      VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {8}, {9})",
                    valueChangeId,
                    attrChangeId,
                    (int)valueChange.ValueChangeType,
                    BulkSqlHelpers.NullableParam(valueChange.StringValue, NpgsqlTypes.NpgsqlDbType.Text),
                    BulkSqlHelpers.NullableParam(valueChange.IntValue, NpgsqlTypes.NpgsqlDbType.Integer),
                    BulkSqlHelpers.NullableParam(valueChange.GuidValue, NpgsqlTypes.NpgsqlDbType.Uuid),
                    BulkSqlHelpers.NullableParam(valueChange.BoolValue, NpgsqlTypes.NpgsqlDbType.Boolean),
                    BulkSqlHelpers.NullableParam(valueChange.DateTimeValue, NpgsqlTypes.NpgsqlDbType.TimestampTz),
                    BulkSqlHelpers.NullableParam(valueChange.ByteValueLength, NpgsqlTypes.NpgsqlDbType.Integer),
                    BulkSqlHelpers.NullableParam(valueChange.ReferenceValue?.Id, NpgsqlTypes.NpgsqlDbType.Uuid));
            }
        }
    }

    /// <inheritdoc />
    public async Task<(List<MetaverseObjectChange> Items, int TotalCount)> GetDeletedMvoChangesAsync(
        int? objectTypeId = null,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        string? displayNameSearch = null,
        int page = 1,
        int pageSize = 50)
    {
        var query = Repository.Database.MetaverseObjectChanges
            .Where(c => c.ChangeType == ObjectChangeType.Deleted && c.MetaverseObject == null);

        // Apply filters
        if (objectTypeId.HasValue)
            query = query.Where(c => c.DeletedObjectTypeId == objectTypeId.Value);

        if (fromDate.HasValue)
            query = query.Where(c => c.ChangeTime >= fromDate.Value);

        if (toDate.HasValue)
            query = query.Where(c => c.ChangeTime <= toDate.Value);

        if (!string.IsNullOrWhiteSpace(displayNameSearch))
        {
            query = query.Where(c =>
                c.DeletedObjectDisplayName != null &&
                c.DeletedObjectDisplayName.Contains(displayNameSearch));
        }

        // Get total count before pagination
        var totalCount = await query.CountAsync();

        // Apply ordering and pagination
        var items = await query
            .OrderByDescending(c => c.ChangeTime)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Include(c => c.DeletedObjectType)
            .ToListAsync();

        return (items, totalCount);
    }

    /// <inheritdoc />
    public async Task<List<MetaverseObjectChange>> GetDeletedMvoChangeHistoryAsync(Guid changeId)
    {
        // First, get the Delete change record
        var targetChange = await Repository.Database.MetaverseObjectChanges
            .OrderBy(c => c.Id)
            .FirstOrDefaultAsync(c => c.Id == changeId);

        if (targetChange == null)
            return new List<MetaverseObjectChange>();

        // Resolve the original MVO ID from the deletion change record.
        // DeletedMetaverseObjectId is explicitly set via raw SQL on prior change records,
        // and via SetDeletedMetaverseObjectIdAsync on the deletion record itself.
        // As a fallback, EF Core may auto-set MetaverseObjectId via relationship fixup.
        var mvoId = targetChange.DeletedMetaverseObjectId
            ?? (targetChange.MetaverseObject != null ? targetChange.MetaverseObject.Id : (Guid?)null);

        // Also check the shadow FK property directly (EF Core stores it even without navigation loaded)
        if (!mvoId.HasValue || mvoId.Value == Guid.Empty)
        {
            var shadowFk = Repository.Database.Entry(targetChange).Property<Guid?>("MetaverseObjectId").CurrentValue;
            if (shadowFk.HasValue && shadowFk.Value != Guid.Empty)
                mvoId = shadowFk;
        }

        // If we have the MVO ID, find ALL change records using DeletedMetaverseObjectId
        // (set on prior records by raw SQL) OR MetaverseObjectId (set on deletion record by EF Core).
        if (mvoId.HasValue && mvoId.Value != Guid.Empty)
        {
            return await Repository.Database.MetaverseObjectChanges
                .AsSplitQuery()
                .Where(c => c.DeletedMetaverseObjectId == mvoId || c.Id == changeId)
                .OrderByDescending(c => c.ChangeTime)
                .Include(c => c.ActivityRunProfileExecutionItem)
                .ThenInclude(rpei => rpei!.Activity)
                .Include(c => c.AttributeChanges)
                .ThenInclude(ac => ac.Attribute)
                .Include(c => c.AttributeChanges)
                .ThenInclude(ac => ac.ValueChanges)
                .ThenInclude(vc => vc.ReferenceValue)
                .ToListAsync();
        }

        // Fallback: return only the Delete change with its attribute changes loaded
        return await Repository.Database.MetaverseObjectChanges
            .AsSplitQuery()
            .Where(c => c.Id == changeId)
            .Include(c => c.AttributeChanges)
            .ThenInclude(ac => ac.Attribute)
            .Include(c => c.AttributeChanges)
            .ThenInclude(ac => ac.ValueChanges)
            .ThenInclude(vc => vc.ReferenceValue)
            .ToListAsync();
    }

    public async Task<PagedResultSet<MetaverseObjectAttributeValue>> GetAttributeValuesPagedAsync(
        Guid metaverseObjectId,
        string attributeName,
        int page,
        int pageSize,
        string? searchText = null)
    {
        var query = Repository.Database.Set<MetaverseObjectAttributeValue>()
            .Where(av => av.MetaverseObject.Id == metaverseObjectId
                         && av.Attribute.Name == attributeName);

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            var search = searchText.ToLowerInvariant();
            query = query.Where(av =>
                (av.StringValue != null && av.StringValue.ToLower().Contains(search))
                || (av.ReferenceValue != null && av.ReferenceValue.AttributeValues
                    .Any(rav => rav.StringValue != null && rav.StringValue.ToLower().Contains(search)))
            );
        }

        var totalCount = await query.CountAsync();

        // AsTracking required: Include path AttributeValue -> ReferenceValue(MVO) -> AttributeValues creates a cycle.
        var values = await query
            .AsTracking()
            .AsSplitQuery()
            .OrderBy(av => av.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Include(av => av.Attribute)
            .Include(av => av.ReferenceValue)
            .ThenInclude(rv => rv!.Type)
            .Include(av => av.ReferenceValue)
            .ThenInclude(rv => rv!.AttributeValues.Where(rvav => rvav.Attribute.Name == Constants.BuiltInAttributes.DisplayName))
            .ThenInclude(rvav => rvav.Attribute)
            .ToListAsync();

        return new PagedResultSet<MetaverseObjectAttributeValue>
        {
            Results = values,
            TotalResults = totalCount,
            CurrentPage = page,
            PageSize = pageSize
        };
    }

    public async Task<List<Guid>> GetMetaverseObjectIdsByDateAttributeRangeAsync(int metaverseObjectTypeId, int attributeId, DateTime? afterUtc, DateTime throughUtc)
    {
        // Superset candidate selection for the outbound (export) lane of the Temporal Scope Reconciler (#892).
        // The composite (AttributeId, DateTimeValue) partial index serves the equality-then-range predicate;
        // "DateTimeValue" IS NOT NULL also excludes asserted-null marker rows. Filtering by object type is
        // required because a Metaverse Attribute is shared across types. The final in/out-of-scope decision is
        // the reconciler's in-memory full evaluation, so a generous window here is safe.
        var sql = @"SELECT DISTINCT av.""MetaverseObjectId"" AS ""Value""
                    FROM ""MetaverseObjectAttributeValues"" av
                    INNER JOIN ""MetaverseObjects"" mvo ON mvo.""Id"" = av.""MetaverseObjectId""
                    WHERE av.""AttributeId"" = {0}
                      AND mvo.""TypeId"" = {1}
                      AND av.""DateTimeValue"" IS NOT NULL
                      AND av.""DateTimeValue"" <= {2}";
        var parameters = new List<object> { attributeId, metaverseObjectTypeId, throughUtc };
        if (afterUtc.HasValue)
        {
            sql += @"
                      AND av.""DateTimeValue"" > {3}";
            parameters.Add(afterUtc.Value);
        }

        return await Repository.Database.Database
            .SqlQueryRaw<Guid>(sql, parameters.ToArray())
            .ToListAsync();
    }

    #endregion

}
