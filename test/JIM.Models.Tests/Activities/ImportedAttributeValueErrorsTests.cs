// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Linq;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Models.Tests.Activities;

/// <summary>
/// What an execution item keeps of the values a Connector could not read (#2066): each value with its attribute's type,
/// bounded so one malformed source cannot bloat the Activity.
/// </summary>
[TestFixture]
public class ImportedAttributeValueErrorsTests
{
    private static ConnectedSystemImportObjectAttributeValueError Reported(string attributeName, string? value) =>
        new() { AttributeName = attributeName, Value = value, Reason = "Not a valid date and time." };

    private static ConnectedSystemObjectType PersonType() => new()
    {
        Name = "person",
        Attributes =
        [
            new ConnectedSystemObjectTypeAttribute { Name = "startDate", Type = AttributeDataType.DateTime },
            new ConnectedSystemObjectTypeAttribute { Name = "grades", Type = AttributeDataType.Number }
        ]
    };

    [Test]
    public void Record_NothingReported_RecordsNothing()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ImportedAttributeValueErrors.Record(null, PersonType()), Is.Null);
            Assert.That(ImportedAttributeValueErrors.Record([], PersonType()), Is.Null);
        }
    }

    [Test]
    public void Record_ReportedValues_TakeTheirAttributeTypeFromTheSchemaWhateverItsCase()
    {
        var recorded = ImportedAttributeValueErrors.Record([Reported("STARTDATE", "31/02/2026"), Reported("department", "x")], PersonType());

        Assert.That(recorded!.Values.Select(v => v.AttributeType), Is.EqualTo(new AttributeDataType?[] { AttributeDataType.DateTime, null }),
            "An attribute the schema does not know has no type to show, rather than a guessed one.");
    }

    [Test]
    public void Record_MoreValuesThanAreKept_KeepsTheFirstAndCountsThemAll()
    {
        var reported = Enumerable.Range(1, ImportedAttributeValueErrors.MaxRecordedValues + 5).Select(i => Reported("grades", $"bad{i}")).ToList();

        var recorded = ImportedAttributeValueErrors.Record(reported, PersonType());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(recorded!.Total, Is.EqualTo(ImportedAttributeValueErrors.MaxRecordedValues + 5));
            Assert.That(recorded.Values, Has.Count.EqualTo(ImportedAttributeValueErrors.MaxRecordedValues));
            Assert.That(recorded.Values[0].Value, Is.EqualTo("bad1"));
        }
    }

    [Test]
    public void Record_VeryLongValue_IsShortenedAndMarked()
    {
        var recorded = ImportedAttributeValueErrors.Record([Reported("startDate", new string('x', 5_000))], PersonType());

        var value = recorded!.Values.Single().Value!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(value, Has.Length.EqualTo(ImportedAttributeValueErrors.MaxValueLength + 3));
            Assert.That(value, Does.EndWith("..."));
        }
    }

    [Test]
    public void ToJsonThenFromJson_RoundTripsEveryField()
    {
        var recorded = ImportedAttributeValueErrors.Record([Reported("startDate", "31/02/2026"), Reported("photo", null)], PersonType())!;

        var read = ImportedAttributeValueErrors.FromJson(recorded.ToJson());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(read!.Total, Is.EqualTo(2));
            Assert.That(read.Values.Select(v => (v.AttributeName, v.AttributeType, v.Value, v.Reason)), Is.EqualTo(new[]
            {
                ("startDate", (AttributeDataType?)AttributeDataType.DateTime, (string?)"31/02/2026", "Not a valid date and time."),
                ("photo", (AttributeDataType?)null, (string?)null, "Not a valid date and time.")
            }));
        }
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("{ not json")]
    public void FromJson_AbsentOrUnreadable_IsNull(string? json)
    {
        Assert.That(ImportedAttributeValueErrors.FromJson(json), Is.Null);
    }
}
