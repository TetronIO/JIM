// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Transactional;
using System.Globalization;

namespace JIM.Application.Servers.Preview;

/// <summary>
/// Renders an attribute value as the text a preview delta carries in its old and new value columns. One renderer for
/// every preview, so the same value reads the same way whichever adapter reported it and whichever direction it
/// travels in.
/// </summary>
internal static class PreviewValueRenderer
{
    /// <summary>
    /// A Metaverse Object attribute value, without the attribute-name prefix the entity's own ToString carries; null for
    /// an asserted-null marker or an empty value.
    /// </summary>
    internal static string? Render(MetaverseObjectAttributeValue value)
    {
        if (value.NullValue)
            return null;
        if (value.StringValue != null)
            return value.StringValue;
        if (value.IntValue.HasValue)
            return value.IntValue.Value.ToString();
        if (value.LongValue.HasValue)
            return value.LongValue.Value.ToString();
        if (value.DecimalValue.HasValue)
            return value.DecimalValue.Value.ToString(CultureInfo.InvariantCulture);
        if (value.DateTimeValue.HasValue)
            return value.DateTimeValue.Value.ToString("O");
        if (value.BoolValue.HasValue)
            return value.BoolValue.Value.ToString();
        if (value.GuidValue.HasValue)
            return value.GuidValue.Value.ToString();
        if (value.ReferenceValueId.HasValue || value.ReferenceValue != null)
            return (value.ReferenceValueId ?? value.ReferenceValue!.Id).ToString();
        if (value.UnresolvedReferenceValueId.HasValue || value.UnresolvedReferenceValue != null)
            return (value.UnresolvedReferenceValueId ?? value.UnresolvedReferenceValue!.Id).ToString();
        if (value.ByteValue != null)
            return $"{value.ByteValue.Length} bytes";
        return null;
    }

    /// <summary>
    /// A value staged for export to a target Connected System; null for a change that clears the attribute.
    /// </summary>
    internal static string? Render(PendingExportAttributeValueChange change)
    {
        if (change.StringValue != null)
            return change.StringValue;
        if (change.IntValue.HasValue)
            return change.IntValue.Value.ToString(CultureInfo.InvariantCulture);
        if (change.LongValue.HasValue)
            return change.LongValue.Value.ToString(CultureInfo.InvariantCulture);
        if (change.DecimalValue.HasValue)
            return change.DecimalValue.Value.ToString(CultureInfo.InvariantCulture);
        if (change.DateTimeValue.HasValue)
            return change.DateTimeValue.Value.ToString("O", CultureInfo.InvariantCulture);
        if (change.BoolValue.HasValue)
            return change.BoolValue.Value.ToString();
        if (change.GuidValue.HasValue)
            return change.GuidValue.Value.ToString();
        if (change.UnresolvedReferenceValue != null)
            return change.UnresolvedReferenceValue;
        if (change.ResolvedReferenceCsoId.HasValue)
            return change.ResolvedReferenceCsoId.Value.ToString();
        if (change.ByteValue != null)
            return $"{change.ByteValue.Length} bytes";
        return null;
    }

    /// <summary>
    /// Several values of one attribute as one cell: each rendered, empty ones dropped, in a stable order so the same set
    /// always reads the same. Null when nothing is left, which is how a cell says "no value".
    /// </summary>
    internal static string? Join(IEnumerable<string?> renderedValues)
    {
        var present = renderedValues.Where(v => !string.IsNullOrEmpty(v)).Select(v => v!).OrderBy(v => v, StringComparer.Ordinal).ToList();
        return present.Count == 0 ? null : string.Join(", ", present);
    }
}
