// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;

namespace JIM.Web.Models;

/// <summary>
/// What the Schema tab's attribute dialog offers, and what confirming it changes. Two things can be edited, with
/// different reach: the data type, only where the Connector says its schema cannot state one definitively; and
/// whether the values are case-exact, for any Text attribute on any Connector (#2051).
/// </summary>
public static class SchemaAttributeEdit
{
    /// <summary>
    /// Whether the dialog has anything to offer for this attribute.
    /// </summary>
    public static bool CanEdit(ConnectedSystemObjectTypeAttribute attribute, bool supportsUserSelectedTypes) =>
        supportsUserSelectedTypes || OffersCaseExact(attribute.Type);

    /// <summary>
    /// Whether an attribute of this type can be declared case-exact. Only text compares with or without case.
    /// </summary>
    public static bool OffersCaseExact(AttributeDataType type) => type == AttributeDataType.Text;

    /// <summary>
    /// Applies the dialog's choices. The type is applied only where the Connector allows an override and the
    /// attribute is not yet referenced or holding values; the case-exact declaration only where it changed, so
    /// confirming a type change does not pin the declaration discovery stated.
    /// </summary>
    public static void Apply(
        ConnectedSystemObjectTypeAttribute attribute,
        bool supportsUserSelectedTypes,
        bool typeEditable,
        AttributeDataType? chosenType,
        bool caseExact)
    {
        if (supportsUserSelectedTypes && typeEditable && chosenType.HasValue)
        {
            attribute.Type = chosenType.Value;

            // Marks the type as the administrator's rather than the Connector's, so the next schema refresh
            // leaves it alone instead of silently restoring what discovery inferred (#1354).
            attribute.TypeSetByAdministrator = true;
        }

        if (!OffersCaseExact(attribute.Type) || attribute.CaseExact == caseExact)
            return;

        attribute.CaseExact = caseExact;

        // Likewise the declaration: a schema refresh keeps the administrator's answer.
        attribute.CaseExactSetByAdministrator = true;
    }
}
