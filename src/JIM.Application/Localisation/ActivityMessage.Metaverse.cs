// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Resources;
using JIM.Models.Core.DTOs;
using JIM.Models.Localisation;

namespace JIM.Application.Localisation;

/// <summary>
/// What removing or unassigning a Metaverse Attribute, and removing a Metaverse Object Type, say on the Activities that
/// record each removal (#3039).
/// </summary>
public static partial class ActivityMessage
{
    /// <summary>
    /// What a reference is called, as an argument of a longer message: the message that describes it, else the English it
    /// was described with.
    /// </summary>
    public static MessageArgument DescribedReference(AttributeReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return reference.DescriptionDetail is { } detail ? detail : reference.Description;
    }

    /// <summary>The child Activity for a reference removed with a Metaverse Attribute.</summary>
    public static JimMessage MetaverseAttributeReferenceRemoved(AttributeReference reference, string attributeName) =>
        Create(nameof(ActivityMessages.MetaverseAttributeReferenceRemoved), DescribedReference(reference), attributeName);

    /// <summary>The child Activity for a Metaverse Attribute's removal.</summary>
    public static JimMessage MetaverseAttributeRemoved(string attributeName) =>
        Create(nameof(ActivityMessages.MetaverseAttributeRemoved), attributeName);

    /// <summary>The Activity for a Metaverse Attribute unassigned from a Metaverse Object Type.</summary>
    public static JimMessage MetaverseAttributeUnassigned(string attributeName, string objectTypeName) =>
        Create(nameof(ActivityMessages.MetaverseAttributeUnassigned), attributeName, objectTypeName);

    /// <summary>The child Activity for something removed with a Metaverse Object Type or an attribute's assignment to one.</summary>
    public static JimMessage MetaverseReferenceRemovedScoped(MessageArgument reference, string objectTypeName) =>
        Create(nameof(ActivityMessages.MetaverseReferenceRemovedScoped), reference, objectTypeName);

    /// <summary>The child Activity for a Metaverse Object Type's removal.</summary>
    public static JimMessage MetaverseObjectTypeRemoved(string objectTypeName) =>
        Create(nameof(ActivityMessages.MetaverseObjectTypeRemoved), objectTypeName);
}
