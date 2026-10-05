// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Transactional;
using Microsoft.EntityFrameworkCore;

namespace JIM.PostgresData.Repositories;

public partial class SyncRepository
{
    #region Retired Values Register (#242, Phase 6)

    /// <inheritdoc />
    public async Task<HashSet<string>> GetRetiredGeneratedValuesInUseAsync(int? metaverseAttributeId, int? connectedSystemObjectTypeAttributeId, IReadOnlyCollection<string> normalisedValues)
    {
        ValidateExactlyOneAttributeReference(metaverseAttributeId, connectedSystemObjectTypeAttributeId);

        if (normalisedValues.Count == 0)
            return [];

        // LOWER("NormalisedValue") on the left matches the register's unique expression index for the attribute
        // column exactly (IX_RetiredGeneratedValues_MvAttributeId_LowerNormalisedValue_Unique and its Connected
        // System sibling); the values arrive already lower-cased, so the array is not lowered again.
        var attributeColumn = metaverseAttributeId.HasValue ? "MetaverseAttributeId" : "ConnectedSystemObjectTypeAttributeId";
        var attributeId = metaverseAttributeId ?? connectedSystemObjectTypeAttributeId!.Value;
        var sql = $@"SELECT LOWER(""NormalisedValue"") AS ""Value""
                    FROM ""RetiredGeneratedValues""
                    WHERE ""{attributeColumn}"" = {{0}} AND LOWER(""NormalisedValue"") = ANY({{1}})";

        var rows = await _context.Database.SqlQueryRaw<string>(sql, attributeId, normalisedValues.ToArray()).ToListAsync();
        return rows.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GeneratedValueRetirement>> RetireAndDeleteGeneratedValueAssignmentsAsync(IReadOnlyCollection<Guid> assignmentIds, RetiredGeneratedValueReason reason, Guid? activityId)
    {
        if (assignmentIds.Count == 0)
            return [];

        // The worker's per-run context tracks by default: detach any tracked copy of an assignment this statement
        // deletes, or the next SaveChangesAsync would act on a row that no longer exists (src/CLAUDE.md, "Raw SQL
        // Writes Must Fix Up or Detach Tracked Instances").
        var idSet = assignmentIds.ToHashSet();
        DetachTrackedEntities<GeneratedValueAssignment>(a => idSet.Contains(a.Id));

        return await RetiredGeneratedValueSql.RetireAndDeleteAssignmentsAsync(_context, assignmentIds, reason, activityId);
    }

    /// <inheritdoc />
    public async Task<List<RetiredGeneratedValueCount>> GetRetiredGeneratedValueCountsAsync(IReadOnlyCollection<int> metaverseAttributeIds, IReadOnlyCollection<int> connectedSystemObjectTypeAttributeIds)
    {
        var counts = new List<RetiredGeneratedValueCount>();

        if (metaverseAttributeIds.Count > 0)
        {
            var ids = metaverseAttributeIds.ToList();
            counts.AddRange(await _context.RetiredGeneratedValues
                .AsNoTracking()
                .Where(r => r.MetaverseAttributeId != null && ids.Contains(r.MetaverseAttributeId.Value))
                .GroupBy(r => r.MetaverseAttributeId)
                .Select(g => new RetiredGeneratedValueCount { MetaverseAttributeId = g.Key, Count = g.Count() })
                .ToListAsync());
        }

        if (connectedSystemObjectTypeAttributeIds.Count > 0)
        {
            var ids = connectedSystemObjectTypeAttributeIds.ToList();
            counts.AddRange(await _context.RetiredGeneratedValues
                .AsNoTracking()
                .Where(r => r.ConnectedSystemObjectTypeAttributeId != null && ids.Contains(r.ConnectedSystemObjectTypeAttributeId.Value))
                .GroupBy(r => r.ConnectedSystemObjectTypeAttributeId)
                .Select(g => new RetiredGeneratedValueCount { ConnectedSystemObjectTypeAttributeId = g.Key, Count = g.Count() })
                .ToListAsync());
        }

        return counts;
    }

    /// <inheritdoc />
    public async Task<(List<RetiredGeneratedValueHeader> Items, int? TotalCount)> GetRetiredGeneratedValueHeadersRangeAsync(
        int? metaverseAttributeId, int? connectedSystemObjectTypeAttributeId, string? search, int offset, int count, bool includeTotalCount)
    {
        ValidateExactlyOneAttributeReference(metaverseAttributeId, connectedSystemObjectTypeAttributeId);

        var query = _context.RetiredGeneratedValues.AsNoTracking()
            .Where(r => r.MetaverseAttributeId == metaverseAttributeId && r.ConnectedSystemObjectTypeAttributeId == connectedSystemObjectTypeAttributeId);

        if (!string.IsNullOrWhiteSpace(search))
        {
            // The value side compares the lower-cased form both ways, so it is case-insensitive like the register
            // itself; the holder's name is free text, so ILike.
            var term = search.Trim();
            var lowered = term.ToLowerInvariant();
            var pattern = $"%{EscapeLikePattern(term)}%";
            query = query.Where(r => r.NormalisedValue.Contains(lowered) || (r.FromObjectDisplayName != null && EF.Functions.ILike(r.FromObjectDisplayName, pattern)));
        }

        int? totalCount = includeTotalCount ? await query.CountAsync() : null;

        var window = query
            .OrderByDescending(r => r.RetiredAt)
            .ThenByDescending(r => r.Id)
            .Skip(offset)
            .Take(count);

        var items = await ProjectHeaders(window).ToListAsync();
        return (items, totalCount);
    }

    /// <inheritdoc />
    public async Task<List<RetiredGeneratedValueHeader>> GetRetiredGeneratedValueHeadersForObjectAsync(Guid fromObjectId)
    {
        var query = _context.RetiredGeneratedValues.AsNoTracking()
            .Where(r => r.FromObjectId == fromObjectId)
            .OrderByDescending(r => r.RetiredAt)
            .ThenByDescending(r => r.Id);

        return await ProjectHeaders(query).ToListAsync();
    }

    /// <inheritdoc />
    public async Task<int> DeleteRetiredGeneratedValuesForAttributeAsync(int? metaverseAttributeId, int? connectedSystemObjectTypeAttributeId)
    {
        ValidateExactlyOneAttributeReference(metaverseAttributeId, connectedSystemObjectTypeAttributeId);

        var attributeColumn = metaverseAttributeId.HasValue ? "MetaverseAttributeId" : "ConnectedSystemObjectTypeAttributeId";
        var attributeId = metaverseAttributeId ?? connectedSystemObjectTypeAttributeId!.Value;

        // Built into a local first rather than passed inline: EF1002 flags an interpolated string literal handed
        // directly to ExecuteSqlRawAsync, even though the column name here is fixed, non-user-controlled text.
        var sql = $@"DELETE FROM ""RetiredGeneratedValues"" WHERE ""{attributeColumn}"" = {{0}}";
        return await _context.Database.ExecuteSqlRawAsync(sql, attributeId);
    }

    /// <summary>
    /// The header projection shared by the list and per-object reads: the register entry, plus what is known now
    /// about the object that held the value (it may have been deleted) and the Activity that retired it (it may have
    /// been pruned). Both are left joins on plain columns, because the register deliberately references neither.
    /// </summary>
    private IQueryable<RetiredGeneratedValueHeader> ProjectHeaders(IQueryable<RetiredGeneratedValue> source)
    {
        return from r in source
               join mvo in _context.MetaverseObjects on r.FromObjectId equals (Guid?)mvo.Id into mvos
               from mvo in mvos.DefaultIfEmpty()
               join cso in _context.ConnectedSystemObjects on r.FromObjectId equals (Guid?)cso.Id into csos
               from cso in csos.DefaultIfEmpty()
               join activity in _context.Activities on r.ActivityId equals (Guid?)activity.Id into activities
               from activity in activities.DefaultIfEmpty()
               select new RetiredGeneratedValueHeader
               {
                   Id = r.Id,
                   MetaverseAttributeId = r.MetaverseAttributeId,
                   ConnectedSystemObjectTypeAttributeId = r.ConnectedSystemObjectTypeAttributeId,
                   AttributeName = r.MetaverseAttributeId != null ? r.MetaverseAttribute!.Name : r.ConnectedSystemObjectTypeAttribute!.Name,
                   Value = r.Value,
                   RetiredAt = r.RetiredAt,
                   Reason = r.Reason,
                   FromObjectId = r.FromObjectId,
                   FromObjectDisplayName = r.FromObjectDisplayName,
                   // An import value's holder is a Metaverse Object, an export value's a Connected System Object; the
                   // attribute column says which, so a stray id match on the other table can never be read.
                   FromObjectExists = r.MetaverseAttributeId != null ? mvo != null : cso != null,
                   FromObjectTypeName = r.MetaverseAttributeId != null
                       ? (mvo != null ? mvo.Type.Name : null)
                       : (cso != null ? cso.Type.Name : null),
                   FromObjectTypePluralName = r.MetaverseAttributeId != null && mvo != null ? mvo.Type.PluralName : null,
                   FromObjectConnectedSystemId = r.MetaverseAttributeId == null && cso != null ? cso.ConnectedSystemId : null,
                   ActivityId = r.ActivityId,
                   ActivityTargetName = activity != null ? activity.TargetName : null,
                   ActivityTargetContext = activity != null ? activity.TargetContext : null
               };
    }

    /// <summary>
    /// Escapes the LIKE wildcards in an administrator's search text, so "%" or "_" match themselves.
    /// </summary>
    private static string EscapeLikePattern(string term) =>
        term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    #endregion
}
