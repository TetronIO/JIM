// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Linq.Expressions;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Enums;
using JIM.Models.History;
using JIM.Models.Utility;
using Microsoft.EntityFrameworkCore;

namespace JIM.PostgresData.Repositories;

/// <summary>
/// The Metaverse Object change search (#1901): changes across every Metaverse Object, filtered by time, object type,
/// display name and how named attributes changed.
/// </summary>
public partial class MetaverseRepository
{
    /// <inheritdoc />
    public async Task<RangeResultSet<MetaverseObjectChangeSearchResult>> SearchMetaverseObjectChangesRangeAsync(
        MetaverseObjectChangeSearchQuery query,
        int offset,
        int count,
        bool includeTotalCount = true)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (count < 1)
            throw new ArgumentOutOfRangeException(nameof(count), "count must be a positive number");

        if (offset < 0)
            offset = 0;

        // The same window cap as the other header range reads in this repository.
        if (count > MaxHeaderWindowSize)
            count = MaxHeaderWindowSize;

        var matches = BuildChangeSearch(query);

        // Counting scans every match rather than a window of them, so it is skipped when the caller holds the total.
        int? totalCount = includeTotalCount ? await matches.CountAsync() : null;

        // The window is chosen by id first, so the heavier projections below run for one window only. Id breaks ties
        // between changes made in the same instant, which a synchronisation page produces routinely.
        var windowIds = await matches
            .OrderByDescending(c => c.ChangeTime)
            .ThenByDescending(c => c.Id)
            .Skip(offset)
            .Take(count)
            .Select(c => c.Id)
            .ToListAsync();

        if (windowIds.Count == 0)
            return new RangeResultSet<MetaverseObjectChangeSearchResult> { TotalResults = totalCount };

        var changes = await Repository.Database.MetaverseObjectChanges
            .AsNoTracking()
            .Where(c => windowIds.Contains(c.Id))
            .Select(MvoChangeHistoryProjection)
            .ToDictionaryAsync(c => c.Id);
        await ResolveChangeValueContributingSystemsAsync(changes.Values.ToList());

        var identities = await LoadChangeSearchIdentitiesAsync(windowIds);
        var matchedAttributeIds = query.Conditions.Select(c => c.AttributeId).Distinct().ToList();

        var results = windowIds
            .Where(changes.ContainsKey)
            .Select(id => BuildChangeSearchResult(changes[id], identities[id], matchedAttributeIds))
            .ToList();

        return new RangeResultSet<MetaverseObjectChangeSearchResult> { Results = results, TotalResults = totalCount };
    }

    /// <summary>
    /// The filtered set of changes, before ordering and windowing. Changes to an object since deleted carry no live
    /// object, so their type and name come from the object's deletion record, matched on the preserved id.
    /// </summary>
    private IQueryable<MetaverseObjectChange> BuildChangeSearch(MetaverseObjectChangeSearchQuery query)
    {
        var changes = Repository.Database.MetaverseObjectChanges.AsNoTracking();
        var deletions = Repository.Database.MetaverseObjectChanges
            .Where(d => d.ChangeType == ObjectChangeType.Deleted && d.DeletedMetaverseObjectId != null);

        if (query.FromDate.HasValue)
        {
            var fromDate = query.FromDate.Value;
            changes = changes.Where(c => c.ChangeTime >= fromDate);
        }

        if (query.ToDate.HasValue)
        {
            var toDate = query.ToDate.Value;
            changes = changes.Where(c => c.ChangeTime <= toDate);
        }

        if (query.ObjectTypeId.HasValue)
        {
            var typeId = query.ObjectTypeId.Value;
            changes = changes.Where(c =>
                (c.MetaverseObject != null && c.MetaverseObject.Type.Id == typeId) ||
                (c.MetaverseObject == null && deletions.Any(d =>
                    d.DeletedMetaverseObjectId == c.DeletedMetaverseObjectId && d.DeletedObjectTypeId == typeId)));
        }

        if (!string.IsNullOrWhiteSpace(query.DisplayNameSearch))
        {
            var pattern = LikePattern.Contains(query.DisplayNameSearch.Trim());
            changes = changes.Where(c =>
                (c.MetaverseObject != null && c.MetaverseObject.CachedDisplayName != null &&
                 EF.Functions.ILike(c.MetaverseObject.CachedDisplayName, pattern)) ||
                (c.MetaverseObject == null && deletions.Any(d =>
                    d.DeletedMetaverseObjectId == c.DeletedMetaverseObjectId &&
                    d.DeletedObjectDisplayName != null &&
                    EF.Functions.ILike(d.DeletedObjectDisplayName, pattern))));
        }

        if (query.Conditions.Count > 0)
        {
            var conditionPredicates = query.Conditions.Select(ConditionPredicate).ToList();
            var combined = conditionPredicates.Skip(1).Aggregate(conditionPredicates[0], (all, next) =>
                query.MatchMode == AttributeConditionMatchMode.All
                    ? PredicateComposition.And(all, next)
                    : PredicateComposition.Or(all, next));
            changes = changes.Where(combined);
        }

        return changes;
    }

    /// <summary>
    /// A change meets a condition when it changed the named attribute, with a value change of the right kind (added,
    /// removed, or either) matching the condition's value when it has one. Under <see cref="AttributeConditionMatchMode.All"/>
    /// the conditions are joined on the one change, so all of them were met by one object in one Activity.
    /// </summary>
    private static Expression<Func<MetaverseObjectChange, bool>> ConditionPredicate(ResolvedAttributeChangeCondition condition)
    {
        var attributeId = condition.AttributeId;
        var valuePredicate = ValuePredicate(condition);
        return c => c.AttributeChanges.Any(ac =>
            ac.Attribute != null && ac.Attribute.Id == attributeId &&
            ac.ValueChanges.AsQueryable().Any(valuePredicate));
    }

    private static Expression<Func<MetaverseObjectChangeAttributeValue, bool>> ValuePredicate(ResolvedAttributeChangeCondition condition)
    {
        Expression<Func<MetaverseObjectChangeAttributeValue, bool>> predicate = condition.Operator switch
        {
            AttributeChangeOperator.ChangedTo => v => v.ValueChangeType == ValueChangeType.Add,
            AttributeChangeOperator.ChangedFrom => v => v.ValueChangeType == ValueChangeType.Remove,
            _ => v => v.ValueChangeType == ValueChangeType.Add || v.ValueChangeType == ValueChangeType.Remove
        };

        var match = condition.Match;
        if (match == null)
            return predicate;

        return PredicateComposition.And(predicate, ValueMatchPredicate(condition.AttributeType, match));
    }

    private static Expression<Func<MetaverseObjectChangeAttributeValue, bool>> ValueMatchPredicate(AttributeDataType attributeType, AttributeChangeValueMatch match)
    {
        switch (attributeType)
        {
            case AttributeDataType.Text when match.Text != null:
                var textPattern = LikePattern.Wildcard(match.Text);
                return v => v.StringValue != null && EF.Functions.ILike(v.StringValue, textPattern);

            case AttributeDataType.Reference when match.Text != null:
                var referencePattern = LikePattern.Wildcard(match.Text);
                return v => v.ReferenceValue != null && v.ReferenceValue.CachedDisplayName != null &&
                            EF.Functions.ILike(v.ReferenceValue.CachedDisplayName, referencePattern);

            case AttributeDataType.Number when match.IntValue.HasValue:
                var intValue = match.IntValue.Value;
                return v => v.IntValue == intValue;

            case AttributeDataType.LongNumber when match.LongValue.HasValue:
                var longValue = match.LongValue.Value;
                return v => v.LongValue == longValue;

            case AttributeDataType.Decimal when match.DecimalValue.HasValue:
                var decimalValue = match.DecimalValue.Value;
                return v => v.DecimalValue == decimalValue;

            case AttributeDataType.Boolean when match.BoolValue.HasValue:
                var boolValue = match.BoolValue.Value;
                return v => v.BoolValue == boolValue;

            case AttributeDataType.Guid when match.GuidValue.HasValue:
                var guidValue = match.GuidValue.Value;
                return v => v.GuidValue == guidValue;

            case AttributeDataType.DateTime when match.DateTimeFrom.HasValue && match.DateTimeTo.HasValue:
                var from = match.DateTimeFrom.Value;
                var to = match.DateTimeTo.Value;
                return v => v.DateTimeValue >= from && v.DateTimeValue < to;

            default:
                // The server parses each value for its attribute's type before querying, so a mismatch here is a
                // caller bug; matching nothing silently would read as "no such changes".
                throw new ArgumentException($"The condition's value does not match its attribute's data type ({attributeType}).");
        }
    }

    private sealed record ChangeSearchIdentity(Guid? MetaverseObjectId, bool Deleted, string? DisplayName, int? TypeId, string? TypeName, Guid? ActivityId);

    /// <summary>
    /// Names the object and Activity behind each change in the window: the live object where it still exists, else
    /// the identity its deletion record preserved.
    /// </summary>
    private async Task<Dictionary<Guid, ChangeSearchIdentity>> LoadChangeSearchIdentitiesAsync(List<Guid> changeIds)
    {
        var rows = await Repository.Database.MetaverseObjectChanges
            .AsNoTracking()
            .Where(c => changeIds.Contains(c.Id))
            .Select(c => new
            {
                c.Id,
                LiveId = c.MetaverseObject != null ? c.MetaverseObject.Id : (Guid?)null,
                LiveName = c.MetaverseObject != null ? c.MetaverseObject.CachedDisplayName : null,
                LiveTypeId = c.MetaverseObject != null ? c.MetaverseObject.Type.Id : (int?)null,
                LiveTypeName = c.MetaverseObject != null ? c.MetaverseObject.Type.Name : null,
                c.DeletedMetaverseObjectId,
                ActivityId = c.ActivityRunProfileExecutionItem != null ? c.ActivityRunProfileExecutionItem.ActivityId : (Guid?)null
            })
            .ToListAsync();

        var deletedIds = rows
            .Where(r => r.LiveId == null && r.DeletedMetaverseObjectId.HasValue)
            .Select(r => r.DeletedMetaverseObjectId!.Value)
            .Distinct()
            .ToList();

        var deletionRecords = deletedIds.Count == 0
            ? []
            : await Repository.Database.MetaverseObjectChanges
                .AsNoTracking()
                .Where(d => d.ChangeType == ObjectChangeType.Deleted && d.DeletedMetaverseObjectId != null &&
                            deletedIds.Contains(d.DeletedMetaverseObjectId.Value))
                .Select(d => new
                {
                    ObjectId = d.DeletedMetaverseObjectId!.Value,
                    d.ChangeTime,
                    d.DeletedObjectDisplayName,
                    d.DeletedObjectTypeId,
                    TypeName = d.DeletedObjectType != null ? d.DeletedObjectType.Name : null
                })
                .ToListAsync();

        // An object has one deletion record in practice; the latest wins if that ever stops holding.
        var deletionByObject = deletionRecords
            .GroupBy(d => d.ObjectId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(d => d.ChangeTime).First());

        return rows.ToDictionary(r => r.Id, r =>
        {
            if (r.LiveId.HasValue)
                return new ChangeSearchIdentity(r.LiveId, false, r.LiveName, r.LiveTypeId, r.LiveTypeName, r.ActivityId);

            var deletion = r.DeletedMetaverseObjectId.HasValue && deletionByObject.TryGetValue(r.DeletedMetaverseObjectId.Value, out var found)
                ? found
                : null;
            return new ChangeSearchIdentity(r.DeletedMetaverseObjectId, true, deletion?.DeletedObjectDisplayName,
                deletion?.DeletedObjectTypeId, deletion?.TypeName, r.ActivityId);
        });
    }

    private static MetaverseObjectChangeSearchResult BuildChangeSearchResult(
        MvoChangeHistoryDto change,
        ChangeSearchIdentity identity,
        List<int> matchedAttributeIds)
    {
        return new MetaverseObjectChangeSearchResult
        {
            Change = change,
            MetaverseObjectId = identity.MetaverseObjectId,
            MetaverseObjectDeleted = identity.Deleted,
            MetaverseObjectDisplayName = identity.DisplayName,
            MetaverseObjectTypeId = identity.TypeId,
            MetaverseObjectTypeName = identity.TypeName,
            ActivityId = identity.ActivityId,
            MatchedAttributeIds = change.AttributeChanges
                .Where(ac => ac.AttributeId.HasValue && matchedAttributeIds.Contains(ac.AttributeId.Value))
                .Select(ac => ac.AttributeId!.Value)
                .Distinct()
                .ToList()
        };
    }
}
