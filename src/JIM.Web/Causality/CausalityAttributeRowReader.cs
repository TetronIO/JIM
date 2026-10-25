// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Utility;

namespace JIM.Web.Causality;

/// <summary>
/// Reads one window of a change record's attribute rows for the causality panel (#2498), through the side the change
/// belongs to. The pages hosting the panel call it; the panel itself has no data access of its own.
/// </summary>
public static class CausalityAttributeRowReader
{
    public static async Task<RangeResultSet<CausalityAttributeRow>> ReadAsync(
        JimApplication jim, CausalityAttributeRowSource source, ChangeValueRowQuery query)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Side == CausalityChangeSide.MetaverseObject)
        {
            var read = await jim.Metaverse.GetMvoChangeValueRowsAsync(query);
            return new RangeResultSet<CausalityAttributeRow> { Results = read.Results.Select(CausalityAttributeRowMapper.FromRow).ToList(), TotalResults = read.TotalResults };
        }

        var cso = await jim.ConnectedSystems.GetCsoChangeValueRowsAsync(query);
        return new RangeResultSet<CausalityAttributeRow> { Results = cso.Results.Select(CausalityAttributeRowMapper.FromRow).ToList(), TotalResults = cso.TotalResults };
    }
}
