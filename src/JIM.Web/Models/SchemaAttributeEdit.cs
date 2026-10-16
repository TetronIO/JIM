// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;

namespace JIM.Web.Models;

/// <summary>
/// What the Schema tab's attribute dialog offers, and what confirming it changes. Two things can be edited, with
/// different reach: the data type, only where the Connector says its schema cannot state one definitively (applied
/// through the server's shared rule set); and whether the values are case-exact, for any Text attribute on any
/// Connector (#2051).
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
    /// Applies the dialog's case-exact choice, only where it changed, so confirming a type change does not pin the
    /// declaration discovery stated. The data type override is not applied here: it goes through
    /// <c>ConnectedSystemServer.OverrideAttributeTypeAsync</c>, the rule set the REST API shares, before this runs.
    /// </summary>
    public static void ApplyCaseExact(ConnectedSystemObjectTypeAttribute attribute, bool caseExact)
    {
        if (!OffersCaseExact(attribute.Type) || attribute.CaseExact == caseExact)
            return;

        attribute.CaseExact = caseExact;

        // A schema refresh keeps the administrator's answer.
        attribute.CaseExactSetByAdministrator = true;
    }
}
