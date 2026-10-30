// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Interfaces;
using JIM.Models.Utility;
using JIM.Web.Shared;

namespace JIM.Web.Models;

/// <summary>
/// Reads a Connected System Object change's values for the change history timeline (#2498); see
/// <see cref="IChangeHistoryValueReader"/>.
/// </summary>
public sealed class CsoChangeHistoryValueReader(IJimApplicationFactory jimFactory) : IChangeHistoryValueReader
{
    public async Task<RangeResultSet<ChangeHistoryTimeline.AttributeChangeDisplay>> ReadValuesAsync(ChangeHistoryTimeline.ChangeGroup change, ChangeValueRowQuery query)
    {
        using var jim = jimFactory.Create();
        var result = await jim.ConnectedSystems.GetCsoChangeValueRowsAsync(query);
        return new RangeResultSet<ChangeHistoryTimeline.AttributeChangeDisplay>
        {
            Results = result.Results.Select(row => ChangeHistoryTimeline.ToDisplay(
                row.Operation,
                CsoChangeGroupMapper.ToAttributeChange(row.AttributeName, row.AttributePlurality, row.AttributeType, row.Value),
                row.PreviousValue is { } previous ? CsoChangeGroupMapper.ToAttributeChange(row.AttributeName, row.AttributePlurality, row.AttributeType, previous) : null))
                .ToList(),
            TotalResults = result.TotalResults
        };
    }

    public async Task<List<ChangeValueAttributeSummary>> ReadAttributeSummariesAsync(ChangeHistoryTimeline.ChangeGroup change)
    {
        if (change.ChangeId is not { } changeId)
            return [];

        using var jim = jimFactory.Create();
        return await jim.ConnectedSystems.GetCsoChangeAttributeValueSummariesAsync(changeId);
    }
}
