// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Staging;

namespace JIM.Web.Models;

/// <summary>
/// What a Run Profile Execution Item's Attribute Values Not Imported section shows (#2066), kept apart from the markup
/// so the logic is tested without rendering it.
/// </summary>
public static class AttributeValueErrorDisplay
{
    /// <summary>
    /// The values an item recorded, or null where it recorded none: an item from before they were kept, or from a
    /// Connector that reports a message alone, keeps its Error Details instead.
    /// </summary>
    public static ImportedAttributeValueErrors? ForItem(string? attributeValueErrorsJson) =>
        ImportedAttributeValueErrors.FromJson(attributeValueErrorsJson) is { Values.Count: > 0 } recorded ? recorded : null;

    /// <summary>The values whose attribute or value contains the search text, whatever its case; all of them for none.</summary>
    public static IEnumerable<ImportedAttributeValueError> Matching(IEnumerable<ImportedAttributeValueError> values, string? searchText) =>
        string.IsNullOrWhiteSpace(searchText)
            ? values
            : values.Where(v => v.AttributeName.Contains(searchText, StringComparison.OrdinalIgnoreCase)
                                || (v.Value?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false));

    /// <summary>What an attribute of the type expects, in words; null where the type is unknown.</summary>
    public static string? Expected(AttributeDataType? type) => type switch
    {
        AttributeDataType.Text => "Text",
        AttributeDataType.Number or AttributeDataType.LongNumber => "A whole number",
        AttributeDataType.Decimal => "A decimal number",
        AttributeDataType.DateTime => "A date and time",
        AttributeDataType.Boolean => "True or false",
        AttributeDataType.Guid => "A GUID",
        AttributeDataType.Binary => "Binary data",
        AttributeDataType.Reference => "A reference to another object",
        _ => null
    };

    /// <summary>Who found a value could not be imported (#2153): the Connector, or JIM.</summary>
    public static string FoundBy(ImportAttributeValueErrorSource source) =>
        source == ImportAttributeValueErrorSource.Jim ? "JIM" : "Connector";

    /// <summary>The Connected System's Schema tab, open on the Object Type where one is given.</summary>
    public static string SchemaHref(int connectedSystemId, string? objectTypeName) => string.IsNullOrEmpty(objectTypeName)
        ? $"/admin/connected-systems/{connectedSystemId}?t=schema"
        : $"/admin/connected-systems/{connectedSystemId}?t=schema&ot={Uri.EscapeDataString(objectTypeName)}";
}
