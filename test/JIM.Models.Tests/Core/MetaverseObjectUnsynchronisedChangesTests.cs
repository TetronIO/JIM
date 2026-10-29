// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using JIM.Models.Core;
using NUnit.Framework;

namespace JIM.Models.Tests.Core;

/// <summary>
/// The record of what a write outside synchronisation changed on a Metaverse Object (#2615): what it keeps, what it
/// leaves out, and that it survives the trip to and from the stored document.
/// </summary>
[TestFixture]
public class MetaverseObjectUnsynchronisedChangesTests
{
    private const int Email = 7;
    private const int Aliases = 9;
    private const int Manager = 11;

    private static MetaverseObjectAttributeValue Value(int attributeId, string? text = null, Guid? reference = null) =>
        new() { Id = Guid.NewGuid(), AttributeId = attributeId, StringValue = text, ReferenceValueId = reference };

    [Test]
    public void Record_AdditionsOnly_RecordsTheirAttributesAndNoValues()
    {
        var changes = new MetaverseObjectUnsynchronisedChanges();

        changes.Record([Value(Email, "jay@panoply.local"), Value(Aliases, "j@panoply.local")], null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes.AttributeIds, Is.EqualTo(new[] { Email, Aliases }));
            Assert.That(changes.Removals, Is.Empty, "an added value is read from the object when the change is delivered");
        }
    }

    [Test]
    public void Record_Removals_RecordsTheirAttributesAndTheirValues()
    {
        var changes = new MetaverseObjectUnsynchronisedChanges();
        var managerId = Guid.NewGuid();

        changes.Record(null, [Value(Email, "old@panoply.local"), Value(Manager, reference: managerId)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes.AttributeIds, Is.EqualTo(new[] { Email, Manager }));
            Assert.That(changes.Removals.Select(r => (r.AttributeId, r.StringValue, r.ReferenceValueId)),
                Is.EqualTo(new (int, string?, Guid?)[] { (Email, "old@panoply.local", null), (Manager, null, managerId) }));
        }
    }

    [Test]
    public void Record_AttributeNamedByNavigationOnly_UsesTheAttributesId()
    {
        // A value built by a caller names its attribute by navigation; the foreign key is only set once it is saved.
        var changes = new MetaverseObjectUnsynchronisedChanges();

        changes.Record([new MetaverseObjectAttributeValue { Attribute = new MetaverseAttribute { Id = Email }, StringValue = "jay@panoply.local" }], null);

        Assert.That(changes.AttributeIds, Is.EqualTo(new[] { Email }));
    }

    [Test]
    public void Record_ValuesWithNoContent_RecordsTheAttributeButNoRemoval()
    {
        // An asserted-null marker, and a binary value (which export staging cannot match by content), have no value to
        // match by; recording one would make it match every valueless row of the attribute.
        var changes = new MetaverseObjectUnsynchronisedChanges();
        var marker = new MetaverseObjectAttributeValue { AttributeId = Email, NullValue = true };
        var binary = new MetaverseObjectAttributeValue { AttributeId = Aliases, ByteValue = [1, 2, 3] };

        changes.Record(null, [marker, binary]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes.AttributeIds, Is.EquivalentTo(new[] { Email, Aliases }));
            Assert.That(changes.Removals, Is.Empty);
        }
    }

    [Test]
    public void Record_SecondWrite_MergesWithTheFirst()
    {
        var changes = new MetaverseObjectUnsynchronisedChanges();
        changes.Record([Value(Email, "new@panoply.local")], [Value(Email, "old@panoply.local")]);

        changes.Record([Value(Aliases, "j@panoply.local")], [Value(Aliases, "k@panoply.local"), Value(Email, "old@panoply.local")]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes.AttributeIds, Is.EqualTo(new[] { Email, Aliases }), "unioned, each once");
            Assert.That(changes.Removals.Select(r => r.StringValue), Is.EqualTo(new[] { "old@panoply.local", "k@panoply.local" }),
                "appended, each distinct value once");
        }
    }

    [Test]
    public void ToJson_ThenFromJson_RoundTripsEveryValueKind()
    {
        var date = new DateTime(2026, 10, 29, 9, 30, 15, DateTimeKind.Utc).AddTicks(1230);
        var changes = new MetaverseObjectUnsynchronisedChanges();
        var removals = new List<MetaverseObjectAttributeValue>
        {
            new() { AttributeId = 1, StringValue = "text" },
            new() { AttributeId = 2, IntValue = 42 },
            new() { AttributeId = 3, LongValue = long.MaxValue },
            new() { AttributeId = 4, DecimalValue = 12.3456m },
            new() { AttributeId = 5, GuidValue = Guid.NewGuid() },
            new() { AttributeId = 6, BoolValue = false },
            new() { AttributeId = 7, DateTimeValue = date },
            new() { AttributeId = 8, ReferenceValueId = Guid.NewGuid() }
        };
        changes.Record(null, removals);

        var read = MetaverseObjectUnsynchronisedChanges.FromJson(changes.ToJson());

        Assert.That(read, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(read!.AttributeIds, Is.EqualTo(changes.AttributeIds));
            Assert.That(read.Removals, Is.EqualTo(changes.Removals), "every typed field survives, a false Boolean and a date to the tick included");
        }
    }

    [Test]
    public void ToJson_Removal_StoresOnlyTheRecordedValueFields()
    {
        // A convenience on the record (whether it has a value) is not part of the stored document.
        var changes = new MetaverseObjectUnsynchronisedChanges();
        changes.Record(null, [Value(Email, "old@panoply.local")]);

        using var document = JsonDocument.Parse(changes.ToJson());

        var removal = document.RootElement.GetProperty("removals")[0];
        Assert.That(removal.EnumerateObject().Select(p => p.Name), Is.EquivalentTo(new[] { "attributeId", "stringValue" }),
            "nulls are omitted, and nothing derived is stored");
    }

    [Test]
    public void FromJson_NothingStored_ReturnsNull()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(MetaverseObjectUnsynchronisedChanges.FromJson(null), Is.Null);
            Assert.That(MetaverseObjectUnsynchronisedChanges.FromJson(""), Is.Null);
            Assert.That(MetaverseObjectUnsynchronisedChanges.FromJson("   "), Is.Null);
        }
    }

    [Test]
    public void FromJson_NotAValidDocument_Throws()
    {
        Assert.That(() => MetaverseObjectUnsynchronisedChanges.FromJson("{ not json"), Throws.InstanceOf<JsonException>());
    }

    [Test]
    public void ToAttributeValue_Removal_IsAnUnsavedValueWithTheRecordedFields()
    {
        var referenced = Guid.NewGuid();
        var removal = new MetaverseObjectUnsynchronisedRemoval { AttributeId = Manager, ReferenceValueId = referenced };

        var value = removal.ToAttributeValue();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(value.Id, Is.EqualTo(Guid.Empty), "export staging matches an unsaved value by reference and content");
            Assert.That(value.AttributeId, Is.EqualTo(Manager));
            Assert.That(value.ReferenceValueId, Is.EqualTo(referenced));
        }
    }
}
