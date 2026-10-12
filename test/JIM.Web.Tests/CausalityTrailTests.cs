// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Linq;
using JIM.Web.Causality;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Causality Explorer's trail (#2100): the foci that led to the one in view, oldest first, carried in the page
/// address. It is read from whatever an address holds, so it must never throw on a malformed one; it stays at most
/// <see cref="CausalityTrail.MaxFoci"/> long; and following a focus already on it returns there rather than repeating it,
/// as browser history does.
/// </summary>
[TestFixture]
public class CausalityTrailTests
{
    private static readonly CausalityFocus A = Item("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly CausalityFocus B = new(CausalityFocusKind.ConnectedSystemObject, Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"));
    private static readonly CausalityFocus C = new(CausalityFocusKind.MetaverseObject, Guid.Parse("cccccccc-0000-0000-0000-000000000003"));
    private static readonly CausalityFocus D = Item("dddddddd-0000-0000-0000-000000000004");

    // ─── Reading an address ───

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void Parse_NothingGiven_IsEmpty(string? value)
    {
        Assert.That(CausalityTrail.Parse(value, D).Foci, Is.Empty);
    }

    [Test]
    public void Parse_EachKindOfToken_ReadsTheFociOldestFirst()
    {
        var trail = CausalityTrail.Parse($"{A.Token},{B.Token},{C.Token}", D);

        Assert.That(trail.Foci, Is.EqualTo(new[] { A, B, C }));
    }

    /// <summary>
    /// An address is typed, pasted and truncated by people; a token the explorer cannot read is dropped and the rest are
    /// kept, rather than the page failing.
    /// </summary>
    [Test]
    public void Parse_MalformedTokens_AreDroppedAndTheRestKept()
    {
        var trail = CausalityTrail.Parse($"i:not-a-guid,{A.Token},x:{Guid.NewGuid()},c:, ,{B.Token},m{Guid.NewGuid()},{C.Token}", D);

        Assert.That(trail.Foci, Is.EqualTo(new[] { A, B, C }));
    }

    [Test]
    public void Parse_LongerThanTheTrailHolds_KeepsTheMostRecent()
    {
        var foci = Enumerable.Range(1, 15).Select(i => Item($"00000000-0000-0000-0000-{i:D12}")).ToList();

        var trail = CausalityTrail.Parse(string.Join(",", foci.Select(f => f.Token)), D);

        Assert.That(trail.Foci, Is.EqualTo(foci.TakeLast(CausalityTrail.MaxFoci - 1)),
            "the focus in view is the trail's last place, so the address holds one fewer");
    }

    /// <summary>
    /// The focus in view is never also listed behind itself: an address that names it in its own trail is read as having
    /// returned to it.
    /// </summary>
    [Test]
    public void Parse_TrailNamingTheFocusInView_EndsBeforeIt()
    {
        var trail = CausalityTrail.Parse($"{A.Token},{D.Token},{B.Token}", D);

        Assert.That(trail.Foci, Is.EqualTo(new[] { A }));
    }

    // ─── Following ───

    [Test]
    public void Follow_SomethingNew_AddsTheFocusInView()
    {
        var trail = CausalityTrail.Parse($"{A.Token},{B.Token}", C);

        Assert.That(trail.Follow(C, D).Foci, Is.EqualTo(new[] { A, B, C }));
    }

    /// <summary>
    /// Following a focus already on the trail returns to it: what came after it is dropped, as browser history drops the
    /// pages ahead of one navigated back to.
    /// </summary>
    [Test]
    public void Follow_AFocusOnTheTrail_ReturnsToItAndDropsWhatFollowedIt()
    {
        var trail = CausalityTrail.Parse($"{A.Token},{B.Token},{C.Token}", D);

        Assert.That(trail.Follow(D, B).Foci, Is.EqualTo(new[] { A }));
    }

    [Test]
    public void Follow_TheFocusInView_KeepsTheTrailAsItIs()
    {
        var trail = CausalityTrail.Parse($"{A.Token},{B.Token}", C);

        Assert.That(trail.Follow(C, C).Foci, Is.EqualTo(new[] { A, B }));
    }

    [Test]
    public void Follow_WithTheTrailFull_DropsTheOldest()
    {
        var foci = Enumerable.Range(1, CausalityTrail.MaxFoci - 1).Select(i => Item($"00000000-0000-0000-0000-{i:D12}")).ToList();
        var trail = CausalityTrail.Parse(string.Join(",", foci.Select(f => f.Token)), D);

        var followed = trail.Follow(D, A);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(followed.Foci, Has.Count.EqualTo(CausalityTrail.MaxFoci - 1));
            Assert.That(followed.Foci[0], Is.EqualTo(foci[1]), "the oldest is dropped");
            Assert.That(followed.Foci[^1], Is.EqualTo(D));
        }
    }

    // ─── Writing an address ───

    [Test]
    public void Serialise_RoundTrips()
    {
        var trail = CausalityTrail.Parse($"{A.Token},{B.Token},{C.Token}", D);

        Assert.That(CausalityTrail.Parse(trail.Serialise(), D).Foci, Is.EqualTo(trail.Foci));
    }

    [Test]
    public void Serialise_Empty_IsNull()
    {
        Assert.That(CausalityTrail.Empty.Serialise(), Is.Null);
    }

    [Test]
    public void Href_EachKindOfFocus_IsItsExplorerAddressCarryingTheTrail()
    {
        var trail = CausalityTrail.Parse($"{A.Token}", D);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(trail.Href(D), Is.EqualTo($"/causality/items/{D.Id}?trail={A.Token}"));
            Assert.That(trail.Href(B), Is.EqualTo($"/causality/connected-system-objects/{B.Id}?trail={A.Token}"));
            Assert.That(trail.Href(C), Is.EqualTo($"/causality/metaverse-objects/{C.Id}?trail={A.Token}"));
            Assert.That(CausalityTrail.Empty.Href(D), Is.EqualTo($"/causality/items/{D.Id}"), "no trail, no query");
        }
    }

    /// <summary>
    /// Each focus on the trail links back to itself as it stood: with the trail that led to it, not the longer one.
    /// </summary>
    [Test]
    public void CrumbHref_ReturnsToThatFocusWithTheTrailThatLedToIt()
    {
        var trail = CausalityTrail.Parse($"{A.Token},{B.Token},{C.Token}", D);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(trail.CrumbHref(0), Is.EqualTo($"/causality/items/{A.Id}"));
            Assert.That(trail.CrumbHref(2), Is.EqualTo($"/causality/metaverse-objects/{C.Id}?trail={A.Token},{B.Token}"));
        }
    }

    [Test]
    public void Equality_SameFoci_AreEqual()
    {
        Assert.That(CausalityTrail.Parse($"{A.Token},{B.Token}", D), Is.EqualTo(CausalityTrail.Parse($"{A.Token},{B.Token}", D)));
    }

    private static CausalityFocus Item(string id) => new(CausalityFocusKind.Item, Guid.Parse(id));
}
