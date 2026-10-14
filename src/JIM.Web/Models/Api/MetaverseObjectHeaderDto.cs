// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Utilities;

namespace JIM.Web.Models.Api;

/// <summary>
/// Lightweight DTO for Metaverse Objects in list views.
/// </summary>
public class MetaverseObjectHeaderDto
{
    /// <summary>
    /// The unique identifier (GUID) of the Metaverse Object.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// When the object was created.
    /// </summary>
    public DateTime Created { get; set; }

    /// <summary>
    /// The display name of the object (always included).
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// The current status of the object.
    /// </summary>
    public MetaverseObjectStatus Status { get; set; }

    /// <summary>
    /// The Metaverse Object Type, with its Id and Name.
    /// </summary>
    /// <remarks>
    /// Nested to match the single-object response shape.
    /// </remarks>
    public MetaverseObjectTypeDto Type { get; set; } = null!;

    /// <summary>
    /// The object's other attribute values, keyed by attribute name: the attributes the request named, or for a
    /// Predefined Search, the attributes the search lists. Each value has its natural type: text, a number, a
    /// boolean, an ISO-8601 date and time, a GUID, or base64 for Binary; a Reference holds the referenced Metaverse
    /// Object's ID as a GUID. A multi-valued attribute shows one of its values (the last). DisplayName is not
    /// included, as it has its own property.
    /// </summary>
    /// <remarks>
    /// The REST list endpoint takes the names in its <c>attributes</c> query parameter. Base64 is System.Text.Json's
    /// representation for a byte array, and a Reference matches MetaverseObjectAttributeValueDto.ReferenceValueId.
    /// </remarks>
    public Dictionary<string, object?> Attributes { get; set; } = new();

    /// <summary>
    /// Creates a DTO from a MetaverseObjectHeader.
    /// </summary>
    public static MetaverseObjectHeaderDto FromHeader(MetaverseObjectHeader header)
    {
        var dto = new MetaverseObjectHeaderDto
        {
            Id = header.Id,
            Created = header.Created,
            DisplayName = header.Name,
            Status = header.Status,
            Type = new MetaverseObjectTypeDto
            {
                Id = header.TypeId,
                Name = header.TypeName
            }
        };

        // Add any additional attributes (excluding DisplayName which has its own property)
        foreach (var av in header.AttributeValues.Where(av => av.Attribute.Name != Constants.BuiltInAttributes.DisplayName))
        {
            dto.Attributes[av.Attribute.Name] = GetTypedValue(av);
        }

        return dto;
    }

    /// <summary>
    /// Surfaces the populated value field for an attribute value in its natural type, so JSON
    /// serialisation produces the right JSON type per Attribute Data Type (see the
    /// <see cref="Attributes"/> documentation). Returns null when no value field is populated,
    /// for example an asserted-null row.
    /// </summary>
    private static object? GetTypedValue(MetaverseObjectAttributeValue av)
    {
        if (av.StringValue != null)
            return av.StringValue;

        if (av.DateTimeValue.HasValue)
            return DateTimeAttributeValue.ToCanonical(av.DateTimeValue.Value);

        if (av.IntValue.HasValue)
            return av.IntValue.Value;

        if (av.LongValue.HasValue)
            return av.LongValue.Value;

        if (av.DecimalValue.HasValue)
            return av.DecimalValue.Value;

        if (av.ByteValue != null)
            return av.ByteValue;

        if (av.GuidValue.HasValue)
            return av.GuidValue.Value;

        if (av.BoolValue.HasValue)
            return av.BoolValue.Value;

        if (av.ReferenceValueId.HasValue)
            return av.ReferenceValueId.Value;

        // The FK scalar is preferred above, but fall back to the navigation in case only it was populated.
        if (av.ReferenceValue != null)
            return av.ReferenceValue.Id;

        return null;
    }
}
