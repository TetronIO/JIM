// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Linq;
using System.Text;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Models.Tests.Activities;

/// <summary>
/// What an execution item keeps of an imported object that was rejected before a Connected System Object existed for it
/// (#874): every attribute the Connector returned, with credential values withheld, bounded per attribute and per snapshot.
/// </summary>
[TestFixture]
public class ImportedObjectSnapshotTests
{
    private static ConnectedSystemImportObjectAttribute Text(string name, params string[] values) =>
        new() { Name = name, Type = AttributeDataType.Text, StringValues = values.ToList() };

    private static ConnectedSystemImportObject Person(params ConnectedSystemImportObjectAttribute[] attributes) => new()
    {
        ObjectType = "person",
        ChangeType = ObjectChangeType.Added,
        Attributes = attributes.ToList()
    };

    [Test]
    public void Record_KeepsObjectTypeChangeTypeAndEveryAttributeInTheConnectorsOrder()
    {
        var snapshot = ImportedObjectSnapshot.Record(Person(Text("employeeId", "100482"), Text("Department", "Finance"), Text("department", "Finance Operations")));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot.ObjectType, Is.EqualTo("person"));
            Assert.That(snapshot.ChangeType, Is.EqualTo(ObjectChangeType.Added));
            Assert.That(snapshot.Attributes.Select(a => a.Name), Is.EqualTo(new[] { "employeeId", "Department", "department" }),
                "Both duplicated attributes are kept: they are the evidence for a duplicate attribute error.");
            Assert.That(snapshot.Attributes[2].Values, Is.EqualTo(new[] { "Finance Operations" }));
            Assert.That(snapshot.OmittedAttributeCount, Is.Null);
        }
    }

    [Test]
    public void Record_EachValueType_IsKeptAsReadableText()
    {
        var importObject = Person(
            new ConnectedSystemImportObjectAttribute { Name = "grade", Type = AttributeDataType.Number, IntValues = [7] },
            new ConnectedSystemImportObjectAttribute { Name = "usn", Type = AttributeDataType.LongNumber, LongValues = [9_000_000_000] },
            new ConnectedSystemImportObjectAttribute { Name = "salary", Type = AttributeDataType.Decimal, DecimalValues = [1234.5m] },
            new ConnectedSystemImportObjectAttribute { Name = "startDate", Type = AttributeDataType.DateTime, DateTimeValues = [new DateTime(2026, 11, 23, 9, 0, 0, DateTimeKind.Utc)] },
            new ConnectedSystemImportObjectAttribute { Name = "objectGUID", Type = AttributeDataType.Guid, GuidValues = [Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff")] },
            new ConnectedSystemImportObjectAttribute { Name = "enabled", Type = AttributeDataType.Boolean, BoolValues = [true] },
            new ConnectedSystemImportObjectAttribute { Name = "photo", Type = AttributeDataType.Binary, ByteValues = [new byte[2048]] },
            new ConnectedSystemImportObjectAttribute { Name = "manager", Type = AttributeDataType.Reference, ReferenceValues = ["100117"] });

        var snapshot = ImportedObjectSnapshot.Record(importObject);

        Assert.That(snapshot.Attributes.Select(a => (a.Name, a.Type, a.Values.Single())), Is.EqualTo(new[]
        {
            ("grade", AttributeDataType.Number, "7"),
            ("usn", AttributeDataType.LongNumber, "9000000000"),
            ("salary", AttributeDataType.Decimal, "1234.5"),
            ("startDate", AttributeDataType.DateTime, "2026-11-23T09:00:00.0000000Z"),
            ("objectGUID", AttributeDataType.Guid, "6f9619ff-8b86-d011-b42d-00c04fc964ff"),
            ("enabled", AttributeDataType.Boolean, "true"),
            ("photo", AttributeDataType.Binary, "[binary, 2,048 bytes]"),
            ("manager", AttributeDataType.Reference, "100117")
        }), "Binary content is described, never stored: it is unreadable and would spend the snapshot's whole budget.");
    }

    [TestCase("unicodePwd")]
    [TestCase("PASSWORD")]
    [TestCase("initialPassword")]
    [TestCase("apiSecret")]
    [TestCase("pwdLastSet")]
    public void Record_CredentialOrCredentialLikeAttribute_KeepsTheNameButWithholdsTheValues(string name)
    {
        var snapshot = ImportedObjectSnapshot.Record(Person(Text("employeeId", "100482"), Text(name, "Hunter2!", "Hunter3!")));

        var withheld = snapshot.Attributes.Single(a => a.Name == name);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(withheld.Withheld, Is.True);
            Assert.That(withheld.Values, Is.Empty);
            Assert.That(withheld.OmittedValueCount, Is.Null, "A withheld attribute does not say how many values it had either.");
            Assert.That(snapshot.ToJson(), Does.Not.Contain("Hunter"));
        }
    }

    [Test]
    public void Record_MultiValuedAttributeOverTheLimit_KeepsTheFirstValuesAndCountsTheRest()
    {
        var values = Enumerable.Range(1, ImportedObjectSnapshot.MaxValuesPerAttribute + 214).Select(i => $"smtp:alias{i}@example.org").ToArray();

        var snapshot = ImportedObjectSnapshot.Record(Person(Text("employeeId", "100482"), Text("proxyAddresses", values)));

        var proxyAddresses = snapshot.Attributes.Single(a => a.Name == "proxyAddresses");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(proxyAddresses.Values, Has.Count.EqualTo(ImportedObjectSnapshot.MaxValuesPerAttribute));
            Assert.That(proxyAddresses.Values[0], Is.EqualTo("smtp:alias1@example.org"));
            Assert.That(proxyAddresses.OmittedValueCount, Is.EqualTo(214));
            Assert.That(snapshot.Attributes.Single(a => a.Name == "employeeId").OmittedValueCount, Is.Null);
        }
    }

    [Test]
    public void Record_VeryLongValue_IsShortenedAndMarked()
    {
        var snapshot = ImportedObjectSnapshot.Record(Person(Text("description", new string('x', 5_000))));

        var value = snapshot.Attributes.Single().Values.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(value, Has.Length.EqualTo(ImportedObjectSnapshot.MaxValueLength + 3));
            Assert.That(value, Does.EndWith("..."));
        }
    }

    [Test]
    public void Record_OverTheSnapshotLimit_DropsTheLargestAttributesFirstAndNamesThem()
    {
        // Each large attribute is about 50 KB of values before the snapshot limit applies; the small ones fit easily.
        var big = Enumerable.Range(1, 50).Select(_ => new string('y', 1000)).ToArray();
        var importObject = Person(Text("employeeId", "100482"), Text("memberOf", big), Text("displayName", "Priya Shah"), Text("notes", big));

        var snapshot = ImportedObjectSnapshot.Record(importObject);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Encoding.UTF8.GetByteCount(snapshot.ToJson()), Is.LessThanOrEqualTo(ImportedObjectSnapshot.MaxSnapshotBytes));
            Assert.That(snapshot.Attributes.Select(a => a.Name), Does.Contain("employeeId").And.Contain("displayName"),
                "Small attributes are kept: dropping the largest first keeps the most attributes.");
            Assert.That(snapshot.OmittedAttributeCount, Is.GreaterThanOrEqualTo(1));
            Assert.That(snapshot.OmittedAttributeNames, Is.SubsetOf(new[] { "memberOf", "notes" }));
            Assert.That(snapshot.OmittedAttributeNames, Has.Count.EqualTo(snapshot.OmittedAttributeCount));
        }
    }

    [Test]
    public void Record_ProtectedAttribute_IsKeptWhenOthersAreDroppedForSize()
    {
        var big = Enumerable.Range(1, 50).Select(_ => new string('y', 1000)).ToArray();
        var importObject = Person(Text("employeeId", "100482"), Text("memberOf", big), Text("notes", big));

        var snapshot = ImportedObjectSnapshot.Record(importObject, protectedAttributeNames: ["NOTES"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot.Attributes.Select(a => a.Name), Does.Contain("notes"),
                "An attribute named in the error explains the error, so it is the last thing to go.");
            Assert.That(snapshot.OmittedAttributeNames, Is.EqualTo(new[] { "memberOf" }));
            Assert.That(Encoding.UTF8.GetByteCount(snapshot.ToJson()), Is.LessThanOrEqualTo(ImportedObjectSnapshot.MaxSnapshotBytes));
        }
    }

    [Test]
    public void Record_AttributesNamedInTheError_AreMarkedSo()
    {
        var snapshot = ImportedObjectSnapshot.Record(Person(Text("employeeId", "100482"), Text("Department", "Finance"), Text("department", "Finance Operations")),
            protectedAttributeNames: ["department"]);

        Assert.That(snapshot.Attributes.Select(a => (a.Name, a.NamedInError)), Is.EqualTo(new[]
        {
            ("employeeId", (bool?)null),
            ("Department", (bool?)true),
            ("department", (bool?)true)
        }), "The page highlights what the error is about; a name matched ignoring case is the same attribute.");
    }

    [Test]
    public void Record_ProtectedAttributesAloneOverTheLimit_StillStaysWithinTheLimit()
    {
        var big = Enumerable.Range(1, 50).Select(_ => new string('y', 1000)).ToArray();
        var importObject = Person(Text("a", big), Text("b", big), Text("c", big));

        var snapshot = ImportedObjectSnapshot.Record(importObject, protectedAttributeNames: ["a", "b", "c"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Encoding.UTF8.GetByteCount(snapshot.ToJson()), Is.LessThanOrEqualTo(ImportedObjectSnapshot.MaxSnapshotBytes));
            Assert.That(snapshot.Attributes.Select(a => a.Name), Is.EqualTo(new[] { "a", "b", "c" }),
                "Protected attributes lose values before they lose their place.");
            Assert.That(snapshot.Attributes.Sum(a => a.OmittedValueCount ?? 0), Is.GreaterThan(0));
        }
    }

    [Test]
    public void Record_ManyAttributesOverTheLimit_NamesAtMostTheNameLimitButCountsThemAll()
    {
        var attributes = Enumerable.Range(1, 400).Select(i => Text($"attribute{i:000}", new string('z', 1000))).ToArray();

        var snapshot = ImportedObjectSnapshot.Record(Person(attributes));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot.Attributes.Count + snapshot.OmittedAttributeCount, Is.EqualTo(400));
            Assert.That(snapshot.OmittedAttributeNames!, Has.Count.LessThanOrEqualTo(ImportedObjectSnapshot.MaxOmittedAttributeNames));
            Assert.That(Encoding.UTF8.GetByteCount(snapshot.ToJson()), Is.LessThanOrEqualTo(ImportedObjectSnapshot.MaxSnapshotBytes));
        }
    }

    [Test]
    public void RunLimitReached_RecordsNoAttributesAndSaysWhy()
    {
        var read = ImportedObjectSnapshot.FromJson(ImportedObjectSnapshot.CreateRunLimitReached().ToJson());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(read!.RunLimitReached, Is.True);
            Assert.That(read.Attributes, Is.Empty);
        }
    }

    [Test]
    public void ToJsonThenFromJson_RoundTripsEveryField()
    {
        var values = Enumerable.Range(1, ImportedObjectSnapshot.MaxValuesPerAttribute + 1).Select(i => $"v{i}").ToArray();
        var recorded = ImportedObjectSnapshot.Record(Person(Text("employeeId", "100482"), Text("password", "x"), Text("aliases", values)));

        var read = ImportedObjectSnapshot.FromJson(recorded.ToJson());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(read!.ObjectType, Is.EqualTo("person"));
            Assert.That(read.ChangeType, Is.EqualTo(ObjectChangeType.Added));
            Assert.That(read.RunLimitReached, Is.Null);
            Assert.That(read.Attributes.Select(a => (a.Name, a.Type, a.Values.Count, a.OmittedValueCount, a.Withheld)), Is.EqualTo(new[]
            {
                ("employeeId", AttributeDataType.Text, 1, (int?)null, (bool?)null),
                ("password", AttributeDataType.Text, 0, (int?)null, (bool?)true),
                ("aliases", AttributeDataType.Text, ImportedObjectSnapshot.MaxValuesPerAttribute, (int?)1, (bool?)null)
            }));
        }
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("{ not json")]
    public void FromJson_AbsentOrUnreadable_IsNull(string? json)
    {
        Assert.That(ImportedObjectSnapshot.FromJson(json), Is.Null);
    }
}
