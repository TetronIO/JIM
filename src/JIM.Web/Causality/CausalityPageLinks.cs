// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JimUtilities = JIM.Utilities.Utilities;

namespace JIM.Web.Causality;

/// <summary>
/// The item page's links (#2100): each item and object opens its own page, as the causality panel always has.
/// </summary>
public sealed class CausalityPageLinks : ICausalityLinks
{
    public static readonly CausalityPageLinks Instance = new();

    private CausalityPageLinks()
    {
    }

    /// <inheritdoc />
    public string ItemLinkText => "View activity item";

    /// <inheritdoc />
    public string ItemHref(Guid runProfileExecutionItemId) => $"/activity/item/{runProfileExecutionItemId}";

    /// <inheritdoc />
    public string? ConnectedSystemObjectHref(int connectedSystemId, Guid connectedSystemObjectId) =>
        JimUtilities.GetConnectedSystemObjectHref(connectedSystemId, connectedSystemObjectId);

    /// <inheritdoc />
    /// <remarks>
    /// Null, never a guess, where the plural name is unknown: the route is keyed on it (<c>/t/{plural}/v/{id}</c>), and
    /// the fallback this replaced once invented <c>/identity/search/{id}</c>, a route that does not exist.
    /// </remarks>
    public string? MetaverseObjectHref(Guid metaverseObjectId, string? typePluralName) =>
        !string.IsNullOrWhiteSpace(typePluralName)
            ? JimUtilities.GetMetaverseObjectHref(metaverseObjectId, typePluralName)
            : null;
}
