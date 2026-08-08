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
