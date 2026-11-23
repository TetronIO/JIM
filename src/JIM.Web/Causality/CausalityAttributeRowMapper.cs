// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Enums;
using JIM.Models.Staging.DTOs;
using System.Globalization;
using JIM.Web.Resources;

namespace JIM.Web.Causality;

/// <summary>
/// Turns the rows a window read returns into the causality panel's attribute rows (#2498). The reads normalise in SQL
/// exactly as <see cref="CausalityModelBuilder"/> normalises a change it carries, so either way a row reads the same.
/// </summary>
public static class CausalityAttributeRowMapper
{
    public static CausalityAttributeRow FromRow(CsoChangeValueRow row) => new(
        ToOperation(row.Operation),
        row.AttributeName,
        TypeAndPlurality(row.AttributeType, row.AttributePlurality == AttributePlurality.MultiValued),
        ValueText(row.Value),
        row.PreviousValue is { } previous ? ValueText(previous) : null,
        row.Value.SyncRuleId,
        row.Value.SyncRuleName);

    public static CausalityAttributeRow FromRow(MvoChangeValueRow row) => new(
        ToOperation(row.Operation),
        row.AttributeName,
        TypeAndPlurality(row.AttributeType, row.AttributePlurality == AttributePlurality.MultiValued),
        ValueText(row.Value),
        row.PreviousValue is { } previous ? ValueText(previous) : null,
        row.Value.ContributedBySyncRuleId,
        row.Value.ContributedBySyncRuleName);

    /// <summary>
    /// A row's demoted type and plurality, such as "Text · Single-valued".
    /// </summary>
    public static string TypeAndPlurality(AttributeDataType attributeType, bool isMultiValued)
    {
        var typeName = attributeType switch
        {
            AttributeDataType.NotSet => CausalityText.AttributeTypeUnknown,
            AttributeDataType.Text => CausalityText.AttributeTypeText,
            AttributeDataType.Number => CausalityText.AttributeTypeNumber,
            AttributeDataType.DateTime => CausalityText.AttributeTypeDateTime,
            AttributeDataType.Binary => CausalityText.AttributeTypeBinary,
            AttributeDataType.Reference => CausalityText.AttributeTypeReference,
            AttributeDataType.Guid => CausalityText.AttributeTypeGuid,
            AttributeDataType.Boolean => CausalityText.AttributeTypeBoolean,
            AttributeDataType.LongNumber => CausalityText.AttributeTypeLongNumber,
            _ => attributeType.ToString()
        };

        return string.Format(CultureInfo.CurrentCulture, CausalityText.AttributeTypeAndPlurality, typeName,
            isMultiValued ? CausalityText.AttributePluralityMulti : CausalityText.AttributePluralitySingle);
    }

    public static CausalityAttributeOperation ToOperation(ChangeValueOperation operation) => operation switch
    {
        ChangeValueOperation.Set => CausalityAttributeOperation.Set,
        ChangeValueOperation.Add => CausalityAttributeOperation.Add,
        _ => CausalityAttributeOperation.Remove
    };

    public static ChangeValueOperation ToChangeValueOperation(CausalityAttributeOperation operation) => operation switch
    {
        CausalityAttributeOperation.Set => ChangeValueOperation.Set,
        CausalityAttributeOperation.Add => ChangeValueOperation.Add,
        _ => ChangeValueOperation.Remove
    };

    /// <summary>
    /// A Connected System Object value as the panel shows it: a reference by the referenced object's name, except a
    /// Pending Export stub, which has no name yet and carries the identifier it was given (the DN); else the value.
    /// </summary>
    private static string ValueText(CsoValueChangeDto value)
    {
        if (value.ReferenceValue is not { } reference)
            return value.ToDisplayString();

        if (value.IsPendingExportStub && !string.IsNullOrEmpty(value.StringValue))
            return value.StringValue;

        return reference.DisplayName ?? reference.Id.ToString();
    }

    private static string ValueText(MvoValueChangeDto value) =>
        value.ReferenceValue is { } reference ? reference.DisplayName ?? reference.Id.ToString() : value.ToDisplayString();
}
