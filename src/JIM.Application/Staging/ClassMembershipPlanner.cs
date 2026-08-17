// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Application.Staging;

/// <summary>
/// Works out what an object's class membership attribute must say for a given export, i.e. the <c>objectClass</c>
/// value on an RFC 4512 directory.
/// </summary>
/// <remarks>
/// JIM owns this attribute rather than an administrator flowing it, because its value is not a property of the
/// configuration but of the individual object: which auxiliary classes an object belongs to follows from which of
/// the merged classes' attributes actually have values on it, and that differs object by object. A flow can only
/// state one answer for the whole population.
/// <para>
/// Nothing here is directory-specific beyond the vocabulary. A Connected System that does not declare a class
/// membership attribute (<see cref="ObjectTypeTags.Keys.ClassMembershipAttribute"/>) gets an empty plan, so SQL,
/// CSV and SCIM systems are untouched.
/// </para>
/// </remarks>
public static class ClassMembershipPlanner
{
    /// <summary>
    /// Plans the class membership for one export.
    /// </summary>
    /// <param name="objectType">The Object Type being exported, with its tags, merged attributes, the auxiliary
    /// classes an administrator selected, and any structural carrier.</param>
    /// <param name="currentClasses">The classes the object already carries at the Connected System. Empty on a create.</param>
    /// <param name="attributesBeingWritten">The names of the attributes this export writes a value for.</param>
    /// <param name="isCreate">Whether the object is being created, which is what decides between stating the whole
    /// membership and naming only the additions to it.</param>
    /// <param name="attributesAlreadyOnTheObject">The names of the attributes that already have a value at the
    /// Connected System, so a requirement this export does not write can still be satisfied.</param>
    public static ClassMembershipPlan Plan(
        ConnectedSystemObjectType objectType,
        IEnumerable<string> currentClasses,
        IEnumerable<string> attributesBeingWritten,
        bool isCreate,
        IEnumerable<string>? attributesAlreadyOnTheObject = null)
    {
        var attributeName = objectType.Tags
            .FirstOrDefault(tag => tag.Key == ObjectTypeTags.Keys.ClassMembershipAttribute)?.Value;

        if (string.IsNullOrEmpty(attributeName))
            return new ClassMembershipPlan();

        var plan = new ClassMembershipPlan { AttributeName = attributeName };
        var written = new HashSet<string>(attributesBeingWritten, StringComparer.OrdinalIgnoreCase);
        var present = new HashSet<string>(currentClasses, StringComparer.OrdinalIgnoreCase);

        var wanted = ClassesThisObjectBelongsTo(objectType, written);

        // A create states the object's whole membership; an update names only what is being added, because
        // restating a class the object already carries is a change the Connected System has no reason to accept.
        plan.ClassesToWrite = isCreate
            ? wanted
            : wanted.Where(className => !present.Contains(className)).ToList();

        plan.MissingRequiredAttributes = RequiredAttributesNotSatisfied(
            objectType, plan.ClassesToWrite, written,
            new HashSet<string>(attributesAlreadyOnTheObject ?? [], StringComparer.OrdinalIgnoreCase));

        return plan;
    }

    /// <summary>
    /// The classes this object belongs to, most fundamental first.
    /// </summary>
    private static List<string> ClassesThisObjectBelongsTo(ConnectedSystemObjectType objectType, HashSet<string> attributesBeingWritten)
    {
        var classes = new List<string>();

        // An object identified by an auxiliary class still has to exist as something: an RFC 4512 entry has exactly
        // one structural class, so the carrier an administrator named comes first, and the object type's own class
        // is then one of the auxiliary classes attached to it.
        if (objectType.IsAuxiliary() && objectType.StructuralCarrierObjectType != null)
            classes.Add(objectType.StructuralCarrierObjectType.Name);

        classes.Add(objectType.Name);

        // Selecting an auxiliary class makes its attributes available; it does not make every object one. An object
        // belongs to the class once it actually carries one of the class's attributes, and claiming otherwise would
        // oblige it to satisfy that class's requirements for nothing.
        var contributed = objectType.Attributes ?? [];
        var auxiliaryClasses = objectType.Extensions
            .Select(extension => extension.ExtensionObjectType?.Name)
            .OfType<string>()
            .Where(className => contributed.Any(attribute =>
                string.Equals(attribute.ClassName, className, StringComparison.OrdinalIgnoreCase) &&
                attributesBeingWritten.Contains(attribute.Name)));

        // Except discards a class already listed, which an extension naming the carrier or the object type's own
        // class would be, and discards duplicates among the extensions themselves.
        classes.AddRange(auxiliaryClasses.Except(classes, StringComparer.OrdinalIgnoreCase).ToList());

        return classes;
    }

    /// <summary>
    /// The attributes the Connected System requires for the classes being added, that this export neither writes nor
    /// finds already on the object.
    /// </summary>
    /// <remarks>
    /// Only the classes being added are checked. A class the object already carries is between it and the Connected
    /// System; refusing an unrelated change over one would block work an administrator cannot act on.
    /// </remarks>
    private static List<string> RequiredAttributesNotSatisfied(
        ConnectedSystemObjectType objectType,
        List<string> classesBeingAdded,
        HashSet<string> attributesBeingWritten,
        HashSet<string> attributesAlreadyOnTheObject)
    {
        if (classesBeingAdded.Count == 0)
            return [];

        var adding = new HashSet<string>(classesBeingAdded, StringComparer.OrdinalIgnoreCase);

        return (objectType.Attributes ?? [])
            .Where(attribute => attribute.Required)
            .Where(attribute => attribute.ClassName != null && adding.Contains(attribute.ClassName))
            .Where(attribute => !attributesBeingWritten.Contains(attribute.Name))
            .Where(attribute => !attributesAlreadyOnTheObject.Contains(attribute.Name))
            .Select(attribute => attribute.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
