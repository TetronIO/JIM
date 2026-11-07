// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace JIM.PostgresData.Repositories;

/// <summary>
/// Loading objects' attribute values without some attributes' values, by index range rather than by filter: the one load
/// behind both partial loads of a large group's members, export execution's (#2816) and the Delta Import's (#2817).
/// </summary>
/// <remarks>
/// Leaving an attribute out with a predicate (<c>AttributeId &lt;&gt; ALL(...)</c>, which an EF filtered include
/// generates) cannot bound an index scan, so PostgreSQL reads every value row of the object and filters: about 290 ms for a
/// 200,000-member group, every one of them a member it then throws away. Covering every other attribute id with ranges
/// makes each one an index range scan on (object, attribute), which never visits a member row: 0.15 ms for the same group.
/// </remarks>
public partial class ConnectedSystemRepository
{
    /// <summary>
    /// Each object's values whose attribute id falls in one of its ranges (every id but the attributes it leaves out),
    /// each range an index range scan on (object, attribute), and the left-out attributes' values wanted all the same, by
    /// id. A value both arms return is kept once by the caller.
    /// </summary>
    internal const string AttributeValuesByRangesSql =
        @"SELECT v.*
          FROM unnest(@rangeObjectIds, @rangeLows, @rangeHighs) AS r(object_id, low, high)
          JOIN ""ConnectedSystemObjectAttributeValues"" AS v
            ON v.""ConnectedSystemObjectId"" = r.object_id
           AND v.""AttributeId"" BETWEEN r.low AND r.high
          UNION ALL
          SELECT v.*
          FROM ""ConnectedSystemObjectAttributeValues"" AS v
          WHERE v.""Id"" = ANY(@includedValueIds)";

    /// <summary>
    /// The parameters of <see cref="AttributeValuesByRangesSql"/>: each object with the attributes it leaves out, and the
    /// values of those attributes to load all the same.
    /// </summary>
    internal static NpgsqlParameter[] AttributeValuesByRangesParameters(
        IEnumerable<(Guid ObjectId, IReadOnlyCollection<int> ExcludedAttributeIds)> objects, IReadOnlyCollection<Guid> includedValueIds)
    {
        var ranges = objects
            .SelectMany(o => AttributeIdRangesExcluding(o.ExcludedAttributeIds.Distinct().Order().ToList())
                .Select(range => (o.ObjectId, range.Low, range.High)))
            .ToList();

        return
        [
            new NpgsqlParameter("rangeObjectIds", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ranges.Select(r => r.ObjectId).ToArray() },
            new NpgsqlParameter("rangeLows", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = ranges.Select(r => r.Low).ToArray() },
            new NpgsqlParameter("rangeHighs", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = ranges.Select(r => r.High).ToArray() },
            new NpgsqlParameter("includedValueIds", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = includedValueIds.ToArray() }
        ];
    }

    /// <summary>
    /// The objects' values, untracked, but for the attributes each leaves out, of which only <paramref name="includedValueIds"/>
    /// are loaded; each with the object it belongs to and its attribute (and that attribute's Object Type), in value id order.
    /// The values carry no navigations: the caller joins them to its objects and attributes.
    /// </summary>
    private async Task<List<(Guid ObjectId, ConnectedSystemObjectAttributeValue Value, ConnectedSystemObjectTypeAttribute Attribute, int AttributeTypeId)>>
        GetAttributeValuesExcludingAsync(IEnumerable<(Guid ObjectId, IReadOnlyCollection<int> ExcludedAttributeIds)> objects,
            IReadOnlyCollection<Guid> includedValueIds)
    {
        var rows = await Repository.Database.ConnectedSystemObjectAttributeValues
            .FromSqlRaw(AttributeValuesByRangesSql, AttributeValuesByRangesParameters(objects, includedValueIds).Cast<object>().ToArray())
            .AsNoTrackingWithIdentityResolution()
            .Select(value => new
            {
                ObjectId = EF.Property<Guid>(value, "ConnectedSystemObjectId"),
                Value = value,
                value.Attribute,
                AttributeTypeId = value.Attribute.ConnectedSystemObjectType.Id
            })
            .ToListAsync();

        return rows
            .DistinctBy(row => row.Value.Id)
            .OrderBy(row => row.Value.Id)
            .Select(row => (row.ObjectId, row.Value, row.Attribute, row.AttributeTypeId))
            .ToList();
    }

    /// <summary>
    /// The inclusive ranges of attribute id that cover every id but <paramref name="excludedSortedIds"/>.
    /// </summary>
    internal static IEnumerable<(int Low, int High)> AttributeIdRangesExcluding(IReadOnlyList<int> excludedSortedIds)
    {
        long low = int.MinValue;
        foreach (var excluded in excludedSortedIds)
        {
            if (excluded - 1L >= low)
                yield return ((int)low, excluded - 1);
            low = excluded + 1L;
        }

        if (low <= int.MaxValue)
            yield return ((int)low, int.MaxValue);
    }
}
