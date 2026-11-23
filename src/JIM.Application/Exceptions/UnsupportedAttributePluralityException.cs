// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Localisation;
using JIM.Application.Resources;
using JIM.Models.Core;
using JIM.Models.Localisation;

namespace JIM.Application.Exceptions;

/// <summary>
/// Thrown when a Metaverse Attribute would be created or changed to a plurality its data type cannot hold: today, a
/// multi-valued Boolean (see <see cref="AttributeDataTypeCapabilities"/>). Nothing is created or changed. The REST API
/// maps it to 400 Bad Request carrying <see cref="System.Exception.Message"/>, which names the rule.
/// </summary>
public class UnsupportedAttributePluralityException : InvalidOperationException, IJimMessageCarrier
{
    /// <summary>
    /// The data type that was requested.
    /// </summary>
    public AttributeDataType DataType { get; }

    /// <summary>
    /// The plurality that was requested.
    /// </summary>
    public AttributePlurality Plurality { get; }

    /// <summary>
    /// The refusal, for rendering in the reader's language.
    /// </summary>
    public JimMessage Detail { get; }

    public UnsupportedAttributePluralityException(AttributeDataType dataType, AttributePlurality plurality)
        : this(dataType, plurality, dataType == AttributeDataType.Boolean
            ? ServerMessage.Create(nameof(ServerMessages.MultiValuedBooleanRule))
            : ServerMessage.Create(nameof(ServerMessages.AttributePluralityUnsupported), dataType.ToString(), plurality.ToString()))
    {
    }

    private UnsupportedAttributePluralityException(AttributeDataType dataType, AttributePlurality plurality, JimMessage detail)
        : base(detail.ToEnglish())
    {
        DataType = dataType;
        Plurality = plurality;
        Detail = detail;
    }
}
