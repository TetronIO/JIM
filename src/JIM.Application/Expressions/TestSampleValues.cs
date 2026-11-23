// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Resources;
using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Localisation;
using JIM.Models.Logic;
using JIM.Models.Staging;

namespace JIM.Application.Expressions;

/// <summary>
/// Sample values for the configuration testers (#2055), typed from the schema and held as attribute values on a
/// transient object, so a tester evaluates the object synchronisation would read. Shared by the Attribute Flow and
/// Scope testers, so a sample means the same thing to both.
/// </summary>
internal static class TestSampleValues
{
    /// <summary>
    /// The input's samples converted to the attribute's type, as an imported value of it would be held. A sample that
    /// cannot be converted, or several samples for a single-valued attribute, adds an error instead.
    /// </summary>
    /// <param name="label">How messages name the input: an Expression's accessor (<c>cs["sn"]</c>), or an attribute's
    /// name in quotes.</param>
    /// <param name="errors">Receives each problem as its British English, for the testers that read text.</param>
    internal static List<object> Typed(string label, IReadOnlyList<object> samples, AttributeDataType type, AttributePlurality plurality,
        List<string> errors)
    {
        var problems = new List<JimMessage>();
        var typedValues = Typed(label, samples, type, plurality, problems);
        errors.AddRange(problems.Select(problem => problem.ToEnglish()));
        return typedValues;
    }

    /// <summary>
    /// <see cref="Typed(string, IReadOnlyList{object}, AttributeDataType, AttributePlurality, List{string})"/>, with each
    /// problem as a message (#3030) so the portal can show it in the viewer's language.
    /// </summary>
    internal static List<object> Typed(string label, IReadOnlyList<object> samples, AttributeDataType type, AttributePlurality plurality,
        List<JimMessage> errors)
    {
        if (plurality == AttributePlurality.SingleValued && samples.Count > 1)
        {
            errors.Add(ExpressionMessage.Of(nameof(ExpressionMessages.SampleTooManyValues), label, ExpressionMessage.Digits(samples.Count)));
            return [];
        }

        var typedValues = new List<object>();
        foreach (var sample in samples)
        {
            if (TryConvertSample(sample, type, out var typed))
            {
                typedValues.Add(typed);
                continue;
            }

            var sampleText = ExpressionText.Describe(sample);
            var typeName = ExpressionMessage.DataType(type);
            errors.Add(type switch
            {
                AttributeDataType.DateTime => ExpressionMessage.Of(nameof(ExpressionMessages.SampleWrongTypeWithHint), sampleText, label, typeName,
                    ExpressionMessage.Of(nameof(ExpressionMessages.SampleHintDate))),
                AttributeDataType.Reference => ExpressionMessage.Of(nameof(ExpressionMessages.SampleWrongTypeWithHint), sampleText, label, typeName,
                    ExpressionMessage.Of(nameof(ExpressionMessages.SampleHintReference))),
                AttributeDataType.Binary => ExpressionMessage.Of(nameof(ExpressionMessages.SampleWrongTypeWithHint), sampleText, label, typeName,
                    ExpressionMessage.Of(nameof(ExpressionMessages.SampleHintBinary))),
                _ => ExpressionMessage.Of(nameof(ExpressionMessages.SampleWrongType), sampleText, label, typeName)
            });
        }

        return typedValues;
    }

    internal static bool TryConvertSample(object sample, AttributeDataType type, out object typed)
    {
        typed = null!;
        switch (type)
        {
            case AttributeDataType.Reference:
                // A reference reads as the id of the object it refers to.
                if (sample is Guid guid || Guid.TryParse(sample as string, out guid))
                {
                    typed = guid;
                    return true;
                }
                return false;

            case AttributeDataType.Binary when sample is string base64:
                var buffer = new byte[base64.Length];
                if (!Convert.TryFromBase64String(base64, buffer, out var written))
                    return false;
                typed = buffer[..written];
                return true;

            default:
                if (!ExpressionResultConverter.TryConvert(sample, type, out var converted) || converted == null)
                    return false;
                typed = converted;
                return true;
        }
    }

    internal static ConnectedSystemObjectAttributeValue ToConnectedSystemValue(ConnectedSystemObjectTypeAttribute attribute, object value)
    {
        var attributeValue = new ConnectedSystemObjectAttributeValue { Attribute = attribute, AttributeId = attribute.Id };
        if (attribute.Type == AttributeDataType.Reference)
        {
            var id = (Guid)value;
            attributeValue.ReferenceValue = new ConnectedSystemObject { Id = id };
            attributeValue.ReferenceValueId = id;
            return attributeValue;
        }

        switch (value)
        {
            case string text: attributeValue.StringValue = text; break;
            case int number: attributeValue.IntValue = number; break;
            case long longNumber: attributeValue.LongValue = longNumber; break;
            case decimal decimalNumber: attributeValue.DecimalValue = decimalNumber; break;
            case DateTime date: attributeValue.DateTimeValue = date; break;
            case bool flag: attributeValue.BoolValue = flag; break;
            case Guid guid: attributeValue.GuidValue = guid; break;
            case byte[] bytes: attributeValue.ByteValue = bytes; break;
        }

        return attributeValue;
    }

    internal static MetaverseObjectAttributeValue ToMetaverseValue(MetaverseAttribute attribute, object value)
    {
        var attributeValue = new MetaverseObjectAttributeValue { Attribute = attribute, AttributeId = attribute.Id };
        if (attribute.Type == AttributeDataType.Reference)
        {
            attributeValue.ReferenceValueId = (Guid)value;
            return attributeValue;
        }

        switch (value)
        {
            case string text: attributeValue.StringValue = text; break;
            case int number: attributeValue.IntValue = number; break;
            case long longNumber: attributeValue.LongValue = longNumber; break;
            case decimal decimalNumber: attributeValue.DecimalValue = decimalNumber; break;
            case DateTime date: attributeValue.DateTimeValue = date; break;
            case bool flag: attributeValue.BoolValue = flag; break;
            case Guid guid: attributeValue.GuidValue = guid; break;
            case byte[] bytes: attributeValue.ByteValue = bytes; break;
        }

        return attributeValue;
    }

    /// <summary>
    /// A Connected System Object's value as a sample: as an Expression reads it, a reference as the id of the object
    /// it refers to. Null where the row holds nothing.
    /// </summary>
    internal static object? FromConnectedSystemValue(ConnectedSystemObjectAttributeValue value, AttributeDataType type) =>
        type == AttributeDataType.Reference
            ? value.ReferenceValueId ?? value.ReferenceValue?.Id
            : SyncEngine.ToExpressionValue(value, type);

    /// <summary>
    /// A Metaverse Object's value as a sample, a reference as the id of the object it refers to. Null where the row
    /// holds nothing.
    /// </summary>
    internal static object? FromMetaverseValue(MetaverseObjectAttributeValue value, AttributeDataType type) => type switch
    {
        AttributeDataType.Text => value.StringValue,
        AttributeDataType.Number => value.IntValue,
        AttributeDataType.LongNumber => value.LongValue,
        AttributeDataType.Decimal => value.DecimalValue,
        AttributeDataType.DateTime => value.DateTimeValue,
        AttributeDataType.Boolean => value.BoolValue,
        AttributeDataType.Guid => value.GuidValue,
        AttributeDataType.Binary => value.ByteValue,
        AttributeDataType.Reference => value.ReferenceValueId ?? value.ReferenceValue?.Id,
        _ => null
    };
}
