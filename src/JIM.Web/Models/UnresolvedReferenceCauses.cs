// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Services;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;

namespace JIM.Web.Models;

/// <summary>
/// The likely causes of the unresolved reference values a Connected System Object page has on screen (#959), for the
/// cause chip beside each one. The page's attribute table and each nested value table load their own values, so each
/// asks this to diagnose what it loaded; a value is diagnosed once, however often it is drawn.
/// </summary>
/// <remarks>
/// Diagnosis goes through the same server method as the Unresolved References section's table, so a chip and the
/// section's row for the same value always agree.
/// </remarks>
public sealed class UnresolvedReferenceCauses(ConnectedSystemObjectUnresolvedReferenceContext context)
{
    private readonly Dictionary<Guid, ConnectedSystemObjectUnresolvedReference> _byValueId = [];

    /// <summary>
    /// Whether a value is an unresolved reference: imported as a reference and not linked to an object. A value that has
    /// since resolved keeps its imported text beside the link, so the link is what rules it out.
    /// </summary>
    public static bool IsUnresolved(ConnectedSystemObjectAttributeValue value) =>
        value.UnresolvedReferenceValue != null && value.ReferenceValueId == null && value.ReferenceValue == null;

    /// <summary>Diagnoses whichever of <paramref name="values"/> are unresolved and not yet diagnosed.</summary>
    public async Task DiagnoseAsync(JimApplication jim, IEnumerable<ConnectedSystemObjectAttributeValue> values)
    {
        var pending = values.Where(v => IsUnresolved(v) && !_byValueId.ContainsKey(v.Id)).DistinctBy(v => v.Id).ToList();
        if (pending.Count == 0)
            return;

        var diagnoses = await jim.ConnectedSystems.DiagnoseUnresolvedReferencesAsync(context,
        [
            .. pending.Select(v => new ConnectedSystemObjectUnresolvedReferenceValue
            {
                AttributeId = v.AttributeId != 0 ? v.AttributeId : v.Attribute.Id,
                AttributeName = v.Attribute.Name,
                Value = v.UnresolvedReferenceValue!
            })
        ]);

        foreach (var (value, diagnosis) in pending.Zip(diagnoses))
            _byValueId[value.Id] = diagnosis;
    }

    /// <summary>The value's diagnosis, or null where it is not an unresolved reference or has not been diagnosed.</summary>
    public ConnectedSystemObjectUnresolvedReference? For(ConnectedSystemObjectAttributeValue value) =>
        _byValueId.GetValueOrDefault(value.Id);
}
