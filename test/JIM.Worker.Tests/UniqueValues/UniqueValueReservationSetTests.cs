// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.UniqueValues;
using NUnit.Framework;

namespace JIM.Worker.Tests.UniqueValues;

/// <summary>
/// <see cref="UniqueValueReservationSet"/>: the process-wide, thread-safe reservation set every generation
/// round and adoption checks first (Unique Value Generation, #242).
/// </summary>
[TestFixture]
public class UniqueValueReservationSetTests
{
    [Test]
    public void TryReserve_Unclaimed_SucceedsAndClaimsIt()
    {
        var set = new UniqueValueReservationSet();
        var owner = Guid.NewGuid();

        var result = set.TryReserve(owner, UniqueValueScope.MetaverseAttribute, 1, "joe.bloggs");

        Assert.That(result, Is.True);
    }

    [Test]
    public void TryReserve_AlreadyClaimedByAnyOwner_Fails()
    {
        var set = new UniqueValueReservationSet();
        var ownerA = Guid.NewGuid();
        var ownerB = Guid.NewGuid();

        set.TryReserve(ownerA, UniqueValueScope.MetaverseAttribute, 1, "joe.bloggs");
        var second = set.TryReserve(ownerB, UniqueValueScope.MetaverseAttribute, 1, "joe.bloggs");

        Assert.That(second, Is.False, "a value claimed once must never be claimed twice, regardless of who holds it");
    }

    [Test]
    public void TryReserve_DifferentScopeSameAttributeIdAndValue_AreIndependent()
    {
        var set = new UniqueValueReservationSet();
        var owner = Guid.NewGuid();

        set.TryReserve(owner, UniqueValueScope.MetaverseAttribute, 1, "joe.bloggs");
        var connectedSystemResult = set.TryReserve(owner, UniqueValueScope.ConnectedSystemAttribute, 1, "joe.bloggs");

        Assert.That(connectedSystemResult, Is.True, "Metaverse and Connected System reservations for the same attribute id are different value spaces");
    }

    [Test]
    public void IsReservedByAnotherOwner_ClaimedByAnotherOwner_ReturnsTrue()
    {
        var set = new UniqueValueReservationSet();
        var ownerA = Guid.NewGuid();
        var ownerB = Guid.NewGuid();
        set.TryReserve(ownerA, UniqueValueScope.MetaverseAttribute, 1, "joe.bloggs");

        Assert.That(set.IsReservedByAnotherOwner(ownerB, UniqueValueScope.MetaverseAttribute, 1, "joe.bloggs"), Is.True);
    }

    [Test]
    public void IsReservedByAnotherOwner_ClaimedBySameOwner_ReturnsFalse()
    {
        var set = new UniqueValueReservationSet();
        var owner = Guid.NewGuid();
        set.TryReserve(owner, UniqueValueScope.MetaverseAttribute, 1, "joe.bloggs");

        Assert.That(set.IsReservedByAnotherOwner(owner, UniqueValueScope.MetaverseAttribute, 1, "joe.bloggs"), Is.False);
    }

    [Test]
    public void IsReservedByAnotherOwner_Unclaimed_ReturnsFalse()
    {
        var set = new UniqueValueReservationSet();
        Assert.That(set.IsReservedByAnotherOwner(Guid.NewGuid(), UniqueValueScope.MetaverseAttribute, 1, "joe.bloggs"), Is.False);
    }

    [Test]
    public void TryReserve_ParallelContentionOnTheSameKey_SucceedsExactlyOnce()
    {
        var set = new UniqueValueReservationSet();
        var successCount = 0;

        Parallel.For(0, 100, _ =>
        {
            if (set.TryReserve(Guid.NewGuid(), UniqueValueScope.MetaverseAttribute, 1, "joe.bloggs"))
                Interlocked.Increment(ref successCount);
        });

        Assert.That(successCount, Is.EqualTo(1));
    }

    [Test]
    public void ReleaseAll_ReleasesOnlyThatOwnersClaims()
    {
        var set = new UniqueValueReservationSet();
        var ownerA = Guid.NewGuid();
        var ownerB = Guid.NewGuid();
        set.TryReserve(ownerA, UniqueValueScope.MetaverseAttribute, 1, "joe.bloggs");
        set.TryReserve(ownerB, UniqueValueScope.MetaverseAttribute, 1, "jane.doe");

        set.ReleaseAll(ownerA);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(set.TryReserve(Guid.NewGuid(), UniqueValueScope.MetaverseAttribute, 1, "joe.bloggs"), Is.True, "ownerA's claim must be released");
            Assert.That(set.IsReservedByAnotherOwner(Guid.NewGuid(), UniqueValueScope.MetaverseAttribute, 1, "jane.doe"), Is.True, "ownerB's claim must be untouched");
        }
    }
}
