// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Causality;

/// <summary>
/// Where the causality panel's links go (#2100): every address it writes for an execution item, a Connected System
/// Object or a Metaverse Object, and what an item link says.
/// </summary>
/// <remarks>
/// <para>
/// One Lineage, two hosts. On the item page (<see cref="CausalityPageLinks"/>) a link opens the item's or the object's
/// own page, exactly as it always has. In the Causality Explorer the same links re-centre the explorer instead,
/// carrying the trail, so following a chain never leaves the page. The builders and cards ask this policy rather than
/// formatting an address themselves, which is what lets one rendering serve both.
/// </para>
/// <para>
/// Every other address (a Connected System, a Synchronisation Rule, a Pending Export, a deletion record) is the same on
/// both hosts, and is not the policy's to decide.
/// </para>
/// </remarks>
public interface ICausalityLinks
{
    /// <summary>
    /// What a link to an execution item says.
    /// </summary>
    string ItemLinkText { get; }

    /// <summary>
    /// The address an execution item's link opens.
    /// </summary>
    string ItemHref(Guid runProfileExecutionItemId);

    /// <summary>
    /// The address a Connected System Object's link opens, or null where it cannot be addressed.
    /// </summary>
    string? ConnectedSystemObjectHref(int connectedSystemId, Guid connectedSystemObjectId);

    /// <summary>
    /// The address a Metaverse Object's link opens, or null where it cannot be addressed. The item page's address is
    /// keyed on the object's type's plural name, which the page may not have resolved; an address that does not need
    /// it ignores it.
    /// </summary>
    string? MetaverseObjectHref(Guid metaverseObjectId, string? typePluralName);
}
