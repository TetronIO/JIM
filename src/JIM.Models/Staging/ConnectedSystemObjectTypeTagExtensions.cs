// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// Reads JIM's own classifications off a Connected System Object Type's tags.
/// </summary>
/// <remarks>
/// Every surface that hides internal object types (the portal's schema screen, the PowerShell cmdlet) asks the same
/// question, so it is answered in one place rather than by each of them matching key and value strings for itself.
/// </remarks>
public static class ConnectedSystemObjectTypeTagExtensions
{
    /// <summary>
    /// Whether the Connected System classified this object type as an auxiliary class: one that augments another
    /// rather than standing alone.
    /// </summary>
    /// <remarks>
    /// An object type carrying no class-kind tag is unclassified, and is deliberately not auxiliary. Guessing would
    /// put a structural class in front of an administrator as something to attach to their objects.
    /// </remarks>
    public static bool IsAuxiliary(this ConnectedSystemObjectType objectType)
    {
        return objectType.Tags.Any(tag =>
            tag.Key == ObjectTypeTags.Keys.ClassKind &&
            tag.Value == ObjectTypeTags.Values.ClassKindAuxiliary);
    }

    /// <summary>
    /// Whether the Connected System classified this object type as a structural class: one an object can be, rather
    /// than one it merely carries.
    /// </summary>
    /// <remarks>
    /// The strict reading of the tag, so an unclassified type is not structural either. Callers choosing between
    /// classes want the ones the Connected System actually vouched for; <see cref="IsAuxiliary"/> negated would
    /// offer abstract and unclassified types alongside them.
    /// </remarks>
    public static bool IsStructural(this ConnectedSystemObjectType objectType)
    {
        return objectType.Tags.Any(tag =>
            tag.Key == ObjectTypeTags.Keys.ClassKind &&
            tag.Value == ObjectTypeTags.Values.ClassKindStructural);
    }

    /// <summary>
    /// The name of the attribute carrying this object type's class membership, or null when the Connected System
    /// does not have the concept.
    /// </summary>
    /// <remarks>
    /// Its presence is what says JIM computes class membership for this object type rather than an administrator
    /// flowing it, so it also answers "does merging auxiliary classes mean anything here?" for the surfaces that
    /// offer it. See <see cref="ObjectTypeTags.Keys.ClassMembershipAttribute"/>.
    /// </remarks>
    public static string? ClassMembershipAttributeName(this ConnectedSystemObjectType objectType)
    {
        var name = objectType.Tags
            .FirstOrDefault(tag => tag.Key == ObjectTypeTags.Keys.ClassMembershipAttribute)?.Value;

        return string.IsNullOrEmpty(name) ? null : name;
    }

    /// <summary>
    /// Whether the Connected System hands this object type's class membership to JIM to compute.
    /// </summary>
    public static bool ManagesClassMembership(this ConnectedSystemObjectType objectType)
    {
        return objectType.ClassMembershipAttributeName() != null;
    }

    /// <summary>
    /// Whether the Connected System reported this object type as one it uses for its own configuration or operation,
    /// rather than one an administrator would manage.
    /// </summary>
    /// <remarks>
    /// An object type carrying no visibility tag is not internal. That is the classification contract: an untagged
    /// type is unclassified, and unclassified always means "show it".
    /// </remarks>
    public static bool IsInternal(this ConnectedSystemObjectType objectType)
    {
        return IsInternal(objectType.Tags.Select(tag => (tag.Key, tag.Value)));
    }

    /// <summary>
    /// Whether a set of classification tags marks its object type internal. Takes the tags as key/value pairs so
    /// that a caller holding a projection of them, rather than the entity, can ask the same question.
    /// </summary>
    public static bool IsInternal(IEnumerable<(string Key, string Value)> tags)
    {
        return tags.Any(tag =>
            tag.Key == ObjectTypeTags.Keys.Visibility &&
            tag.Value == ObjectTypeTags.Values.VisibilityInternal);
    }
}
