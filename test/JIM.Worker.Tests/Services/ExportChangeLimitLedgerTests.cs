// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Worker.Tests.Services;

/// <summary>
/// Pins the Run Profile Safeguards (#1618) decision the ledger makes once per Export run: a change
/// type whose pending count exceeds its limit is withheld for the whole run and attempts none of it;
/// a type at or under its limit, or carrying no limit, runs in full. There is no partial attempt.
/// </summary>
[TestFixture]
public class ExportChangeLimitLedgerTests
{
    [Test]
    public void Construct_NoLimit_TypeIsNeverWithheld()
    {
        var ledger = new ExportChangeLimitLedger(
            maxCreates: null, maxUpdates: null, maxDeletes: null,
            executablePendingCountsByType: new Dictionary<PendingExportChangeType, int> { [PendingExportChangeType.Delete] = 5_000 });

        Assert.That(ledger.IsWithheld(PendingExportChangeType.Delete), Is.False);
        Assert.That(ledger.Withheld(PendingExportChangeType.Delete), Is.EqualTo(0));
        Assert.That(ledger.Reserve(PendingExportChangeType.Delete, 5_000), Is.EqualTo(5_000));
        Assert.That(ledger.AnyWithheld, Is.False);
    }

    [Test]
    public void Construct_PendingCountBelowLimit_TypeIsNotWithheld()
    {
        var ledger = new ExportChangeLimitLedger(
            maxCreates: null, maxUpdates: null, maxDeletes: 100,
            executablePendingCountsByType: new Dictionary<PendingExportChangeType, int> { [PendingExportChangeType.Delete] = 60 });

        Assert.That(ledger.IsWithheld(PendingExportChangeType.Delete), Is.False);
        Assert.That(ledger.Withheld(PendingExportChangeType.Delete), Is.EqualTo(0));
        Assert.That(ledger.Reserve(PendingExportChangeType.Delete, 60), Is.EqualTo(60), "an allowed type is granted the whole of what is requested");
    }

    [Test]
    public void Construct_PendingCountEqualToLimit_TypeIsNotWithheld()
    {
        var ledger = new ExportChangeLimitLedger(
            maxCreates: null, maxUpdates: null, maxDeletes: 100,
            executablePendingCountsByType: new Dictionary<PendingExportChangeType, int> { [PendingExportChangeType.Delete] = 100 });

        Assert.That(ledger.IsWithheld(PendingExportChangeType.Delete), Is.False);
        Assert.That(ledger.Withheld(PendingExportChangeType.Delete), Is.EqualTo(0));
        Assert.That(ledger.Reserve(PendingExportChangeType.Delete, 100), Is.EqualTo(100));
    }

    [Test]
    public void Construct_PendingCountOneOverLimit_WithholdsTheWholeType()
    {
        var ledger = new ExportChangeLimitLedger(
            maxCreates: null, maxUpdates: null, maxDeletes: 100,
            executablePendingCountsByType: new Dictionary<PendingExportChangeType, int> { [PendingExportChangeType.Delete] = 101 });

        Assert.That(ledger.IsWithheld(PendingExportChangeType.Delete), Is.True);
        Assert.That(ledger.Withheld(PendingExportChangeType.Delete), Is.EqualTo(101), "the withheld count is the pending count at the start, not a running tally");
        Assert.That(ledger.Reserve(PendingExportChangeType.Delete, 101), Is.EqualTo(0), "a withheld type attempts none of its pending changes");
        Assert.That(ledger.AnyWithheld, Is.True);
    }

    [Test]
    public void Construct_PendingCountWellOverLimit_WithholdsTheWholeType()
    {
        var ledger = new ExportChangeLimitLedger(
            maxCreates: null, maxUpdates: null, maxDeletes: 100,
            executablePendingCountsByType: new Dictionary<PendingExportChangeType, int> { [PendingExportChangeType.Delete] = 442 });

        Assert.That(ledger.IsWithheld(PendingExportChangeType.Delete), Is.True);
        Assert.That(ledger.Withheld(PendingExportChangeType.Delete), Is.EqualTo(442));
        Assert.That(ledger.Reserve(PendingExportChangeType.Delete, 442), Is.EqualTo(0));
    }

    [Test]
    public void Construct_LimitOfZeroWithSomethingPending_WithholdsTheWholeType()
    {
        var ledger = new ExportChangeLimitLedger(
            maxCreates: null, maxUpdates: null, maxDeletes: 0,
            executablePendingCountsByType: new Dictionary<PendingExportChangeType, int> { [PendingExportChangeType.Delete] = 1 });

        Assert.That(ledger.IsWithheld(PendingExportChangeType.Delete), Is.True);
        Assert.That(ledger.Withheld(PendingExportChangeType.Delete), Is.EqualTo(1));
        Assert.That(ledger.Reserve(PendingExportChangeType.Delete, 1), Is.EqualTo(0));
    }

    [Test]
    public void Construct_LimitOfZeroWithNothingPending_TypeIsNotWithheld()
    {
        // Nothing exceeds a limit of zero when the count is also zero: the type is technically
        // "allowed", it simply has nothing to attempt. The type is absent from the counts
        // dictionary, mirroring what the count query returns when nothing of a type is pending.
        var ledger = new ExportChangeLimitLedger(
            maxCreates: null, maxUpdates: null, maxDeletes: 0,
            executablePendingCountsByType: new Dictionary<PendingExportChangeType, int>());

        Assert.That(ledger.IsWithheld(PendingExportChangeType.Delete), Is.False);
        Assert.That(ledger.Withheld(PendingExportChangeType.Delete), Is.EqualTo(0));
    }

    [Test]
    public void Construct_EachChangeTypeDecidedIndependently()
    {
        var ledger = new ExportChangeLimitLedger(
            maxCreates: 10, maxUpdates: null, maxDeletes: 0,
            executablePendingCountsByType: new Dictionary<PendingExportChangeType, int>
            {
                [PendingExportChangeType.Create] = 12,
                [PendingExportChangeType.Update] = 1_200,
                [PendingExportChangeType.Delete] = 442
            });

        Assert.That(ledger.IsWithheld(PendingExportChangeType.Create), Is.True, "12 pending exceeds the limit of 10");
        Assert.That(ledger.IsWithheld(PendingExportChangeType.Update), Is.False, "no limit is set");
        Assert.That(ledger.IsWithheld(PendingExportChangeType.Delete), Is.True, "anything pending exceeds a limit of 0");

        Assert.That(ledger.Withheld(PendingExportChangeType.Create), Is.EqualTo(12));
        Assert.That(ledger.Withheld(PendingExportChangeType.Update), Is.EqualTo(0));
        Assert.That(ledger.Withheld(PendingExportChangeType.Delete), Is.EqualTo(442));

        Assert.That(ledger.Reserve(PendingExportChangeType.Create, 12), Is.EqualTo(0));
        Assert.That(ledger.Reserve(PendingExportChangeType.Update, 1_200), Is.EqualTo(1_200));
        Assert.That(ledger.Reserve(PendingExportChangeType.Delete, 442), Is.EqualTo(0));
    }

    [Test]
    public void Limit_ReturnsTheConfiguredLimitPerType()
    {
        var ledger = new ExportChangeLimitLedger(
            maxCreates: 5, maxUpdates: null, maxDeletes: 0,
            executablePendingCountsByType: new Dictionary<PendingExportChangeType, int>());

        Assert.That(ledger.Limit(PendingExportChangeType.Create), Is.EqualTo(5));
        Assert.That(ledger.Limit(PendingExportChangeType.Update), Is.Null);
        Assert.That(ledger.Limit(PendingExportChangeType.Delete), Is.EqualTo(0));
    }

    [Test]
    public void Reserve_ZeroOrNegativeRequested_GrantsNothingRegardlessOfWithheldState()
    {
        var ledger = new ExportChangeLimitLedger(
            maxCreates: null, maxUpdates: null, maxDeletes: null,
            executablePendingCountsByType: new Dictionary<PendingExportChangeType, int>());

        Assert.That(ledger.Reserve(PendingExportChangeType.Delete, 0), Is.EqualTo(0));
    }

    [Test]
    public void AnyWithheld_NoTypeWithheld_IsFalse()
    {
        var ledger = new ExportChangeLimitLedger(
            maxCreates: 10, maxUpdates: 10, maxDeletes: 10,
            executablePendingCountsByType: new Dictionary<PendingExportChangeType, int>
            {
                [PendingExportChangeType.Create] = 10,
                [PendingExportChangeType.Update] = 5,
                [PendingExportChangeType.Delete] = 0
            });

        Assert.That(ledger.AnyWithheld, Is.False);
    }
}
