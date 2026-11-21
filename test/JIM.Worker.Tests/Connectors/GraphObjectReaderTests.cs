// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json;
using JIM.Connectors.Graph;
using JIM.Models.Core;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

[TestFixture]
public class GraphObjectReaderTests
{
    private static readonly GraphReadAttribute Id = new("id", AttributeDataType.Text, AttributePlurality.SingleValued, "id");

    [Test]
    public void Read_Id_IsReturnedAndStaged()
    {
        var result = Read("""{"id":"4f1e9d2a-0000-0000-0000-000000000001"}""", Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Id, Is.EqualTo("4f1e9d2a-0000-0000-0000-000000000001"));
            Assert.That(Attribute(result, "id").StringValues, Is.EqualTo(new[] { "4f1e9d2a-0000-0000-0000-000000000001" }));
        }
    }

    [Test]
    public void Read_TextBooleanAndDateTime_AreConverted()
    {
        var result = Read("""{"id":"1","displayName":"Ada Lovelace","accountEnabled":true,"employeeHireDate":"2026-03-01T08:30:00Z"}""",
            Id,
            new("displayName", AttributeDataType.Text, AttributePlurality.SingleValued, "displayName"),
            new("accountEnabled", AttributeDataType.Boolean, AttributePlurality.SingleValued, "accountEnabled"),
            new("employeeHireDate", AttributeDataType.DateTime, AttributePlurality.SingleValued, "employeeHireDate"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Attribute(result, "displayName").StringValues, Is.EqualTo(new[] { "Ada Lovelace" }));
            Assert.That(Attribute(result, "accountEnabled").BoolValues, Is.EqualTo(new[] { true }));
            Assert.That(Attribute(result, "employeeHireDate").DateTimeValues.Single(), Is.EqualTo(new DateTime(2026, 3, 1, 8, 30, 0, DateTimeKind.Utc)));
            Assert.That(Attribute(result, "employeeHireDate").DateTimeValues.Single().Kind, Is.EqualTo(DateTimeKind.Utc));
            Assert.That(result.UnreadableValues, Is.Empty);
        }
    }

    [Test]
    public void Read_MultiValuedText_KeepsEveryValue()
    {
        var result = Read("""{"id":"1","otherMails":["a@example.com","b@example.com"]}""",
            Id, new("otherMails", AttributeDataType.Text, AttributePlurality.MultiValued, "otherMails"));

        Assert.That(Attribute(result, "otherMails").StringValues, Is.EqualTo(new[] { "a@example.com", "b@example.com" }));
    }

    [Test]
    public void Read_ValueInsideAComplexProperty_IsReadFromItsMember()
    {
        var result = Read("""{"id":"1","onPremisesExtensionAttributes":{"extensionAttribute1":"Finance","extensionAttribute2":null}}""",
            Id,
            new("extensionAttribute1", AttributeDataType.Text, AttributePlurality.SingleValued, "onPremisesExtensionAttributes", "extensionAttribute1"),
            new("extensionAttribute2", AttributeDataType.Text, AttributePlurality.SingleValued, "onPremisesExtensionAttributes", "extensionAttribute2"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Attribute(result, "extensionAttribute1").StringValues, Is.EqualTo(new[] { "Finance" }));
            Assert.That(result.Attributes.Select(a => a.Name), Does.Not.Contain("extensionAttribute2"));
        }
    }

    [Test]
    public void Read_NullMissingOrEmpty_StagesNoValue()
    {
        // An attribute with no value is left out, so JIM reads it as cleared rather than as an empty string.
        var result = Read("""{"id":"1","jobTitle":null,"businessPhones":[],"department":""}""",
            Id,
            new("jobTitle", AttributeDataType.Text, AttributePlurality.SingleValued, "jobTitle"),
            new("businessPhones", AttributeDataType.Text, AttributePlurality.MultiValued, "businessPhones"),
            new("department", AttributeDataType.Text, AttributePlurality.SingleValued, "department"),
            new("surname", AttributeDataType.Text, AttributePlurality.SingleValued, "surname"));

        Assert.That(result.Attributes.Select(a => a.Name), Is.EqualTo(new[] { "id" }));
    }

    [Test]
    public void Read_NumberLongNumberAndBinary_AreConverted()
    {
        var result = Read("""{"id":"1","extension_a_level":7,"extension_a_badge":9007199254740993,"extension_a_photo":"AQID"}""",
            Id,
            new("extension_a_level", AttributeDataType.Number, AttributePlurality.SingleValued, "extension_a_level"),
            new("extension_a_badge", AttributeDataType.LongNumber, AttributePlurality.SingleValued, "extension_a_badge"),
            new("extension_a_photo", AttributeDataType.Binary, AttributePlurality.SingleValued, "extension_a_photo"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Attribute(result, "extension_a_level").IntValues, Is.EqualTo(new[] { 7 }));
            Assert.That(Attribute(result, "extension_a_badge").LongValues, Is.EqualTo(new[] { 9007199254740993L }));
            Assert.That(Attribute(result, "extension_a_photo").ByteValues.Single(), Is.EqualTo(new byte[] { 1, 2, 3 }));
        }
    }

    [TestCase("""{"id":"1","accountEnabled":"yes"}""", "accountEnabled", AttributeDataType.Boolean)]
    [TestCase("""{"id":"1","employeeHireDate":"not a date"}""", "employeeHireDate", AttributeDataType.DateTime)]
    [TestCase("""{"id":"1","level":"seven"}""", "level", AttributeDataType.Number)]
    [TestCase("""{"id":"1","level":3000000000}""", "level", AttributeDataType.Number)]
    [TestCase("""{"id":"1","photo":"!!not base64!!"}""", "photo", AttributeDataType.Binary)]
    public void Read_ValueOfTheWrongType_IsReportedAndTheRestRead(string json, string name, AttributeDataType type)
    {
        var result = Read(json, Id, new(name, type, AttributePlurality.SingleValued, name));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Id, Is.EqualTo("1"));
            Assert.That(result.Attributes.Select(a => a.Name), Is.EqualTo(new[] { "id" }));
            Assert.That(result.UnreadableValues.Single().AttributeName, Is.EqualTo(name));
            Assert.That(result.UnreadableValues.Single().Reason, Is.Not.Empty);
        }
    }

    [Test]
    public void Read_SeveralValuesForASingleValuedAttribute_IsReported()
    {
        var result = Read("""{"id":"1","businessPhones":["+44 1","+44 2"]}""",
            Id, new("businessPhones", AttributeDataType.Text, AttributePlurality.SingleValued, "businessPhones"));

        Assert.That(result.UnreadableValues.Single().AttributeName, Is.EqualTo("businessPhones"));
    }

    [Test]
    public void Read_OneValueInAnArrayForASingleValuedAttribute_IsTaken()
    {
        var result = Read("""{"id":"1","businessPhones":["+44 1"]}""",
            Id, new("businessPhones", AttributeDataType.Text, AttributePlurality.SingleValued, "businessPhones"));

        Assert.That(Attribute(result, "businessPhones").StringValues, Is.EqualTo(new[] { "+44 1" }));
    }

    [Test]
    public void Read_NoId_ReturnsNoId()
    {
        Assert.That(Read("""{"displayName":"x"}""", Id).Id, Is.Null);
    }

    private static GraphObjectReadResult Read(string json, params GraphReadAttribute[] attributes)
    {
        using var document = JsonDocument.Parse(json);
        return GraphObjectReader.Read(document.RootElement, attributes);
    }

    private static ConnectedSystemImportObjectAttribute Attribute(GraphObjectReadResult result, string name)
    {
        var attribute = result.Attributes.SingleOrDefault(a => a.Name == name);
        Assert.That(attribute, Is.Not.Null, $"Expected the attribute '{name}' to be read.");
        return attribute!;
    }
}
