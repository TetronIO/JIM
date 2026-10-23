// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Enums;
using JIM.Models.History;

namespace JIM.Web.Models;

/// <summary>
/// One attribute a Metaverse Object change touched, reduced to what an Object Changes row shows: the values removed
/// and added, as text.
/// </summary>
public sealed record ObjectChangeSummary(
    string AttributeName,
    bool SingleValued,
    bool Matched,
    IReadOnlyList<string> RemovedValues,
    IReadOnlyList<string> AddedValues)
{
    /// <summary>
    /// Summarises each attribute a search result changed: the attributes the search's conditions named first, so the
    /// row leads with why it matched, then the rest by name.
    /// </summary>
    public static IReadOnlyList<ObjectChangeSummary> Summarise(MetaverseObjectChangeSearchResult result) =>
        result.Change.AttributeChanges
            .Select(ac => new ObjectChangeSummary(
                ac.AttributeName,
                ac.AttributePlurality == AttributePlurality.SingleValued,
                ac.AttributeId.HasValue && result.MatchedAttributeIds.Contains(ac.AttributeId.Value),
                ValueTexts(ac, ValueChangeType.Remove),
                ValueTexts(ac, ValueChangeType.Add)))
            .OrderByDescending(s => s.Matched)
            .ThenBy(s => s.AttributeName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<string> ValueTexts(MvoAttributeChangeDto attributeChange, ValueChangeType valueChangeType) =>
        attributeChange.ValueChanges
            .Where(v => v.ValueChangeType == valueChangeType)
            .Select(v => v.ReferenceValue?.DisplayName ?? v.ToDisplayString())
            .ToList();
}
