﻿using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Enums;
using JIM.Models.Exceptions;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Models.Utility;
using Microsoft.EntityFrameworkCore;
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
            Created = t.Created,
            AttributesCount = t.Attributes.Count,
            BuiltIn = t.BuiltIn,
            HasPredefinedSearches = t.PredefinedSearches.Count > 0
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

    public async Task<MetaverseAttribute?> GetMetaverseAttributeAsync(string name)
    {
        return await Repository.Database.MetaverseAttributes.SingleOrDefaultAsync(x => x.Name == name);
    }
    #endregion

    #region metaverse objects
    public async Task<MetaverseObject?> GetMetaverseObjectAsync(Guid id)
    {
        return await Repository.Database.MetaverseObjects.
            Include(mo => mo.Type).
            Include(mo => mo.AttributeValues).
            ThenInclude(av => av.Attribute).
            Include(mo => mo.AttributeValues).
            ThenInclude(av => av.ReferenceValue).
            ThenInclude(rv => rv!.AttributeValues.Where(rvav => rvav.Attribute.Name == Constants.BuiltInAttributes.DisplayName)).
            Include(mo => mo.AttributeValues).
            ThenInclude(av => av.ReferenceValue).
            ThenInclude(rv => rv!.Type).
            SingleOrDefaultAsync(mo => mo.Id == id);
    }

    public async Task<MetaverseObjectHeader?> GetMetaverseObjectHeaderAsync(Guid id)
    {
        return await Repository.Database.MetaverseObjects
            .Include(mo => mo.Type)
            .Include(mo => mo.AttributeValues)
            .ThenInclude(av => av.ReferenceValue)
            .ThenInclude(rv => rv!.AttributeValues.Where(rvav => rvav.Attribute.Name == Constants.BuiltInAttributes.DisplayName))
            .Select(d => new MetaverseObjectHeader
            {
                Id = d.Id,
                Created = d.Created,
                Status = d.Status,
                TypeId = d.Type.Id,
                TypeName = d.Type.Name
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

        // select just the attributes we need into a header and just enough objects for the desired page
        var results = await objects.Skip(offset).Take(itemsToGet).Select(d => new MetaverseObjectHeader
        {
            Id = d.Id,
            Created = d.Created,
            Status = d.Status,
            TypeId = d.Type.Id,
            TypeName = d.Type.Name,
            AttributeValues = GetFilteredAttributeValuesList(predefinedSearch, d)
        }).ToListAsync();

        // now with all the ids we know how many total results there are and so can populate paging info
        var pagedResultSet = new PagedResultSet<MetaverseObjectHeader>
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
    /// <exception cref="NotSupportedException">Will be thrown if functions or expressions are in use in the sync rule mapping. These are not yet supported.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Will be thrown if an unsupported attribute type is specified.</exception>
    /// <exception cref="MultipleMatchesException">Will be thrown if there's more than one Metaverse Object that matches the sync rule mapping criteria.</exception>
    public async Task<MetaverseObject?> FindMetaverseObjectUsingMatchingRuleAsync(ConnectedSystemObject connectedSystemObject, MetaverseObjectType metaverseObjectType, SyncRuleMapping syncRuleMapping)
    {
        if (syncRuleMapping.Sources.Count > 1)
            throw new NotImplementedException("Sync Rule Mappings with more than one Source at not yet supported (i.e. functions).");

        // at this point in development, we expect and can process a single source.
        var source = syncRuleMapping.Sources[0];
        if (source.ConnectedSystemAttribute == null)
            throw new InvalidDataException("syncRuleMapping.Sources[0].ConnectedSystemAttribute");
        
        // get the source attribute value(s)
        var csoAttributeValues = connectedSystemObject.AttributeValues.Where(q => q.AttributeId == source.ConnectedSystemAttribute.Id);
        
        // try and find a match for any of the source attribute values.
        // this enables an MVA such as 'CN' to be used as a matching attribute.
        foreach (var csoAttributeValue in csoAttributeValues)
        {
            // construct the base query. This much is true, regardless of the sync rule mapping properties.
            var metaVerseObjects = from o in Repository.Database.MetaverseObjects.
                    Include(mvo => mvo.AttributeValues).
                    ThenInclude(av => av.Attribute).
                    Where(mvo => mvo.Type.Id == metaverseObjectType.Id)
                    select o;
            
            // work out what type of attribute it is
            switch (source.ConnectedSystemAttribute.Type)
            {
                case AttributeDataType.Text:
                    metaVerseObjects = metaVerseObjects.Where(mvo => 
                        mvo.AttributeValues.Any(av => 
                            syncRuleMapping.TargetMetaverseAttribute != null && 
                            av.Attribute.Id == syncRuleMapping.TargetMetaverseAttribute.Id && 
                            av.StringValue == csoAttributeValue.StringValue));
                    break;
                case AttributeDataType.Number:
                    metaVerseObjects = metaVerseObjects.Where(mvo => 
                        mvo.AttributeValues.Any(av => 
                            syncRuleMapping.TargetMetaverseAttribute != null && 
                            av.Attribute.Id == syncRuleMapping.TargetMetaverseAttribute.Id && 
                            av.IntValue == csoAttributeValue.IntValue));
                    break;
                case AttributeDataType.DateTime:
                    throw new NotSupportedException("DateTime attributes are not supported in Object Matching Rules.");
                case AttributeDataType.Binary:
                    throw new NotSupportedException("Binary attributes are not supported in Object Matching Rules.");
                case AttributeDataType.Reference:
                    throw new NotSupportedException("Reference attributes are not supported in Object Matching Rules.");
                case AttributeDataType.Guid:
                    metaVerseObjects = metaVerseObjects.Where(mvo => 
                        mvo.AttributeValues.Any(av => 
                            syncRuleMapping.TargetMetaverseAttribute != null && 
                            av.Attribute.Id == syncRuleMapping.TargetMetaverseAttribute.Id && 
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
