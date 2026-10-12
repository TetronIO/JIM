// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Linq;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Models.Tests.Transactional;

/// <summary>
/// The runs a Pending Export records as having staged it (#2100, Phase 3-0).
/// <para>
/// One export can carry changes from several synchronisations, when a later run adds its change to an export still
/// queued from an earlier one. Each of those runs caused the export, so each must be recorded on it: the export run
/// links back to every one of them, and the Causality Explorer's Queued by panel lists them. The latest stays named
/// on its own as well, because the portal's connection explanations read it.
/// </para>
/// </summary>
[TestFixture]
public class PendingExportStagerTests
{
    private static readonly Guid First = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Second = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Test]
    public void RecordStager_FirstRun_IsTheLatestAndTheOnlyStager()
    {
        var export = new PendingExport();

        export.RecordStager(First);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(export.QueuedByRunProfileExecutionItemId, Is.EqualTo(First));
            Assert.That(export.StagedByRunProfileExecutionItemIds, Is.EqualTo(new[] { First }));
        }
    }

    [Test]
    public void RecordStager_ALaterRun_IsAddedAfterTheEarlierOneAndBecomesTheLatest()
    {
        var export = new PendingExport();

        export.RecordStager(First);
        export.RecordStager(Second);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(export.QueuedByRunProfileExecutionItemId, Is.EqualTo(Second));
            Assert.That(export.StagedByRunProfileExecutionItemIds, Is.EqualTo(new[] { First, Second }), "oldest first");
        }
    }

    [Test]
    public void RecordStager_TheSameRunAgain_IsRecordedOnce()
    {
        // One item can stage into one export twice in a run: drift correction queues a fix, then the object's own
        // export evaluation merges into it.
        var export = new PendingExport();

        export.RecordStager(First);
        export.RecordStager(First);

        Assert.That(export.StagedByRunProfileExecutionItemIds, Is.EqualTo(new[] { First }));
    }

    [Test]
    public void RecordStager_MoreRunsThanItKeeps_KeepsTheMostRecent()
    {
        // An export that cannot be sent can sit queued for weeks, gaining a stager from every run that changes its
        // object. The list is capped so the row cannot grow without bound, and the runs dropped are the oldest.
        var export = new PendingExport();
        var stagers = Enumerable.Range(0, PendingExport.MaxRecordedStagers + 3).Select(_ => Guid.NewGuid()).ToList();

        foreach (var stager in stagers)
            export.RecordStager(stager);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(export.StagedByRunProfileExecutionItemIds, Has.Count.EqualTo(PendingExport.MaxRecordedStagers));
            Assert.That(export.StagedByRunProfileExecutionItemIds, Is.EqualTo(stagers.Skip(3)));
            Assert.That(export.QueuedByRunProfileExecutionItemId, Is.EqualTo(stagers[^1]));
        }
    }

    [Test]
    public void InheritStagersFrom_AnExportItReplaces_CarriesItsStagersInOrder()
    {
        // A later run's change merges with an export still queued by deleting it and staging a replacement. The
        // replacement carries every change the original did, so it carries the runs that staged them too.
        var original = new PendingExport();
        original.RecordStager(First);
        original.RecordStager(Second);
        var replacement = new PendingExport();

        replacement.InheritStagersFrom(original);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(replacement.StagedByRunProfileExecutionItemIds, Is.EqualTo(new[] { First, Second }));
            Assert.That(replacement.QueuedByRunProfileExecutionItemId, Is.EqualTo(Second));
        }
    }

    [Test]
    public void InheritStagersFrom_AnExportStagedBeforeTheListWasRecorded_CarriesItsLatestStager()
    {
        // Exports queued before this release name their latest stager only. That one is still a stager of the
        // replacement, so it is carried rather than lost.
        var original = new PendingExport();
        original.RecordStager(First);
        original.StagedByRunProfileExecutionItemIds.Clear();
        var replacement = new PendingExport();

        replacement.InheritStagersFrom(original);

        Assert.That(replacement.StagedByRunProfileExecutionItemIds, Is.EqualTo(new[] { First }));
    }

    [Test]
    public void InheritStagersFrom_ThenTheReplacingRun_ListsEveryRunOnce()
    {
        var original = new PendingExport();
        original.RecordStager(First);
        var replacement = new PendingExport();

        replacement.InheritStagersFrom(original);
        replacement.RecordStager(Second);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(replacement.StagedByRunProfileExecutionItemIds, Is.EqualTo(new[] { First, Second }));
            Assert.That(replacement.QueuedByRunProfileExecutionItemId, Is.EqualTo(Second));
        }
    }

    [Test]
    public void Stagers_AnExportStagedBeforeTheListWasRecorded_FallsBackToItsLatestStager()
    {
        var export = new PendingExport();
        export.RecordStager(First);
        export.StagedByRunProfileExecutionItemIds.Clear();

        Assert.That(export.Stagers(), Is.EqualTo(new[] { First }));
    }

    [Test]
    public void Stagers_AnExportWithNoRecordedRun_IsEmpty()
    {
        Assert.That(new PendingExport().Stagers(), Is.Empty);
    }

    [Test]
    public void WithCauseFrom_AnExportStagedBySeveralRuns_CopiesEveryStager()
    {
        // The Pending Export is deleted the moment its export succeeds, so the export run copies what it needs
        // first. A stager missed here cannot be recovered afterwards.
        var export = new PendingExport { Id = Guid.NewGuid() };
        export.RecordStager(First);
        export.RecordStager(Second);

        var item = new ProcessedExportItem().WithCauseFrom(export);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(item.StagedByRunProfileExecutionItemIds, Is.EqualTo(new[] { First, Second }));
            Assert.That(item.QueuedByRunProfileExecutionItemId, Is.EqualTo(Second));
        }
    }
}
