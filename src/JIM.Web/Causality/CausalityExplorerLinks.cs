// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Causality;

/// <summary>
/// The Causality Explorer's links (#2100): every item and object the panel names re-centres the explorer on it, carrying
/// the trail with the focus in view added, so following a chain never leaves the page and the way back is always there.
/// </summary>
/// <remarks>
/// A record, so two policies for the same focus and trail are equal: the panel rebuilds its models only when its context
/// changes, and a re-render must not rebuild what is already on screen.
/// </remarks>
public sealed record CausalityExplorerLinks(CausalityTrail Trail, CausalityFocus Current) : ICausalityLinks
{
    /// <inheritdoc />
    public string ItemLinkText => "Explore from here";

    /// <inheritdoc />
    public string ItemHref(Guid runProfileExecutionItemId) =>
        HrefFor(new CausalityFocus(CausalityFocusKind.Item, runProfileExecutionItemId));

    /// <inheritdoc />
    /// <remarks>The explorer addresses an object by its id alone; the Connected System is not needed.</remarks>
    public string? ConnectedSystemObjectHref(int connectedSystemId, Guid connectedSystemObjectId) =>
        HrefFor(new CausalityFocus(CausalityFocusKind.ConnectedSystemObject, connectedSystemObjectId));

    /// <inheritdoc />
    /// <remarks>The explorer addresses an object by its id alone, so it links one whose type was not resolved.</remarks>
    public string? MetaverseObjectHref(Guid metaverseObjectId, string? typePluralName) =>
        HrefFor(new CausalityFocus(CausalityFocusKind.MetaverseObject, metaverseObjectId));

    private string HrefFor(CausalityFocus next) => Trail.Follow(Current, next).Href(next);
}
