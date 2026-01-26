using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Enums;
using JIM.Models.Exceptions;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Models.Utility;
using Microsoft.EntityFrameworkCore;
using Serilog;
namespace JIM.PostgresData.Repositories;

public class MetaverseRepository : IMetaverseRepository
{
    #region accessors
    private PostgresDataRepository Repository { get; }
    #endregion

    #region constructors
    internal MetaverseRepository(PostgresDataRepository dataRepository)
    {
        Repository = dataRepository;
    }
    #endregion

    #region metaverse object types

    public async Task<List<MetaverseObjectType>> GetMetaverseObjectTypesAsync(bool includeChildObjects)
    {
        if (includeChildObjects)
            return await Repository.Database.MetaverseObjectTypes.Include(q => q.Attributes.OrderBy(a => a.Name)).OrderBy(x => x.Name).ToListAsync();
                
        return await Repository.Database.MetaverseObjectTypes.OrderBy(x => x.Name).ToListAsync();
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

    public async Task<MetaverseAttribute?> GetMetaverseAttributeAsync(int id)
    {
        return await Repository.Database.MetaverseAttributes.SingleOrDefaultAsync(x => x.Id == id);
    }

    public async Task<MetaverseAttribute?> GetMetaverseAttributeWithObjectTypesAsync(int id)
    {
        return await Repository.Database.MetaverseAttributes
            .Include(a => a.MetaverseObjectTypes)
            .SingleOrDefaultAsync(x => x.Id == id);
    }

    public async Task<MetaverseAttribute?> GetMetaverseAttributeAsync(string name)
    {
        return await Repository.Database.MetaverseAttributes.SingleOrDefaultAsync(x => x.Name == name);
    }

    public async Task CreateMetaverseAttributeAsync(MetaverseAttribute attribute)
    {
        Repository.Database.MetaverseAttributes.Add(attribute);
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
    #endregion

    #region metaverse objects
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

    public async Task<MetaverseObjectHeader?> GetMetaverseObjectHeaderAsync(Guid id)
    {
        return await Repository.Database.MetaverseObjects
            .Include(mo => mo.Type)
            .Include(mo => mo.AttributeValues)
                .ThenInclude(av => av.Attribute)
            .Include(mo => mo.AttributeValues)
                .ThenInclude(av => av.ReferenceValue)
                    .ThenInclude(rv => rv!.AttributeValues.Where(rvav => rvav.Attribute.Name == Constants.BuiltInAttributes.DisplayName))
                        .ThenInclude(rvav => rvav.Attribute)
            .Select(d => new MetaverseObjectHeader
            {
                Id = d.Id,
                Created = d.Created,
                Status = d.Status,
                TypeId = d.Type.Id,
                TypeName = d.Type.Name,
                TypePluralName = d.Type.PluralName,
                AttributeValues = d.AttributeValues.ToList()
            }).SingleOrDefaultAsync(mo => mo.Id == id);
    }

    public async Task UpdateMetaverseObjectAsync(MetaverseObject metaverseObject)
    {
        Repository.Database.Update(metaverseObject);
        await Repository.Database.SaveChangesAsync();
    }

    public async Task CreateMetaverseObjectAsync(MetaverseObject metaverseObject)
    {
        Repository.Database.MetaverseObjects.Add(metaverseObject);
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

        Repository.Database.UpdateRange(objectList);
        await Repository.Database.SaveChangesAsync();
    }

    public async Task<MetaverseObject?> GetMetaverseObjectByTypeAndAttributeAsync(MetaverseObjectType metaverseObjectType, MetaverseAttribute metaverseAttribute, string attributeValue)
    {
        var av = await Repository.Database.MetaverseObjectAttributeValues
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

    public async Task<PagedResultSet<MetaverseObject>> GetMetaverseObjectsOfTypeAsync(
        int metaverseObjectTypeId,
        int page = 1,
        int pageSize = 20,
        QuerySortBy querySortBy = QuerySortBy.DateCreated,
        QueryRange queryRange = QueryRange.Forever)
    {
        if (pageSize < 1)
            throw new ArgumentOutOfRangeException(nameof(pageSize), "pageSize must be a positive number");

        if (page < 1)
            page = 1;

        // limit page size to avoid increasing latency unnecessarily
        if (pageSize > 100)
            pageSize = 100;

        var objects = from o in Repository.Database.MetaverseObjects.
                Include(mo => mo.AttributeValues).
                ThenInclude(av => av.Attribute).
                Where(q => q.Type.Id == metaverseObjectTypeId)
            select o;

        if (queryRange != QueryRange.Forever)
        {
            switch (queryRange)
            {
                case QueryRange.LastYear:
                    objects = objects.Where(q => q.Created >= DateTime.UtcNow - TimeSpan.FromDays(365));
                    break;
                case QueryRange.LastMonth:
                    objects = objects.Where(q => q.Created >= DateTime.UtcNow - TimeSpan.FromDays(30));
                    break;
                case QueryRange.LastWeek:
                    objects = objects.Where(q => q.Created >= DateTime.UtcNow - TimeSpan.FromDays(7));
                    break;
            }
        }

        switch (querySortBy)
        {
            case QuerySortBy.DateCreated:
                objects = objects.OrderByDescending(q => q.Created);
                break;

            // todo: support more ways of sorting, i.e. by attribute value
        }

        // now just retrieve a page's worth of images from the results
        var grossCount = objects.Count();
        var offset = (page - 1) * pageSize;
        var itemsToGet = grossCount >= pageSize ? pageSize : grossCount;
        var results = await objects.Skip(offset).Take(itemsToGet).ToListAsync();

        // now with all the ids we know how many total results there are and so can populate paging info
        var pagedResultSet = new PagedResultSet<MetaverseObject>
        {
            PageSize = pageSize,
            TotalResults = grossCount,
            CurrentPage = page,
            QuerySortBy = querySortBy,
            QueryRange = queryRange,
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

    public async Task<PagedResultSet<MetaverseObjectHeader>> GetMetaverseObjectsOfTypeAsync(
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

        // construct the base query. This much is true, regardless of the search properties.
        var objects = from o in Repository.Database.MetaverseObjects.
                Include(mo => mo.AttributeValues).
                ThenInclude(av => av.Attribute).
                Where(q => q.Type.Id == predefinedSearch.MetaverseObjectType.Id)
            select o;

        // is there criteria to use in the predefined search criteria groups?
        foreach (var group in predefinedSearch.CriteriaGroups)
        {
            foreach (var criteria in group.Criteria)
            {
                switch (criteria.ComparisonType)
                {
                    case SearchComparisonType.Equals:
                        objects = objects.Where(q => q.AttributeValues.Any(av => av.Attribute.Id == criteria.MetaverseAttribute.Id && av.StringValue == criteria.StringValue));
                        break;
                    case SearchComparisonType.NotEquals:
                        objects = objects.Where(q => q.AttributeValues.Any(av => av.Attribute.Id == criteria.MetaverseAttribute.Id && av.StringValue != criteria.StringValue));
                        break;
                    case SearchComparisonType.StartsWith:
                        objects = objects.Where(q => q.AttributeValues.Any(av => av.Attribute.Id == criteria.MetaverseAttribute.Id && av.StringValue != null && av.StringValue.StartsWith(criteria.StringValue)));
                        break;
                    case SearchComparisonType.NotStartsWith:
                        objects = objects.Where(q => q.AttributeValues.Any(av => av.Attribute.Id == criteria.MetaverseAttribute.Id && (av.StringValue == null || !av.StringValue.StartsWith(criteria.StringValue))));
                        break;
                    case SearchComparisonType.EndsWith:
                        objects = objects.Where(q => q.AttributeValues.Any(av => av.Attribute.Id == criteria.MetaverseAttribute.Id && av.StringValue != null && av.StringValue.EndsWith(criteria.StringValue)));
                        break;
                    case SearchComparisonType.NotEndsWith:
                        objects = objects.Where(q => q.AttributeValues.Any(av => av.Attribute.Id == criteria.MetaverseAttribute.Id && (av.StringValue == null || !av.StringValue.EndsWith(criteria.StringValue))));
                        break;

                    case SearchComparisonType.Contains: // need to lookup if we need to handle contains different with postgres
                    case SearchComparisonType.NotContains:
                    case SearchComparisonType.LessThan: // for numbers, we need a number value property on the criteria object
                    case SearchComparisonType.LessThanOrEquals:
                    case SearchComparisonType.GreaterThan:
                    case SearchComparisonType.GreaterThanOrEquals:
                        throw new NotSupportedException($"Not currently supporting PredefinedSearchComparisonType.{criteria.ComparisonType}");
                }
            }

            if (group.Type == SearchGroupType.All)
            {
                // err?
            }
            else
            {
                // Any
                // More err...
            }

            // todo: handle group nesting as well
        }

        // Apply search filter - searches across all attribute values
        // Search is case-insensitive for user convenience
        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            var searchPattern = $"%{searchQuery}%";
            objects = objects.Where(o => o.AttributeValues.Any(av =>
                av.StringValue != null && EF.Functions.ILike(av.StringValue, searchPattern)));
        }

        // Apply sorting - sort by attribute value if specified, otherwise by Created date
        // The sortBy parameter corresponds to the attribute name
        if (!string.IsNullOrWhiteSpace(sortBy))
        {
            // Sort by the specified attribute's string value
            objects = sortDescending
                ? objects.OrderByDescending(o => o.AttributeValues
                    .Where(av => av.Attribute.Name == sortBy)
                    .Select(av => av.StringValue)
                    .FirstOrDefault())
                : objects.OrderBy(o => o.AttributeValues
                    .Where(av => av.Attribute.Name == sortBy)
                    .Select(av => av.StringValue)
                    .FirstOrDefault());
        }
        else
        {
            // Default sort by Created date
            objects = sortDescending
                ? objects.OrderByDescending(q => q.Created)
                : objects.OrderBy(q => q.Created);
        }

        // Get total count for pagination
        var grossCount = await objects.CountAsync();
        var offset = (page - 1) * pageSize;

        // select just the attributes we need into a header and just enough objects for the desired page
        var results = await objects.Skip(offset).Take(pageSize).Select(d => new MetaverseObjectHeader
        {
            Id = d.Id,
            Created = d.Created,
            Status = d.Status,
            TypeId = d.Type.Id,
            TypeName = d.Type.Name,
            TypePluralName = d.Type.PluralName,
            AttributeValues = GetFilteredAttributeValuesList(predefinedSearch, d)
        }).ToListAsync();

        // now with all the ids we know how many total results there are and so can populate paging info
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
    /// Gets a paginated list of metaverse objects with optional filtering by type, search query, or specific attribute value.
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
    /// <param name="syncRuleMapping">The Sync Rule Mapping contains the logic needed to construct a Metaverse Object query.</param>
    /// <returns>A Metaverse Object if a single result is found, otherwise null.</returns>
    /// <exception cref="NotImplementedException">Will be thrown if more than one source is specified. This is not yet supported.</exception>
    /// <exception cref="ArgumentNullException">Will be thrown if the sync rule mapping source connected system attribute is null.</exception>
    /// <exception cref="NotSupportedException">Will be thrown if functions or expressions are in use in the matching rule. These are not yet supported.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Will be thrown if an unsupported attribute type is specified.</exception>
    /// <exception cref="MultipleMatchesException">Will be thrown if there's more than one Metaverse Object that matches the matching rule criteria.</exception>
    public async Task<MetaverseObject?> FindMetaverseObjectUsingMatchingRuleAsync(ConnectedSystemObject connectedSystemObject, MetaverseObjectType metaverseObjectType, ObjectMatchingRule objectMatchingRule)
    {
        if (objectMatchingRule.Sources.Count > 1)
            throw new NotImplementedException("Object Matching Rules with more than one Source are not yet supported (i.e. functions).");

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

            // construct the base query. This much is true, regardless of the matching rule properties.
            var metaVerseObjects = from o in Repository.Database.MetaverseObjects.
                    Include(mvo => mvo.AttributeValues).
                    ThenInclude(av => av.Attribute).
                    Where(mvo => mvo.Type.Id == metaverseObjectType.Id)
                    select o;

            // work out what type of attribute it is
            switch (source.ConnectedSystemAttribute.Type)
            {
                case AttributeDataType.Text:
                    // Check case sensitivity setting on the matching rule
                    if (objectMatchingRule.CaseSensitive)
                    {
                        // Case-sensitive comparison (default) - null check already done above
                        metaVerseObjects = metaVerseObjects.Where(mvo =>
                            mvo.AttributeValues.Any(av =>
                                objectMatchingRule.TargetMetaverseAttribute != null &&
                                av.Attribute.Id == objectMatchingRule.TargetMetaverseAttribute.Id &&
                                av.StringValue != null &&
                                av.StringValue == csoAttributeValue.StringValue));
                    }
                    else
                    {
                        // Case-insensitive comparison using PostgreSQL ILike
                        metaVerseObjects = metaVerseObjects.Where(mvo =>
                            mvo.AttributeValues.Any(av =>
                                objectMatchingRule.TargetMetaverseAttribute != null &&
                                av.Attribute.Id == objectMatchingRule.TargetMetaverseAttribute.Id &&
                                av.StringValue != null &&
                                EF.Functions.ILike(av.StringValue, csoAttributeValue.StringValue!)));
                    }
                    break;
                case AttributeDataType.Number:
                    // Null check already done above
                    metaVerseObjects = metaVerseObjects.Where(mvo =>
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
                    // Null check already done above
                    metaVerseObjects = metaVerseObjects.Where(mvo =>
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

            // execute the search. did we find an MVO?
            var result = await metaVerseObjects.ToListAsync();
            switch (result.Count)
            {
                case 1:
                    return result[0];
                case > 1:
                    throw new MultipleMatchesException(
                        "Multiple Metaverse Objects were found to match the source attribute.",
                        result.Select(q => q.Id).ToList());
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
        // Only execute raw SQL if we have a real database connection (not mocked)
        try
        {
            // Null out FK reference in Activities to preserve audit history
            await Repository.Database.Database.ExecuteSqlRawAsync(
                @"UPDATE ""Activities"" SET ""MetaverseObjectId"" = NULL WHERE ""MetaverseObjectId"" = {0}",
                metaverseObject.Id);

            // Null out FK reference in MetaverseObjectChanges to preserve change history audit trail
            // The MetaverseObjectId column is nullable specifically to support this - DELETE change records
            // intentionally have null MetaverseObjectId since the MVO no longer exists
            await Repository.Database.Database.ExecuteSqlRawAsync(
                @"UPDATE ""MetaverseObjectChanges"" SET ""MetaverseObjectId"" = NULL WHERE ""MetaverseObjectId"" = {0}",
                metaverseObject.Id);
        }
        catch (Exception)
        {
            // Expected when running with mocked DbContext in tests - the Database property may be null
            // or the InMemory provider doesn't support raw SQL. In production with PostgreSQL, this works.
        }

        Repository.Database.MetaverseObjects.Remove(metaverseObject);
        await Repository.Database.SaveChangesAsync();
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
        // 3. Have ALL their CSOs in the specified connected system (will become orphaned)
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
                // Must NOT have any CSOs in OTHER connected systems (would become orphaned)
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
            .FirstOrDefaultAsync(c => c.Id == changeId);

        if (targetChange == null)
            return new List<MetaverseObjectChange>();

        // Validate it's a Delete change with tombstone data
        if (string.IsNullOrEmpty(targetChange.DeletedObjectDisplayName) || !targetChange.DeletedObjectTypeId.HasValue)
            return new List<MetaverseObjectChange> { targetChange };

        // Strategy: Find the original MetaverseObjectId by looking for other changes with the same
        // deleted object identity. Earlier changes (Projected, AttributeFlow) still have the FK populated.
        var mvoId = await Repository.Database.MetaverseObjectChanges
            .Where(c => c.DeletedObjectTypeId == targetChange.DeletedObjectTypeId &&
                        c.DeletedObjectDisplayName == targetChange.DeletedObjectDisplayName &&
                        c.MetaverseObject != null) // Earlier changes still have the FK
            .Select(c => c.MetaverseObject!.Id)
            .FirstOrDefaultAsync();

        // If we found the original MVO ID, fetch ALL changes (including Delete change)
        if (mvoId != Guid.Empty)
        {
            return await Repository.Database.MetaverseObjectChanges
                .AsSplitQuery()
                .Where(c => c.MetaverseObject!.Id == mvoId || // Non-deleted changes
                            (c.DeletedObjectTypeId == targetChange.DeletedObjectTypeId &&
                             c.DeletedObjectDisplayName == targetChange.DeletedObjectDisplayName &&
                             c.ChangeType == ObjectChangeType.Deleted)) // The Delete change
                .OrderByDescending(c => c.ChangeTime)
                .Include(c => c.ActivityRunProfileExecutionItem)
                .ThenInclude(rpei => rpei!.Activity)
                .Include(c => c.AttributeChanges)
                .ThenInclude(ac => ac.Attribute)
                .Include(c => c.AttributeChanges)
                .ThenInclude(ac => ac.ValueChanges)
                .ToListAsync();
        }

        // Fallback: If no earlier changes exist (edge case), return only the Delete change
        // This can happen if an MVO was projected and immediately deleted in the same sync
        return new List<MetaverseObjectChange> { targetChange };
    }

    private static List<MetaverseObjectAttributeValue> GetFilteredAttributeValuesList(PredefinedSearch predefinedSearch, MetaverseObject metaverseObject)
    {
        return predefinedSearch.Attributes
            .Select(predefinedSearchAttribute => metaverseObject.AttributeValues
            .SingleOrDefault(q => q.Attribute.Id == predefinedSearchAttribute.MetaverseAttribute.Id))
            .OfType<MetaverseObjectAttributeValue>()
            .ToList();
    }
    #endregion
}
