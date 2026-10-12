// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Web.Causality;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Causality Explorer's link policy (#2100): every item and object the Lineage names re-centres the explorer on it,
/// carrying the trail with the focus in view added, so following a chain never leaves the page and the way back is
/// always there.
/// </summary>
[TestFixture]
public class CausalityExplorerLinksTests
{
    private static readonly CausalityFocus Earlier = new(CausalityFocusKind.Item, Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"));
    private static readonly CausalityFocus InView = new(CausalityFocusKind.Item, Guid.Parse("dddddddd-0000-0000-0000-000000000004"));

    private static CausalityExplorerLinks Links() =>
        new(CausalityTrail.Parse(Earlier.Token, InView), InView);

    [Test]
    public void ItemHref_ReCentresOnTheItemWithTheFocusInViewAddedToTheTrail()
    {
        var itemId = Guid.NewGuid();

        Assert.That(Links().ItemHref(itemId), Is.EqualTo($"/causality/items/{itemId}?trail={Earlier.Token},{InView.Token}"));
    }

    [Test]
    public void ConnectedSystemObjectHref_ReCentresOnTheObject()
    {
        var csoId = Guid.NewGuid();

        Assert.That(Links().ConnectedSystemObjectHref(2, csoId),
            Is.EqualTo($"/causality/connected-system-objects/{csoId}?trail={Earlier.Token},{InView.Token}"));
    }

    /// <summary>
    /// The explorer addresses a Metaverse Object by its id alone, so it links one whose type the page could not resolve.
    /// </summary>
    [Test]
    public void MetaverseObjectHref_NeedsNoTypePluralName()
    {
        var mvoId = Guid.NewGuid();

        Assert.That(Links().MetaverseObjectHref(mvoId, typePluralName: null),
            Is.EqualTo($"/causality/metaverse-objects/{mvoId}?trail={Earlier.Token},{InView.Token}"));
    }

    [Test]
    public void ItemLinkText_SaysWhatFollowingItDoes()
    {
        Assert.That(Links().ItemLinkText, Is.EqualTo("Explore from here"));
    }

    /// <summary>
    /// The causality panel rebuilds its models only when its context changes; two policies for the same focus and trail
    /// are equal, so a re-render does not rebuild what is already on screen.
    /// </summary>
    [Test]
    public void Equality_SameFocusAndTrail_AreEqual()
    {
        Assert.That(Links(), Is.EqualTo(Links()));
    }
}
