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
    /// object, so their type and name come from the object's deletion record, matched on the object's id.
    /// </summary>
    /// <remarks>
    /// Whether a change's object is live is asked of the object table, never of the change's id, which stays set once
    /// deletions stop clearing it (#2669). The object's id is the change's subject id, or the deleted-object id a
    /// deletion re-keys it to until that column is retired; a deletion record names it by either.
    /// </remarks>
    private IQueryable<MetaverseObjectChange> BuildChangeSearch(MetaverseObjectChangeSearchQuery query)
    {
        var changes = Repository.Database.MetaverseObjectChanges.AsNoTracking();
        var objects = Repository.Database.MetaverseObjects;
        var deletions = Repository.Database.MetaverseObjectChanges.Where(d => d.ChangeType == ObjectChangeType.Deleted);

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
                objects.Any(m => m.Id == c.MetaverseObjectId && m.Type.Id == typeId) ||
                (!objects.Any(m => m.Id == c.MetaverseObjectId) && deletions.Any(d =>
                    (d.MetaverseObjectId == (c.MetaverseObjectId ?? c.DeletedMetaverseObjectId) ||
                     d.DeletedMetaverseObjectId == (c.MetaverseObjectId ?? c.DeletedMetaverseObjectId)) &&
                    d.DeletedObjectTypeId == typeId)));
        }

        if (!string.IsNullOrWhiteSpace(query.DisplayNameSearch))
        {
            var pattern = LikePattern.Contains(query.DisplayNameSearch.Trim());
            changes = changes.Where(c =>
                objects.Any(m => m.Id == c.MetaverseObjectId && m.CachedDisplayName != null &&
                                 EF.Functions.ILike(m.CachedDisplayName, pattern)) ||
                (!objects.Any(m => m.Id == c.MetaverseObjectId) && deletions.Any(d =>
                    (d.MetaverseObjectId == (c.MetaverseObjectId ?? c.DeletedMetaverseObjectId) ||
                     d.DeletedMetaverseObjectId == (c.MetaverseObjectId ?? c.DeletedMetaverseObjectId)) &&
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
    private Expression<Func<MetaverseObjectChange, bool>> ConditionPredicate(ResolvedAttributeChangeCondition condition)
    {
        var attributeId = condition.AttributeId;
        var valuePredicate = ValuePredicate(condition);
        return c => c.AttributeChanges.Any(ac =>
            ac.AttributeId == attributeId &&
            ac.ValueChanges.AsQueryable().Any(valuePredicate));
    }

    private Expression<Func<MetaverseObjectChangeAttributeValue, bool>> ValuePredicate(ResolvedAttributeChangeCondition condition)
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

    private Expression<Func<MetaverseObjectChangeAttributeValue, bool>> ValueMatchPredicate(AttributeDataType attributeType, AttributeChangeValueMatch match)
    {
        switch (attributeType)
        {
            case AttributeDataType.Text when match.Text != null:
                var textPattern = LikePattern.Wildcard(match.Text);
                return v => v.StringValue != null && EF.Functions.ILike(v.StringValue, textPattern);

            case AttributeDataType.Reference when match.Text != null:
                // The referenced object is found by the id the value holds, which is kept after the object goes (#2669).
                var referencePattern = LikePattern.Wildcard(match.Text);
                var objects = Repository.Database.MetaverseObjects;
                return v => objects.Any(m => m.Id == v.ReferenceValueId && m.CachedDisplayName != null &&
                                             EF.Functions.ILike(m.CachedDisplayName, referencePattern));

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

    private sealed record ChangeSearchIdentity(Guid? MetaverseObjectId, bool Deleted, DateTime? DeletedAt, string? DisplayName, int? TypeId, string? TypeName, string? TypePluralName);

    /// <summary>
    /// Names the object behind each change in the window: the live object where it still exists, else the identity its
    /// deletion record preserved, with when it was deleted. Resolved by the one batch read every history reader uses
    /// (#2669), on the change's subject id or the deleted-object id a deletion re-keys it to.
    /// </summary>
    private async Task<Dictionary<Guid, ChangeSearchIdentity>> LoadChangeSearchIdentitiesAsync(List<Guid> changeIds)
    {
        var rows = await Repository.Database.MetaverseObjectChanges
            .AsNoTracking()
            .Where(c => changeIds.Contains(c.Id))
            .Select(c => new { c.Id, ObjectId = c.MetaverseObjectId ?? c.DeletedMetaverseObjectId })
            .ToListAsync();

        var references = await GetMetaverseObjectReferenceSummariesAsync(
            rows.Where(r => r.ObjectId.HasValue).Select(r => r.ObjectId!.Value).Distinct().ToList());

        return rows.ToDictionary(r => r.Id, r =>
        {
            var reference = r.ObjectId is { } objectId ? references.GetValueOrDefault(objectId) : null;
            if (reference is { IsLive: true })
                return new ChangeSearchIdentity(reference.Id, false, null, reference.DisplayName, reference.ObjectTypeId,
                    reference.ObjectTypeName, reference.ObjectTypePluralName);

            // Not live: named from its deletion record where one is held, and still reported as deleted where none is.
            return new ChangeSearchIdentity(r.ObjectId, true, reference?.DeletedAt, reference?.DisplayName, reference?.ObjectTypeId,
                reference?.ObjectTypeName, reference?.ObjectTypePluralName);
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
            MetaverseObjectDeletedAt = identity.DeletedAt,
            MetaverseObjectDisplayName = identity.DisplayName,
            MetaverseObjectTypeId = identity.TypeId,
            MetaverseObjectTypeName = identity.TypeName,
            MetaverseObjectTypePluralName = identity.TypePluralName,
            MatchedAttributeIds = change.AttributeChanges
                .Where(ac => ac.AttributeId.HasValue && matchedAttributeIds.Contains(ac.AttributeId.Value))
                .Select(ac => ac.AttributeId!.Value)
                .Distinct()
                .ToList()
        };
    }
}
