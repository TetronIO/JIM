// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
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
    public void ImportReadRangeField_ToggleClicked_ShowsBothRecordedStatesHighlighted()
    {
        var cut = RenderField(ConnectedSystemRunType.DeltaImport, RawFrom, RawTo, new ImportReadRange { From = Sequence(8), To = Sequence(15) });

        cut.Find(".jim-import-read-range button").Click();

        var blocks = cut.FindAll(".jim-json-syntax");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(blocks, Has.Count.EqualTo(2));
            Assert.That(Text(cut), Does.Contain("Before this run"));
            Assert.That(Text(cut), Does.Contain("After this run"));
            Assert.That(blocks[0].QuerySelector(".jim-json-key")?.TextContent, Is.EqualTo("\"changeSequence\""));
        }
    }

    [Test]
    public void ImportReadRangeField_NothingRecorded_RendersNothing()
    {
        var cut = RenderField(ConnectedSystemRunType.DeltaImport, null, null, null);

        Assert.That(cut.Markup.Trim(), Is.Empty);
    }
}
