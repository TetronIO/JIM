// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;

namespace JIM.Application.Exceptions;

/// <summary>
/// Thrown when a Metaverse Attribute would be created or changed to a plurality its data type cannot hold: today, a
/// multi-valued Boolean (see <see cref="AttributeDataTypeCapabilities"/>). Nothing is created or changed. The REST API
/// maps it to 400 Bad Request carrying <see cref="System.Exception.Message"/>, which names the rule.
/// </summary>
public class UnsupportedAttributePluralityException : InvalidOperationException
{
    /// <summary>
    /// The data type that was requested.
    /// </summary>
    public AttributeDataType DataType { get; }

    /// <summary>
    /// The plurality that was requested.
    /// </summary>
    public AttributePlurality Plurality { get; }

    public UnsupportedAttributePluralityException(AttributeDataType dataType, AttributePlurality plurality)
        : base(dataType == AttributeDataType.Boolean
            ? AttributeDataTypeCapabilities.MultiValuedBooleanRule
            : $"A {dataType} attribute cannot be {plurality}.")
    {
        DataType = dataType;
        Plurality = plurality;
    }
}
