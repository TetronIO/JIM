// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Staging;

namespace JIM.Worker.Tests.Synchronisation;

/// <summary>
/// #2153 S5: the import normaliser puts every date into the form JIM holds it, collapses exact duplicates, and reports
/// what JIM cannot hold (two dates less than a microsecond apart; both true and false) rather than choosing silently.
/// </summary>
[TestFixture]
public class ImportValueNormaliserTests
{
    private const string DateAttribute = "pwdFailureTime";
    private const string BoolAttribute = "isActive";

    private static readonly IReadOnlyDictionary<string, AttributeDataType> Lookup = ImportValueNormaliser.BuildDateAndBooleanLookup(new ConnectedSystemObjectType
    {
        Name = "person",
        Attributes =
        [
            new ConnectedSystemObjectTypeAttribute { Id = 1, Name = DateAttribute, Type = AttributeDataType.DateTime, AttributePlurality = AttributePlurality.MultiValued },
            new ConnectedSystemObjectTypeAttribute { Id = 2, Name = BoolAttribute, Type = AttributeDataType.Boolean, AttributePlurality = AttributePlurality.SingleValued },
            new ConnectedSystemObjectTypeAttribute { Id = 3, Name = "cn", Type = AttributeDataType.Text }
        ]
    });

    private static ConnectedSystemImportObject WithDates(params DateTime[] dates) => new()
    {
        ObjectType = "person",
        Attributes = [new ConnectedSystemImportObjectAttribute { Name = DateAttribute, Type = AttributeDataType.DateTime, DateTimeValues = dates.ToList() }]
    };

    private static ConnectedSystemImportObject WithBools(params bool[] values) => new()
    {
        ObjectType = "person",
        Attributes = [new ConnectedSystemImportObjectAttribute { Name = BoolAttribute, Type = AttributeDataType.Boolean, BoolValues = values.ToList() }]
    };

    [Test]
    public void Normalise_ExactDuplicateDates_CollapseToOneAndAreCounted()
    {
        var date = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);
        var importObject = WithDates(date, date.AddHours(1), date);
        var summary = new ImportValueNormalisationSummary();

        ImportValueNormaliser.Normalise(importObject, Lookup, summary);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(importObject.Attributes[0].DateTimeValues, Is.EqualTo(new[] { date, date.AddHours(1) }));
            Assert.That(summary.DuplicatesCollapsed, Is.EqualTo(1));
            Assert.That(importObject.AttributeValueErrors, Is.Null);
            Assert.That(importObject.ErrorType, Is.Null);
        }
    }

    [Test]
    public void Normalise_SameInstantInTwoKinds_IsAnExactDuplicateNotACollision()
    {
        var utc = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);
        var importObject = WithDates(utc, utc.ToLocalTime());
        var summary = new ImportValueNormalisationSummary();

        ImportValueNormaliser.Normalise(importObject, Lookup, summary);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(importObject.Attributes[0].DateTimeValues, Has.Count.EqualTo(1));
            Assert.That(importObject.AttributeValueErrors, Is.Null);
        }
    }

    [Test]
    public void Normalise_SubMicrosecondPair_ReportsEachRawValueAsJimAndCounts()
    {
        var a = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc).AddTicks(1_234_561);
        var b = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc).AddTicks(1_234_569);
        var other = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        var importObject = WithDates(a, b, other);
        var summary = new ImportValueNormalisationSummary();

        ImportValueNormaliser.Normalise(importObject, Lookup, summary);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(importObject.ErrorType, Is.EqualTo(ConnectedSystemImportObjectError.AttributeValueError));
            Assert.That(importObject.AttributeValueErrors, Has.Count.EqualTo(2));
            Assert.That(importObject.AttributeValueErrors!.Select(e => e.Value), Is.EquivalentTo(new[] { "2026-03-01T09:00:00.1234561Z", "2026-03-01T09:00:00.1234569Z" }));
            Assert.That(importObject.AttributeValueErrors!.All(e => e.AttributeName == DateAttribute), Is.True);
            Assert.That(importObject.AttributeValueErrors!.All(e => e.Reason == ImportValueNormaliser.SubMicrosecondCollisionReason), Is.True);
            Assert.That(importObject.AttributeValueErrors!.All(e => e.Source == ImportAttributeValueErrorSource.Jim), Is.True);
            Assert.That(summary.CollisionsReported, Is.EqualTo(2));
        }
    }

    [Test]
    public void Normalise_LocalAndUnspecifiedDates_AreCanonicalised()
    {
        var unspecified = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Unspecified).AddTicks(5);
        var importObject = WithDates(unspecified);
        var summary = new ImportValueNormalisationSummary();

        ImportValueNormaliser.Normalise(importObject, Lookup, summary);

        var held = importObject.Attributes[0].DateTimeValues.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(held.Kind, Is.EqualTo(DateTimeKind.Utc));
            Assert.That(held.Ticks, Is.EqualTo(new DateTime(2026, 3, 1, 9, 0, 0).Ticks));
            Assert.That(summary.DatesCanonicalised, Is.EqualTo(1));
        }
    }

    [Test]
    public void Normalise_TrueAndFalse_ReportsEachValueAndCounts()
    {
        var importObject = WithBools(true, false, true);
        var summary = new ImportValueNormalisationSummary();

        ImportValueNormaliser.Normalise(importObject, Lookup, summary);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(importObject.ErrorType, Is.EqualTo(ConnectedSystemImportObjectError.AttributeValueError));
            Assert.That(importObject.AttributeValueErrors!.Select(e => e.Value), Is.EquivalentTo(new[] { "true", "false" }));
            Assert.That(importObject.AttributeValueErrors!.All(e => e.Reason == ImportValueNormaliser.BooleanSurplusReason && e.Source == ImportAttributeValueErrorSource.Jim), Is.True);
            Assert.That(summary.BooleanSurplusesReported, Is.EqualTo(2));
        }
    }

    [Test]
    public void Normalise_TrueTwice_CollapsesToOneWithoutReport()
    {
        var importObject = WithBools(true, true);
        var summary = new ImportValueNormalisationSummary();

        ImportValueNormaliser.Normalise(importObject, Lookup, summary);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(importObject.Attributes[0].BoolValues, Is.EqualTo(new[] { true }));
            Assert.That(importObject.AttributeValueErrors, Is.Null);
            Assert.That(summary.DuplicatesCollapsed, Is.EqualTo(1));
        }
    }

    [Test]
    public void Normalise_ObjectWithObjectLevelError_IsLeftUntouched()
    {
        var importObject = WithBools(true, false);
        importObject.ErrorType = ConnectedSystemImportObjectError.ConfigurationError;
        importObject.ErrorMessage = "broken";

        ImportValueNormaliser.Normalise(importObject, Lookup, new ImportValueNormalisationSummary());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(importObject.ErrorType, Is.EqualTo(ConnectedSystemImportObjectError.ConfigurationError));
            Assert.That(importObject.AttributeValueErrors, Is.Null);
        }
    }

    [Test]
    public void Normalise_AttributeTheConnectorAlreadyReported_IsSkipped()
    {
        var importObject = WithBools(true, false);
        importObject.AddAttributeValueError(BoolAttribute, "maybe", "Not a valid true or false value.");

        ImportValueNormaliser.Normalise(importObject, Lookup, new ImportValueNormalisationSummary());

        Assert.That(importObject.AttributeValueErrors, Has.Count.EqualTo(1), "Only the Connector's own report; JIM adds none for an attribute it already named.");
    }

    [Test]
    public void Normalise_AttributeNotDateOrBooleanInSchema_IsNotActedOn()
    {
        var importObject = new ConnectedSystemImportObject
        {
            ObjectType = "person",
            Attributes = [new ConnectedSystemImportObjectAttribute { Name = "cn", BoolValues = [true, false] }]
        };

        ImportValueNormaliser.Normalise(importObject, Lookup, new ImportValueNormalisationSummary());

        Assert.That(importObject.AttributeValueErrors, Is.Null);
    }

    [Test]
    public void Normalise_SingleWholeMicrosecondUtcDate_DoesNotAllocateTheBooleanList()
    {
        var importObject = WithDates(new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc));
        var summary = new ImportValueNormalisationSummary();

        ImportValueNormaliser.Normalise(importObject, Lookup, summary);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(importObject.Attributes[0].HasBoolValues, Is.False);
            Assert.That(summary.DatesCanonicalised, Is.Zero);
        }
    }
}
