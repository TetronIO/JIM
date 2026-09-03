// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Worker.Tests.Services;

/// <summary>
/// Pins the reservation semantics Run Profile Safeguards (#1618) depends on: a batch may only attempt
/// as many of a change type as its Run Profile's limit still allows, the shortfall is recorded as
/// withheld, and the ledger holds exactly under concurrent reservation from parallel export batches.
/// </summary>
[TestFixture]
public class ExportChangeLimitLedgerTests
{
    [Test]
    public void Reserve_NoLimit_GrantsEverythingRequested()
    {
        var ledger = new ExportChangeLimitLedger(maxCreates: null, maxUpdates: null, maxDeletes: null);

        var granted = ledger.Reserve(PendingExportChangeType.Delete, 500);

        Assert.That(granted, Is.EqualTo(500));
        Assert.That(ledger.Withheld(PendingExportChangeType.Delete), Is.EqualTo(0));
        Assert.That(ledger.Attempted(PendingExportChangeType.Delete), Is.EqualTo(500));
        Assert.That(ledger.AnyWithheld, Is.False);
    }

    [Test]
    public void Reserve_LimitOfZero_GrantsNoneAndWithholdsTheLot()
    {
        var ledger = new ExportChangeLimitLedger(maxCreates: null, maxUpdates: null, maxDeletes: 0);

        var granted = ledger.Reserve(PendingExportChangeType.Delete, 342);

        Assert.That(granted, Is.EqualTo(0));
        Assert.That(ledger.Withheld(PendingExportChangeType.Delete), Is.EqualTo(342));
        Assert.That(ledger.Attempted(PendingExportChangeType.Delete), Is.EqualTo(0));
        Assert.That(ledger.AnyWithheld, Is.True);
    }

    [Test]
    public void Reserve_LimitEqualToQueue_GrantsAllAndWithholdsNone()
    {
        var ledger = new ExportChangeLimitLedger(maxCreates: null, maxUpdates: null, maxDeletes: 100);

        var granted = ledger.Reserve(PendingExportChangeType.Delete, 100);

        Assert.That(granted, Is.EqualTo(100));
        Assert.That(ledger.Withheld(PendingExportChangeType.Delete), Is.EqualTo(0));
        Assert.That(ledger.Attempted(PendingExportChangeType.Delete), Is.EqualTo(100));
    }

    [Test]
    public void Reserve_LimitBelowQueue_GrantsRemainderThenNoneOnTheNextCall()
    {
        var ledger = new ExportChangeLimitLedger(maxCreates: null, maxUpdates: null, maxDeletes: 100);

        var firstGrant = ledger.Reserve(PendingExportChangeType.Delete, 60);
        var secondGrant = ledger.Reserve(PendingExportChangeType.Delete, 60);

        Assert.That(firstGrant, Is.EqualTo(60), "the first batch is fully within capacity");
        Assert.That(secondGrant, Is.EqualTo(40), "only the remaining 40 of the limit are granted");
        Assert.That(ledger.Withheld(PendingExportChangeType.Delete), Is.EqualTo(20));
        Assert.That(ledger.Attempted(PendingExportChangeType.Delete), Is.EqualTo(100));

        var thirdGrant = ledger.Reserve(PendingExportChangeType.Delete, 10);

        Assert.That(thirdGrant, Is.EqualTo(0), "the limit is already exhausted");
        Assert.That(ledger.Withheld(PendingExportChangeType.Delete), Is.EqualTo(30));
    }

    [Test]
    public void Reserve_EachChangeTypeHasItsOwnLimit()
    {
        var ledger = new ExportChangeLimitLedger(maxCreates: 10, maxUpdates: null, maxDeletes: 0);

        var createsGranted = ledger.Reserve(PendingExportChangeType.Create, 12);
        var updatesGranted = ledger.Reserve(PendingExportChangeType.Update, 1200);
        var deletesGranted = ledger.Reserve(PendingExportChangeType.Delete, 442);

        Assert.That(createsGranted, Is.EqualTo(10));
        Assert.That(updatesGranted, Is.EqualTo(1200));
        Assert.That(deletesGranted, Is.EqualTo(0));

        Assert.That(ledger.Withheld(PendingExportChangeType.Create), Is.EqualTo(2));
        Assert.That(ledger.Withheld(PendingExportChangeType.Update), Is.EqualTo(0));
        Assert.That(ledger.Withheld(PendingExportChangeType.Delete), Is.EqualTo(442));
    }

    [Test]
    public void Limit_ReturnsTheConfiguredLimitPerType()
    {
        var ledger = new ExportChangeLimitLedger(maxCreates: 5, maxUpdates: null, maxDeletes: 0);

        Assert.That(ledger.Limit(PendingExportChangeType.Create), Is.EqualTo(5));
        Assert.That(ledger.Limit(PendingExportChangeType.Update), Is.Null);
        Assert.That(ledger.Limit(PendingExportChangeType.Delete), Is.EqualTo(0));
    }

    [Test]
    public void Reserve_ConcurrentReservationsAgainstOneLimit_GrantsExactlyTheLimitInTotal()
    {
        // 1,000 concurrent single-export reservations against a limit of 100 (#1618): the ledger's lock
        // must hold under Parallel.For, since parallel export batches call Reserve concurrently.
        var ledger = new ExportChangeLimitLedger(maxCreates: null, maxUpdates: null, maxDeletes: 100);
        var totalGranted = 0;

        Parallel.For(0, 1000, _ =>
        {
            var granted = ledger.Reserve(PendingExportChangeType.Delete, 1);
            Interlocked.Add(ref totalGranted, granted);
        });

        Assert.That(totalGranted, Is.EqualTo(100), "exactly the limit must be granted across every concurrent caller");
        Assert.That(ledger.Attempted(PendingExportChangeType.Delete), Is.EqualTo(100));
        Assert.That(ledger.Withheld(PendingExportChangeType.Delete), Is.EqualTo(900));
    }
}
