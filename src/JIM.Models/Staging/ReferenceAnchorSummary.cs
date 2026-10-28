// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// What a reference to a Connected System Object is written as: the object's id, and the anchor a
/// target system is handed for it. Summary-tier: export execution resolves group memberships against
/// hundreds of thousands of these at scale, so it carries the anchor and nothing else of the object.
/// </summary>
/// <param name="ConnectedSystemObjectId">The referenced Connected System Object.</param>
/// <param name="AnchorValue">The anchor the target system receives (the secondary external ID, such as
/// a DN, where the object has one, otherwise the primary); null while the object has no anchor value
/// yet, which callers must treat as "not resolvable yet", never as an empty anchor (#1398).</param>
/// <param name="IsSecondaryExternalId">Whether <paramref name="AnchorValue"/> came from the secondary
/// external ID.</param>
public sealed record ReferenceAnchorSummary(Guid ConnectedSystemObjectId, string? AnchorValue, bool IsSecondaryExternalId)
{
    /// <summary>
    /// The summary of a Connected System Object already in hand, choosing its anchor the way the
    /// repository's projection does: the secondary external ID's value where the object holds one,
    /// otherwise the primary's.
    /// </summary>
    public static ReferenceAnchorSummary Of(ConnectedSystemObject connectedSystemObject)
    {
        var secondary = connectedSystemObject.AttributeValues.FirstOrDefault(av => av.Attribute?.IsSecondaryExternalId == true);
        var anchor = secondary ?? connectedSystemObject.AttributeValues.FirstOrDefault(av => av.Attribute?.IsExternalId == true);
        return new ReferenceAnchorSummary(connectedSystemObject.Id, anchor?.ToReferenceValueString(), secondary != null);
    }
}
