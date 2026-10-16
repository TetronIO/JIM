// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Staging;

namespace JIM.Web.Models.Api;

/// <summary>
/// A value an import found on an object but could not import: the Connector could not read it as its attribute's type,
/// or JIM could not hold it as read. JIM left the attribute as it already held it.
/// </summary>
/// <remarks>#2066; JIM-raised values from #2153.</remarks>
public class AttributeValueErrorDto
{
    /// <summary>The attribute the value was found on.</summary>
    public string AttributeName { get; set; } = null!;

    /// <summary>The attribute's type when the value was read, which the value failed to be; null where the schema did not know the attribute. Serialised as the enum member name.</summary>
    public AttributeDataType? AttributeType { get; set; }

    /// <summary>The value as the Connector received it, shortened if it was very long; null where it could not be rendered as text.</summary>
    public string? Value { get; set; }

    /// <summary>Why the value could not be imported.</summary>
    public string Reason { get; set; } = null!;

    /// <summary>
    /// Who found the value could not be imported: <c>Connector</c>, which could not read it, or <c>Jim</c>, which could
    /// not hold it as read (two dates less than a microsecond apart, or both true and false for a true or false
    /// attribute). Serialised as the enum member name.
    /// </summary>
    public ImportAttributeValueErrorSource Source { get; set; }

    /// <summary>Creates the DTO from the value recorded on the item.</summary>
    public static AttributeValueErrorDto FromModel(ImportedAttributeValueError model) => new()
    {
        AttributeName = model.AttributeName,
        AttributeType = model.AttributeType,
        Value = model.Value,
        Reason = model.Reason,
        Source = model.Source
    };
}
