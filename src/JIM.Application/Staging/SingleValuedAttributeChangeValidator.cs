// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;

namespace JIM.Application.Staging;

/// <summary>
/// Checks, just before an export is sent, that it carries at most one change for each single-valued attribute (#2182).
/// </summary>
/// <remarks>
/// Export staging gives a single-valued attribute exactly one change, an Update carrying the value it should hold, and
/// merging keeps it that way. An export carrying two contradicts itself: a Connector either refuses it in its own terms
/// (a database rejecting a column assigned twice) or, worse, applies the changes in turn (an add then a path-only
/// remove clears a SCIM attribute) and reports success. Refusing it here, against its own object, means a regression in
/// staging or merging can never reach a Connected System. The refusal is a general error rather than an
/// administrator-actionable one, because it is a fault in JIM: downstream that fails the Activity, which is the point.
/// </remarks>
public static class SingleValuedAttributeChangeValidator
{
    /// <summary>
    /// Returns a failure naming the single-valued attributes the export carries more than one change for, or null when
    /// it carries at most one for each.
    /// </summary>
    /// <param name="pendingExport">The export as the Connector would be handed it.</param>
    public static ConnectedSystemExportResult? Check(PendingExport pendingExport)
    {
        ArgumentNullException.ThrowIfNull(pendingExport);

        var contradicted = pendingExport.AttributeValueChanges
            .Where(change => change.Attribute?.AttributePlurality == AttributePlurality.SingleValued)
            .GroupBy(change => change.AttributeId)
            .Where(changes => changes.Skip(1).Any())
            .Select(changes => changes.First().Attribute.Name)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (contradicted.Count == 0)
            return null;

        return ConnectedSystemExportResult.Failed(
            $"This export carries more than one change for the single-valued attribute(s) {string.Join(", ", contradicted.Select(name => $"'{name}'"))}, " +
            "so it does not say which value the object should hold, and JIM has not sent it. A single-valued attribute is exported as one change " +
            "carrying the value it should hold; check whether more than one Attribute Flow writes to the attribute.");
    }
}
