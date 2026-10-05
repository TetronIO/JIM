// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// A read projection of one retired values register entry (Unique Value Generation, #242, Phase 6) for the
/// portal's retired values list, a Metaverse Object's change history, the REST API and PowerShell: the entry's own
/// fields, plus what is known now about the object that held it and the Activity that retired it.
/// </summary>
public class RetiredGeneratedValueHeader
{
    public long Id { get; set; }

    /// <summary>The Metaverse attribute, for an import flow; null for an export flow.</summary>
    public int? MetaverseAttributeId { get; set; }

    /// <summary>The Connected System Object Type attribute, for an export flow; null for an import flow.</summary>
    public int? ConnectedSystemObjectTypeAttributeId { get; set; }

    /// <summary>The attribute's display name.</summary>
    public string AttributeName { get; set; } = string.Empty;

    /// <summary>The value as it was issued.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>When it was retired (UTC).</summary>
    public DateTime RetiredAt { get; set; }

    /// <summary>Why it was retired.</summary>
    public RetiredGeneratedValueReason Reason { get; set; }

    /// <summary>The object that held it: a Metaverse Object id (import) or Connected System Object id (export).</summary>
    public Guid? FromObjectId { get; set; }

    /// <summary>The holder's display name as captured at retirement.</summary>
    public string? FromObjectDisplayName { get; set; }

    /// <summary>Whether the object that held the value still exists, so a surface can link to it.</summary>
    public bool FromObjectExists { get; set; }

    /// <summary>The holder's object type name, while it exists (a Metaverse Object Type or Connected System Object Type).</summary>
    public string? FromObjectTypeName { get; set; }

    /// <summary>
    /// The holder's Metaverse Object Type plural name, while it exists: what a link to a Metaverse Object is built
    /// from. Null for an export flow's value, whose holder is a Connected System Object.
    /// </summary>
    public string? FromObjectTypePluralName { get; set; }

    /// <summary>For an export flow's value, the Connected System the holder belongs to, while it exists.</summary>
    public int? FromObjectConnectedSystemId { get; set; }

    /// <summary>The Activity during which the value was retired, when one was recorded.</summary>
    public Guid? ActivityId { get; set; }

    /// <summary>That Activity's target name (for a synchronisation, its Run Profile), while the Activity exists.</summary>
    public string? ActivityTargetName { get; set; }

    /// <summary>That Activity's target context (for a synchronisation, its Connected System), while the Activity exists.</summary>
    public string? ActivityTargetContext { get; set; }
}
