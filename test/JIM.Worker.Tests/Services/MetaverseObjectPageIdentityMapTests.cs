// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Models.Core;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Services;

/// <summary>
/// Pins the resolution semantics <see cref="MetaverseObjectPageIdentityMap"/> provides to the sync
/// processors (#1612): every load of a given Metaverse Object row, within one sync page, must resolve
/// onto a single canonical CLR instance, so that in-memory state set on one load (deletion markers,
/// pending attribute-value lists) is visible to code that later loads the same row a different way.
/// </summary>
[TestFixture]
public class MetaverseObjectPageIdentityMapTests
{
    [Test]
    public void Resolve_FirstSightOfAnId_RegistersAndReturnsTheSameInstance()
    {
        var map = new MetaverseObjectPageIdentityMap();
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };

        var resolved = map.Resolve(mvo);

        Assert.That(resolved, Is.SameAs(mvo));
    }

    [Test]
    public void Resolve_SecondDistinctInstanceOfSameId_ReturnsTheFirstInstance()
    {
        var map = new MetaverseObjectPageIdentityMap();
        var id = Guid.NewGuid();
        var first = new MetaverseObject { Id = id, CachedDisplayName = "First load" };
        var second = new MetaverseObject { Id = id, CachedDisplayName = "Second load" };

        map.Resolve(first);
        var resolved = map.Resolve(second);

        Assert.That(resolved, Is.SameAs(first),
            "a later load of the same Id must be absorbed into the canonical (first-seen) instance");
        Assert.That(resolved.CachedDisplayName, Is.EqualTo("First load"));
    }

    [Test]
    public void Resolve_SecondDistinctInstanceOfSameId_IncrementsAbsorbedCount()
    {
        var map = new MetaverseObjectPageIdentityMap();
        var id = Guid.NewGuid();

        map.Resolve(new MetaverseObject { Id = id });
        Assert.That(map.AbsorbedCount, Is.Zero, "first sight of an Id is a registration, not an absorption");

        map.Resolve(new MetaverseObject { Id = id });
        Assert.That(map.AbsorbedCount, Is.EqualTo(1));

        map.Resolve(new MetaverseObject { Id = id });
        Assert.That(map.AbsorbedCount, Is.EqualTo(2), "AbsorbedCount accumulates across multiple collisions");
    }

    [Test]
    public void Resolve_SameInstanceResolvedTwice_DoesNotCountAsAnAbsorption()
    {
        var map = new MetaverseObjectPageIdentityMap();
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };

        map.Resolve(mvo);
        var resolved = map.Resolve(mvo);

        Assert.That(resolved, Is.SameAs(mvo));
        Assert.That(map.AbsorbedCount, Is.Zero, "resolving the already-canonical instance is not a collision");
    }

    [Test]
    public void Resolve_GuidEmpty_PassesThroughUnregistered()
    {
        var map = new MetaverseObjectPageIdentityMap();
        var firstProjection = new MetaverseObject { Id = Guid.Empty, CachedDisplayName = "Projection A" };
        var secondProjection = new MetaverseObject { Id = Guid.Empty, CachedDisplayName = "Projection B" };

        var resolvedFirst = map.Resolve(firstProjection);
        var resolvedSecond = map.Resolve(secondProjection);

        Assert.That(resolvedFirst, Is.SameAs(firstProjection));
        Assert.That(resolvedSecond, Is.SameAs(secondProjection),
            "two distinct not-yet-persisted projections must never collide on the shared empty Id");
        Assert.That(map.AbsorbedCount, Is.Zero);
    }

    [Test]
    public void Seed_CsoWithJoinedMvoNotYetRegistered_RegistersAndLeavesNavigationUnchanged()
    {
        var map = new MetaverseObjectPageIdentityMap();
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), MetaverseObject = mvo };

        map.Seed([cso]);

        Assert.That(cso.MetaverseObject, Is.SameAs(mvo));
        Assert.That(map.AbsorbedCount, Is.Zero);
    }

    [Test]
    public void Seed_CsoWithJoinedMvoAlreadyRegisteredUnderADistinctInstance_RewritesNavigationToCanonical()
    {
        var map = new MetaverseObjectPageIdentityMap();
        var id = Guid.NewGuid();
        var canonical = new MetaverseObject { Id = id, CachedDisplayName = "Canonical" };
        map.Resolve(canonical);

        var distinctLoad = new MetaverseObject { Id = id, CachedDisplayName = "Distinct load" };
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), MetaverseObject = distinctLoad };

        map.Seed([cso]);

        Assert.That(cso.MetaverseObject, Is.SameAs(canonical),
            "Seed must rewrite the CSO's navigation onto the page's canonical instance");
        Assert.That(map.AbsorbedCount, Is.EqualTo(1));
    }

    [Test]
    public void Seed_CsoWithNoJoinedMvo_IsLeftUntouched()
    {
        var map = new MetaverseObjectPageIdentityMap();
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), MetaverseObject = null };

        Assert.That(() => map.Seed([cso]), Throws.Nothing);
        Assert.That(cso.MetaverseObject, Is.Null);
    }

    [Test]
    public void Register_PersistedMvoWithRealId_MakesItTheCanonicalInstanceForLaterLoads()
    {
        var map = new MetaverseObjectPageIdentityMap();
        var id = Guid.NewGuid();
        var persisted = new MetaverseObject { Id = id, CachedDisplayName = "Newly created" };

        map.Register(persisted);
        var laterLoad = new MetaverseObject { Id = id, CachedDisplayName = "Later load" };
        var resolved = map.Resolve(laterLoad);

        Assert.That(resolved, Is.SameAs(persisted));
        Assert.That(map.AbsorbedCount, Is.EqualTo(1));
    }

    [Test]
    public void Register_GuidEmpty_IsANoOp()
    {
        var map = new MetaverseObjectPageIdentityMap();
        var notYetPersisted = new MetaverseObject { Id = Guid.Empty };

        Assert.That(() => map.Register(notYetPersisted), Throws.Nothing);

        // A later real load under Guid.Empty must not collide with the unregistered instance above.
        var separateProjection = new MetaverseObject { Id = Guid.Empty };
        var resolved = map.Resolve(separateProjection);
        Assert.That(resolved, Is.SameAs(separateProjection));
    }

    [Test]
    public void Clear_RegisteredInstance_NoLongerResolvesAsCanonical()
    {
        var map = new MetaverseObjectPageIdentityMap();
        var id = Guid.NewGuid();
        var beforeClear = new MetaverseObject { Id = id };
        map.Resolve(beforeClear);

        map.Clear();

        var afterClear = new MetaverseObject { Id = id };
        var resolved = map.Resolve(afterClear);

        Assert.That(resolved, Is.SameAs(afterClear),
            "after Clear, a fresh load of a previously-seen Id must register anew rather than resolving " +
            "onto an instance from the cleared (previous) page");
    }

    [Test]
    public void Clear_DoesNotResetAbsorbedCount()
    {
        // AbsorbedCount is a cumulative, whole-run tripwire (one map per processor, one processor per
        // run): Clear() runs at every page boundary, but a caller inspecting AbsorbedCount after the run
        // completes must still be able to tell whether ANY page ever saw a same-page identity split.
        var map = new MetaverseObjectPageIdentityMap();
        var id = Guid.NewGuid();
        map.Resolve(new MetaverseObject { Id = id });
        map.Resolve(new MetaverseObject { Id = id });
        Assert.That(map.AbsorbedCount, Is.EqualTo(1));

        map.Clear();

        Assert.That(map.AbsorbedCount, Is.EqualTo(1), "Clear must not reset the cumulative absorption count");
    }
}
