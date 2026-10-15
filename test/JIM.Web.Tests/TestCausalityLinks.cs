// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Web.Causality;

namespace JIM.Web.Tests;

/// <summary>
/// A link policy whose addresses no real page writes, so a test can tell every address that came through the policy
/// from one a builder or a card still formats for itself.
/// </summary>
internal sealed class TestCausalityLinks : ICausalityLinks
{
    public static readonly TestCausalityLinks Instance = new();

    public string ItemLinkText => "Follow";

    public string ItemHref(Guid runProfileExecutionItemId) => $"test:item:{runProfileExecutionItemId}";

    public string? ConnectedSystemObjectHref(int connectedSystemId, Guid connectedSystemObjectId) =>
        $"test:cso:{connectedSystemId}:{connectedSystemObjectId}";

    // Deliberately ignores the plural name: an address that needs none must not be withheld for the want of one.
    public string? MetaverseObjectHref(Guid metaverseObjectId, string? typePluralName) => $"test:mvo:{metaverseObjectId}";
}
