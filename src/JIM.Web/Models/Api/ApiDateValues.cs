// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core.DTOs;
using JIM.Models.Staging.DTOs;
using JIM.Utilities;

namespace JIM.Web.Models.Api;

/// <summary>
/// Puts the dates in a change history page into the form the REST API emits (#2153): UTC, to the microsecond, written
/// with <c>Z</c>. The repository projects these DTOs straight from the database, where Npgsql reads a date as Kind
/// Local, which would serialise with the server's offset. The DTOs live in JIM.Models, which cannot reach
/// <see cref="DateTimeAttributeValue"/>, so the controllers apply this before returning a page.
/// </summary>
public static class ApiDateValues
{
    /// <summary>
    /// Canonicalises each Metaverse Object change's time and every date value it records, in place.
    /// </summary>
    public static void CanonicaliseDates(IEnumerable<MvoChangeHistoryDto> items)
    {
        foreach (var item in items)
        {
            item.ChangeTime = DateTimeAttributeValue.ToCanonical(item.ChangeTime);
            foreach (var valueChange in item.AttributeChanges.SelectMany(a => a.ValueChanges).Where(v => v.DateTimeValue.HasValue))
                valueChange.DateTimeValue = DateTimeAttributeValue.ToCanonical(valueChange.DateTimeValue!.Value);
        }
    }

    /// <summary>
    /// Canonicalises each Connected System Object change's time and every date value it records, in place.
    /// </summary>
    public static void CanonicaliseDates(IEnumerable<CsoChangeHistoryDto> items)
    {
        foreach (var item in items)
        {
            item.ChangeTime = DateTimeAttributeValue.ToCanonical(item.ChangeTime);
            foreach (var valueChange in item.AttributeChanges.SelectMany(a => a.ValueChanges).Where(v => v.DateTimeValue.HasValue))
                valueChange.DateTimeValue = DateTimeAttributeValue.ToCanonical(valueChange.DateTimeValue!.Value);
        }
    }

    /// <summary>
    /// A date value as the REST API emits it, or null for none.
    /// </summary>
    public static DateTime? Canonical(DateTime? value) =>
        value.HasValue ? DateTimeAttributeValue.ToCanonical(value.Value) : null;
}
