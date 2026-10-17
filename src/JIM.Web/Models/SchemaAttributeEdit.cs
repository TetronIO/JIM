// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;

namespace JIM.Web.Models;

/// <summary>
/// What the Schema tab's attribute dialog offers, and what confirming it changes. Three things can be edited, with
/// different reach: the data type, only where the Connector says its schema cannot state one definitively (applied
/// through the server's shared rule set); whether the values are case-exact, for any Text attribute on any Connector
/// (#2051); and the step of time a date is held to, for any DateTime attribute on any Connector (#2153).
/// </summary>
public static class SchemaAttributeEdit
{
    /// <summary>
    /// Whether the dialog has anything to offer for this attribute.
    /// </summary>
    public static bool CanEdit(ConnectedSystemObjectTypeAttribute attribute, bool supportsUserSelectedTypes) =>
        supportsUserSelectedTypes || OffersCaseExact(attribute.Type) || OffersDateTimeResolution(attribute.Type);

    /// <summary>
    /// Whether an attribute of this type can be declared case-exact. Only text compares with or without case.
    /// </summary>
    public static bool OffersCaseExact(AttributeDataType type) => type == AttributeDataType.Text;

    /// <summary>
    /// Whether an attribute of this type has a date and time resolution (#2153). Only a date does.
    /// </summary>
    public static bool OffersDateTimeResolution(AttributeDataType type) => type == AttributeDataType.DateTime;

    /// <summary>
    /// The resolution the dialog shows for an attribute: the one it holds, or the microsecond for none, which is what
    /// none means.
    /// </summary>
    public static DateTimeResolution EffectiveDateTimeResolution(ConnectedSystemObjectTypeAttribute attribute) =>
        attribute.DateTimeResolution ?? DateTimeResolution.Microsecond;

    /// <summary>
    /// Applies the dialog's resolution choice, only where it changed (none and the microsecond being the same), so
    /// confirming the dialog for another reason does not pin the resolution discovery stated.
    /// </summary>
    public static void ApplyDateTimeResolution(ConnectedSystemObjectTypeAttribute attribute, DateTimeResolution resolution)
    {
        if (!OffersDateTimeResolution(attribute.Type) || EffectiveDateTimeResolution(attribute) == resolution)
            return;

        attribute.DateTimeResolution = resolution;

        // A schema refresh keeps the administrator's answer.
        attribute.DateTimeResolutionSetByAdministrator = true;
    }

    /// <summary>
    /// A resolution as the portal names it: "Second", "Ten Milliseconds".
    /// </summary>
    public static string Describe(DateTimeResolution resolution) => resolution switch
    {
        DateTimeResolution.TenMicroseconds => "Ten Microseconds",
        DateTimeResolution.HundredMicroseconds => "Hundred Microseconds",
        DateTimeResolution.TenMilliseconds => "Ten Milliseconds",
        DateTimeResolution.HundredMilliseconds => "Hundred Milliseconds",
        _ => resolution.ToString()
    };

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
