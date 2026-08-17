// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.Models.Transactional;

namespace JIM.Application.Staging;

/// <summary>
/// Checks, just before an export is sent, that every class it adds to an object will have the values that class
/// requires.
/// </summary>
/// <remarks>
/// Adding a class obliges the object to satisfy that class's requirements. Sending it anyway has the Connected
/// System reject the change and report the failure in its own terms; refusing it here names the attributes an
/// administrator has to flow. This is the same reasoning as the LDAP Connector's managed-scope refusal.
/// <para>
/// Checked at export rather than at evaluation because the object's values at the Connected System can change in
/// between, and because this is where a per-object refusal already surfaces on the Pending Export.
/// </para>
/// </remarks>
public static class ClassMembershipValidator
{
    /// <summary>
    /// Returns a failure naming the missing attributes, or null when the export is safe to send.
    /// </summary>
    public static ConnectedSystemExportResult? Check(PendingExport pendingExport, ConnectedSystem connectedSystem)
    {
        var objectType = ObjectTypeFor(pendingExport, connectedSystem);
        if (objectType == null)
            return null;

        var classAttributeName = objectType.Tags
            .FirstOrDefault(tag => tag.Key == ObjectTypeTags.Keys.ClassMembershipAttribute)?.Value;

        if (string.IsNullOrEmpty(classAttributeName))
            return null;

        var classesBeingAdded = pendingExport.AttributeValueChanges
            .Where(change => change.Attribute != null &&
                             change.Attribute.Name.Equals(classAttributeName, StringComparison.OrdinalIgnoreCase))
            .Select(change => change.StringValue)
            .Where(value => !string.IsNullOrEmpty(value))
            .Select(value => value!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (classesBeingAdded.Count == 0)
            return null;

        // A requirement is satisfied either by a value this export writes or by one already on the object.
        var satisfied = pendingExport.AttributeValueChanges
            .Where(HasAValue)
            .Select(change => change.Attribute?.Name)
            .Concat((pendingExport.ConnectedSystemObject?.AttributeValues ?? [])
                .Select(value => value.Attribute?.Name))
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = (objectType.Attributes ?? [])
            .Where(attribute => attribute.Required)
            .Where(attribute => attribute.ClassName != null && classesBeingAdded.Contains(attribute.ClassName))
            .Where(attribute => !satisfied.Contains(attribute.Name))
            .Select(attribute => attribute.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (missing.Count == 0)
            return null;

        var classes = string.Join(", ", classesBeingAdded.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
        var message = $"Cannot add the class(es) '{classes}' to this object: '{connectedSystem.Name}' requires " +
                      $"{string.Join(", ", missing)}, and this export neither writes {(missing.Count == 1 ? "it" : "them")} " +
                      "nor finds a value already on the object. Add an Attribute Flow for the missing attribute(s), or " +
                      "withdraw the auxiliary class selection that brought the class in.";

        return ConnectedSystemExportResult.Failed(message, ConnectedSystemExportErrorType.General);
    }

    /// <summary>
    /// The Object Type as the Connected System's schema holds it, which is where the requirements and the class
    /// membership tag live. The Pending Export's own navigation carries the name but not the schema behind it.
    /// </summary>
    private static ConnectedSystemObjectType? ObjectTypeFor(PendingExport pendingExport, ConnectedSystem connectedSystem)
    {
        var typeId = pendingExport.ConnectedSystemObject?.TypeId;
        if (typeId == null)
            return null;

        return connectedSystem.ObjectTypes?.FirstOrDefault(objectType => objectType.Id == typeId);
    }

    private static bool HasAValue(PendingExportAttributeValueChange change)
    {
        // A Remove leaves nothing behind, so it cannot satisfy a requirement.
        if (change.ChangeType is PendingExportAttributeChangeType.Remove or PendingExportAttributeChangeType.RemoveAll)
            return false;

        return change.StringValue != null || change.IntValue != null || change.LongValue != null ||
               change.DecimalValue != null || change.DateTimeValue != null || change.GuidValue != null ||
               change.BoolValue != null || change.ByteValue != null ||
               change.UnresolvedReferenceValue != null || change.ResolvedReferenceCsoId != null;
    }
}
