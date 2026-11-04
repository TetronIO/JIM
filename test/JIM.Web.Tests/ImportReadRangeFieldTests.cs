// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Threading.Tasks;
using Bunit;
using JIM.Models.Activities;
using JIM.Models.Staging;
using JIM.Web.Shared;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers how an import's watermarks read on its Activity page. The recorded values are a Connector's persisted data,
/// commonly far more than a watermark, so the page says in words what the Connector describes and keeps the raw value
/// behind a toggle for support.
/// </summary>
[TestFixture]
public class ImportReadRangeFieldTests : JimComponentTestContext
{
    private const string RawFrom = "{\"changeSequence\":8}";
    private const string RawTo = "{\"changeSequence\":15}";

    private static ImportWatermarkDescription Sequence(long number, string kind = "change sequence") =>
        new() { Kind = kind, Points = [new ImportWatermarkPoint { Number = number }] };

    private IRenderedComponent<ImportReadRangeField> RenderField(
        ConnectedSystemRunType runType, string? rawFrom, string? rawTo, ImportReadRange? range) =>
        Render<ImportReadRangeField>(p => p
            .Add(c => c.RunType, runType)
            .Add(c => c.WatermarkFrom, rawFrom)
            .Add(c => c.WatermarkTo, rawTo)
            .Add(c => c.ReadRange, range));

    private static string Text(IRenderedComponent<ImportReadRangeField> cut) => cut.Find(".jim-import-read-range").TextContent;

    [Test]
    public void ImportReadRangeField_FullImport_SaysWhereTheNextDeltaImportStarts()
    {
        // A Full Import reads everything, so where it started from is meaningless; what it sets up is the point.
        var cut = RenderField(ConnectedSystemRunType.FullImport, RawFrom, RawTo,
            new ImportReadRange { From = Sequence(8), To = Sequence(15) });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Text(cut), Does.Contain("Next Delta Import Reads Changes After"));
            Assert.That(Text(cut), Does.Contain("15 (change sequence)"));
            Assert.That(Text(cut), Does.Not.Contain("8"));
        }
    }

    [Test]
    public void ImportReadRangeField_FullImportThatRecordedNoNewWatermark_SaysTheNextDeltaImportStartsWhereThisOneDid()
    {
        var cut = RenderField(ConnectedSystemRunType.FullImport, RawFrom, null, new ImportReadRange { From = Sequence(8) });

        Assert.That(Text(cut), Does.Contain("8 (change sequence)"));
    }

    [Test]
    public void ImportReadRangeField_DeltaImport_SaysWhatRangeItRead()
    {
        var cut = RenderField(ConnectedSystemRunType.DeltaImport, RawFrom, RawTo,
            new ImportReadRange { From = Sequence(8), To = Sequence(1500) });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Text(cut), Does.Contain("Read Changes"));
            Assert.That(Text(cut), Does.Contain("After 8, up to 1,500 (change sequence)"));
        }
    }

    [Test]
    public void ImportReadRangeField_DeltaImportWithNothingNewer_SaysThereWereNoChanges()
    {
        var cut = RenderField(ConnectedSystemRunType.DeltaImport, RawFrom, RawFrom,
            new ImportReadRange { From = Sequence(8), To = Sequence(8) });

        Assert.That(Text(cut), Does.Contain("No changes after 8 (change sequence)"));
    }

    [Test]
    public void ImportReadRangeField_DeltaImportWithNothingToStartFrom_SaysItReadFromTheStart()
    {
        var cut = RenderField(ConnectedSystemRunType.DeltaImport, null, RawTo, new ImportReadRange { To = Sequence(15) });

        Assert.That(Text(cut), Does.Contain("From the start, up to 15 (change sequence)"));
    }

    [Test]
    public void ImportReadRangeField_DeltaImportThatRecordedNoNewWatermark_SaysTheNextReadsFromTheSamePoint()
    {
        var cut = RenderField(ConnectedSystemRunType.DeltaImport, RawFrom, null, new ImportReadRange { From = Sequence(8) });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Text(cut), Does.Contain("After 8 (change sequence)"));
            Assert.That(Text(cut), Does.Contain("the next Delta Import reads from the same point"));
        }
    }

    [Test]
    public void ImportReadRangeField_WatermarkKindChangedBetweenRuns_NamesEachSidesKind()
    {
        var cut = RenderField(ConnectedSystemRunType.DeltaImport, RawFrom, RawTo,
            new ImportReadRange { From = Sequence(8, "change log entry"), To = Sequence(900, "Update Sequence Number") });

        Assert.That(Text(cut), Does.Contain("After 8 (change log entry), up to 900 (Update Sequence Number)"));
    }

    [Test]
    public void ImportReadRangeField_TimeWatermark_ShowsTheLocalTime()
    {
        var time = new DateTime(2026, 10, 24, 20, 21, 13, DateTimeKind.Utc);
        var range = new ImportReadRange { To = new ImportWatermarkDescription { Kind = "access log", Points = [new ImportWatermarkPoint { Time = time }] } };

        var cut = RenderField(ConnectedSystemRunType.FullImport, null, RawTo, range);

        Assert.That(Text(cut), Does.Contain($"{time.ToLocalTime().ToFriendlyDate()} (access log)"));
    }

    [Test]
    public void ImportReadRangeField_WatermarkPerObjectType_GivesEachItsOwnLine()
    {
        static ImportWatermarkDescription PerType(long users, long groups) => new()
        {
            Kind = "change log sequence",
            Points = [new ImportWatermarkPoint { Scope = "User", Number = users }, new ImportWatermarkPoint { Scope = "Group", Number = groups }]
        };

        var cut = RenderField(ConnectedSystemRunType.DeltaImport, RawFrom, RawTo,
            new ImportReadRange { From = PerType(10, 4), To = PerType(25, 4) });

        var lines = cut.FindAll(".jim-import-read-range-point").Select(l => l.TextContent.Trim()).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(lines, Is.EqualTo(new[] { "User: after 10, up to 25 (change log sequence)", "Group: no changes after 4 (change log sequence)" }));
        }
    }

    [Test]
    public void ImportReadRangeField_NotDescribedByItsConnector_OffersOnlyTheRawState()
    {
        var cut = RenderField(ConnectedSystemRunType.DeltaImport, RawFrom, RawTo, null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Text(cut), Does.Not.Contain("Read Changes"));
            Assert.That(Text(cut), Does.Contain("Show Connector state"));
        }
    }

    [Test]
    public void ImportReadRangeField_ByDefault_KeepsTheRawStateOutOfTheMarkup()
    {
        var cut = RenderField(ConnectedSystemRunType.DeltaImport, RawFrom, RawTo, new ImportReadRange { From = Sequence(8), To = Sequence(15) });

        Assert.That(cut.Markup, Does.Not.Contain("changeSequence"));
    }

    [Test]
    public async Task ImportReadRangeField_ToggleClicked_ShowsBothRecordedStatesHighlightedAsync()
    {
        var cut = RenderField(ConnectedSystemRunType.DeltaImport, RawFrom, RawTo, new ImportReadRange { From = Sequence(8), To = Sequence(15) });

        await cut.Find(".jim-import-read-range button").ClickAsync();

        var blocks = cut.FindAll(".jim-json-syntax");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(blocks, Has.Count.EqualTo(2));
            Assert.That(Text(cut), Does.Contain("Before this run"));
            Assert.That(Text(cut), Does.Contain("After this run"));
            Assert.That(blocks[0].QuerySelector(".jim-json-key")?.TextContent, Is.EqualTo("\"changeSequence\""));
        }
    }

    // ─── Partition watermarks (#2715) ───

    private const string CorpFrom = "{\"Kind\":\"Accesslog\",\"AccesslogTimestamp\":\"20261103092014.000000Z\"}";
    private const string CorpTo = "{\"Kind\":\"Accesslog\",\"AccesslogTimestamp\":\"20261103100541.000000Z\"}";
    private const string PartnersFrom = "{\"Kind\":\"Accesslog\",\"AccesslogTimestamp\":\"20261103094002.000000Z\"}";

    private static ImportPartitionWatermark Partition(int id, string name, string? value) => new() { PartitionId = id, PartitionName = name, Value = value };

    private static ImportWatermarkDescription Scoped(params (string Scope, long Number)[] points) => new()
    {
        Kind = "change log entry",
        Points = points.Select(p => new ImportWatermarkPoint { Scope = p.Scope, Number = p.Number }).ToList()
    };

    private IRenderedComponent<ImportReadRangeField> RenderPartitions(
        List<ImportPartitionWatermark>? from, List<ImportPartitionWatermark>? to, ImportReadRange? range = null, string? rawFrom = null, string? rawTo = null) =>
        Render<ImportReadRangeField>(p => p
            .Add(c => c.RunType, ConnectedSystemRunType.DeltaImport)
            .Add(c => c.WatermarkFrom, rawFrom)
            .Add(c => c.WatermarkTo, rawTo)
            .Add(c => c.ReadRange, range)
            .Add(c => c.PartitionWatermarksFrom, from)
            .Add(c => c.PartitionWatermarksTo, to));

    private static async Task ShowStateAsync(IRenderedComponent<ImportReadRangeField> cut) =>
        await cut.Find(".jim-import-read-range-toggle").ClickAsync();

    private static List<string> RowNames(IRenderedComponent<ImportReadRangeField> cut) =>
        cut.FindAll(".jim-import-partition-watermark-row .jim-import-partition-watermark-name").Select(c => c.TextContent.Trim()).ToList();

    private static List<string?> States(IRenderedComponent<ImportReadRangeField> cut, string side) =>
        cut.FindAll($".jim-import-partition-watermark-row .jim-import-partition-watermark-{side}").Select(c => c.GetAttribute("data-watermark-state")).ToList();

    [Test]
    public async Task ImportReadRangeField_PartitionWatermarks_ShowsOneLinePerPartitionAsync()
    {
        // A Connector that keeps its watermarks per partition need record nothing for the Connected System as a whole;
        // the field still reads each partition's range, and its state lists each partition rather than one box.
        var range = new ImportReadRange { From = Scoped(("Corp", 8), ("Partners", 4)), To = Scoped(("Corp", 15), ("Partners", 4)) };
        var cut = RenderPartitions(
            [Partition(1, "Corp", CorpFrom), Partition(2, "Partners", PartnersFrom)],
            [Partition(1, "Corp", CorpTo), Partition(2, "Partners", PartnersFrom)],
            range);

        var lines = cut.FindAll(".jim-import-read-range-point");
        await ShowStateAsync(cut);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(lines, Has.Count.EqualTo(2));
            Assert.That(RowNames(cut), Is.EqualTo(new[] { "Corp", "Partners" }));
            Assert.That(cut.FindAll(".jim-import-read-range-state"), Is.Empty, "the partitions' table replaces the Connected System's boxes");
        }
    }

    [Test]
    public async Task ImportReadRangeField_PartitionWithNoStartingWatermark_ReadsFromTheStartAsync()
    {
        // Partners had no watermark, so the run read every object in it: its line reads from the start, and its state
        // shows it started from none.
        var range = new ImportReadRange { From = Scoped(("Corp", 8)), To = Scoped(("Corp", 15), ("Partners", 20)) };
        var cut = RenderPartitions(
            [Partition(1, "Corp", CorpFrom), Partition(2, "Partners", null)],
            [Partition(1, "Corp", CorpTo), Partition(2, "Partners", PartnersFrom)],
            range);

        var partnersLine = cut.FindAll(".jim-import-read-range-point").Single(l => l.TextContent.StartsWith("Partners", StringComparison.Ordinal)).TextContent;
        await ShowStateAsync(cut);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(partnersLine, Does.Contain("from the start, up to 20"));
            Assert.That(States(cut, "before"), Is.EqualTo(new[] { "recorded", "none" }));
            Assert.That(States(cut, "after"), Is.EqualTo(new[] { "recorded", "recorded" }));
        }
    }

    [Test]
    public async Task ImportReadRangeField_PartitionRecordedTheSameValueOrLeftOut_ReadsUnchangedAsync()
    {
        // Corp moved on; Partners was recorded again at the same point; Archive was read but left out by its Connector,
        // so it keeps the watermark it started from.
        var cut = RenderPartitions(
            [Partition(1, "Archive", PartnersFrom), Partition(2, "Corp", CorpFrom), Partition(3, "Partners", PartnersFrom)],
            [Partition(2, "Corp", CorpTo), Partition(3, "Partners", PartnersFrom)]);

        await ShowStateAsync(cut);

        var after = cut.FindAll(".jim-import-partition-watermark-row .jim-import-partition-watermark-after");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(States(cut, "after"), Is.EqualTo(new[] { "unchanged", "recorded", "unchanged" }));
            Assert.That(after[1].TextContent, Does.Contain(CorpTo), "a new value is shown in full");
            Assert.That(after[2].TextContent, Does.Not.Contain(PartnersFrom), "an unchanged value is not repeated");
        }
    }

    [Test]
    public async Task ImportReadRangeField_RunRecordedNoPartitionWatermark_SaysNothingWasRecordedForEveryPartitionAsync()
    {
        // The run kept every watermark it started from (it failed, or withheld the deletions it detected), which is not
        // the same as recording each one again.
        var cut = RenderPartitions([Partition(1, "Corp", CorpFrom), Partition(2, "Partners", null)], null);

        await ShowStateAsync(cut);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(RowNames(cut), Is.EqualTo(new[] { "Corp", "Partners" }));
            Assert.That(States(cut, "after"), Is.EqualTo(new[] { "not-recorded", "not-recorded" }));
        }
    }

    [Test]
    public async Task ImportReadRangeField_PartitionWatermarkCleared_SaysSoRatherThanUnchangedAsync()
    {
        var cut = RenderPartitions([Partition(1, "Corp", CorpFrom)], [Partition(1, "Corp", null)]);

        await ShowStateAsync(cut);

        Assert.That(States(cut, "after"), Is.EqualTo(new[] { "cleared" }));
    }

    [Test]
    public void ImportReadRangeField_PartitionWatermarks_KeepsTheRawValuesOutOfTheMarkupUntilAsked()
    {
        var cut = RenderPartitions([Partition(1, "Corp", CorpFrom)], [Partition(1, "Corp", CorpTo)]);

        Assert.That(cut.Markup, Does.Not.Contain("AccesslogTimestamp"));
    }

    [Test]
    public void ImportReadRangeField_NothingRecorded_RendersNothing()
    {
        var cut = RenderField(ConnectedSystemRunType.DeltaImport, null, null, null);

        Assert.That(cut.Markup.Trim(), Is.Empty);
    }
}
