// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Interfaces;
using JIM.Models.Utility;
using JIM.Web.Shared;

namespace JIM.Web.Models;

/// <summary>
/// Reads a Metaverse Object change's values for the change history timeline (#2498); see
/// <see cref="IChangeHistoryValueReader"/>.
/// </summary>
/// <param name="jimFactory">Creates the application each read runs on.</param>
/// <param name="metaverseObjectId">The object whose history it is, for each attribute's Inspect link.</param>
/// <param name="typePluralName">The object's type plural name, for the Inspect link; no link when null.</param>
public sealed class MvoChangeHistoryValueReader(IJimApplicationFactory jimFactory, Guid metaverseObjectId, string? typePluralName) : IChangeHistoryValueReader
{
    public async Task<RangeResultSet<ChangeHistoryTimeline.AttributeChangeDisplay>> ReadValuesAsync(ChangeHistoryTimeline.ChangeGroup change, ChangeValueRowQuery query)
    {
        using var jim = jimFactory.Create();
        var result = await jim.Metaverse.GetMvoChangeValueRowsAsync(query);
        return new RangeResultSet<ChangeHistoryTimeline.AttributeChangeDisplay>
        {
            Results = result.Results.Select(row => ChangeHistoryTimeline.ToDisplay(
                row.Operation,
                MvoChangeGroupMapper.ToAttributeChange(row.AttributeName, row.AttributePlurality, row.AttributeType, row.AttributeId, row.Value, metaverseObjectId, typePluralName),
                row.PreviousValue is { } previous
                    ? MvoChangeGroupMapper.ToAttributeChange(row.AttributeName, row.AttributePlurality, row.AttributeType, row.AttributeId, previous, metaverseObjectId, typePluralName)
                    : null))
                .ToList(),
            TotalResults = result.TotalResults
        };
    }

    public async Task<List<ChangeValueAttributeSummary>> ReadAttributeSummariesAsync(ChangeHistoryTimeline.ChangeGroup change)
    {
        if (change.ChangeId is not { } changeId)
            return [];

        using var jim = jimFactory.Create();
        return await jim.Metaverse.GetMvoChangeAttributeValueSummariesAsync(changeId);
    }
}
