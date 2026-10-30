// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The attribute names the export review banner lists (#2615): read from what a Metaverse Object records as changed outside
/// synchronisation, for the administrator looking at it. The record is read leniently, because an unreadable one must cost
/// the banner its detail and never the page.
/// </summary>
[TestFixture]
public class MetaverseObjectViewStateChangedAttributeNamesTests
{
    // Metaverse Attribute id -> name, as the page resolves them from the attributes that exist.
    private static readonly Dictionary<int, string> Names = new()
    {
        [1] = "Job Title",
        [2] = "Mobile",
        [3] = "Department",
        [4] = "Manager"
    };

    private static string? NameOf(int attributeId) => Names.GetValueOrDefault(attributeId);

    private static string RecordOf(int[] attributeIds, params int[] removedFromAttributeIds) =>
        new MetaverseObjectUnsynchronisedChanges
        {
            AttributeIds = [.. attributeIds],
            Removals = [.. removedFromAttributeIds.Select(id => new MetaverseObjectUnsynchronisedRemoval { AttributeId = id, StringValue = "removed" })]
        }.ToJson();

    [Test]
    public void ChangedAttributeNamesFrom_AttributesWithValuesChanged_ReturnsTheirNamesInNameOrder()
    {
        var json = RecordOf([2, 1, 3]);

        var names = MetaverseObjectViewState.ChangedAttributeNamesFrom(json, NameOf);

        Assert.That(names, Is.EqualTo(new[] { "Department", "Job Title", "Mobile" }));
    }

    [Test]
    public void ChangedAttributeNamesFrom_NamesDifferingInCase_OrdersThemAlphabetically()
    {
        var names = MetaverseObjectViewState.ChangedAttributeNamesFrom(RecordOf([1, 2]), id => id == 1 ? "Zone" : "apple");

        Assert.That(names, Is.EqualTo(new[] { "apple", "Zone" }));
    }

    [Test]
    public void ChangedAttributeNamesFrom_AdditionsAndRemovals_ReturnsTheNamesOfBothOnce()
    {
        // Mobile changed (and had a value removed), Manager only had a value removed: each is named once.
        var json = RecordOf([2], removedFromAttributeIds: [2, 4]);

        var names = MetaverseObjectViewState.ChangedAttributeNamesFrom(json, NameOf);

        Assert.That(names, Is.EqualTo(new[] { "Manager", "Mobile" }));
    }

    [Test]
    public void ChangedAttributeNamesFrom_AttributeOnlyInRemovals_IsNamedWithoutAskingForAnythingTheObjectHolds()
    {
        // The object holds no value for Manager any more, so the name must come from the attribute and not from a value.
        var json = new MetaverseObjectUnsynchronisedChanges
        {
            Removals = [new MetaverseObjectUnsynchronisedRemoval { AttributeId = 4, ReferenceValueId = Guid.NewGuid() }]
        }.ToJson();

        var names = MetaverseObjectViewState.ChangedAttributeNamesFrom(json, NameOf);

        Assert.That(names, Is.EqualTo(new[] { "Manager" }));
    }

    [Test]
    public void ChangedAttributeNamesFrom_AttributeThatNoLongerExists_IsSkippedAndTheRestAreStillNamed()
    {
        var json = RecordOf([1, 99], removedFromAttributeIds: [98, 2]);

        var names = MetaverseObjectViewState.ChangedAttributeNamesFrom(json, NameOf);

        Assert.That(names, Is.EqualTo(new[] { "Job Title", "Mobile" }));
    }

    [Test]
    public void ChangedAttributeNamesFrom_NoAttributeCanBeResolved_ReturnsNothing()
    {
        var names = MetaverseObjectViewState.ChangedAttributeNamesFrom(RecordOf([98, 99]), _ => null);

        Assert.That(names, Is.Empty);
    }

    [Test]
    public void ChangedAttributeNamesFrom_AttributeResolvedToABlankName_IsSkipped()
    {
        var names = MetaverseObjectViewState.ChangedAttributeNamesFrom(RecordOf([1, 2]), id => id == 1 ? "  " : "Mobile");

        Assert.That(names, Is.EqualTo(new[] { "Mobile" }));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void ChangedAttributeNamesFrom_NothingRecorded_ReturnsNothing(string? json)
    {
        Assert.That(MetaverseObjectViewState.ChangedAttributeNamesFrom(json, NameOf), Is.Empty);
    }

    [Test]
    public void ChangedAttributeNamesFrom_RecordNamingNothing_ReturnsNothing()
    {
        Assert.That(MetaverseObjectViewState.ChangedAttributeNamesFrom(RecordOf([]), NameOf), Is.Empty);
    }

    [TestCase("{not json")]
    [TestCase("[1, 2")]
    [TestCase("\"a string, not a record\"")]
    [TestCase("[1, 2, 3]")]
    [TestCase("{\"attributeIds\": \"one\"}")]
    [TestCase("{\"attributeIds\": null, \"removals\": null}")]
    [TestCase("{\"attributeIds\": [1], \"removals\": [null]}")]
    public void ChangedAttributeNamesFrom_UnreadableRecord_ReturnsNothingWithoutThrowing(string json)
    {
        IReadOnlyList<string>? names = null;

        Assert.That(() => names = MetaverseObjectViewState.ChangedAttributeNamesFrom(json, NameOf), Throws.Nothing);

        // A record that is only partly unreadable need not name everything, but it must never name anything it did not hold.
        Assert.That(names, Is.Not.Null);
        Assert.That(names!.Except(Names.Values), Is.Empty);
    }

    [Test]
    public void ApplyExportReview_GivenTheRecordedChanges_ListsTheNamesForTheAdministrator()
    {
        var state = new MetaverseObjectViewState(Guid.NewGuid());
        var json = RecordOf([2], removedFromAttributeIds: [4]);

        state.ApplyExportReview(pending: true, isAdministrator: true, () => MetaverseObjectViewState.ChangedAttributeNamesFrom(json, NameOf));

        Assert.That(state.ExportReviewChangedAttributeNames, Is.EqualTo(new[] { "Manager", "Mobile" }));
    }
}
