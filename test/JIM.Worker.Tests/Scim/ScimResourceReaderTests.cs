// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text;
using System.Text.Json;
using JIM.Models.Core;
using JIM.Scim;
using JIM.Scim.Discovery;
using JIM.Scim.Schema;

namespace JIM.Worker.Tests.Scim;

/// <summary>
/// Reading a SCIM resource into JIM attribute values. The reader and
/// <see cref="ScimAttributeMapper"/> have to agree: an attribute the mapper publishes but the reader
/// cannot find is an Attribute Flow target that silently never receives a value, so these tests read
/// through schema definitions rather than hand-built accessors.
/// </summary>
public class ScimResourceReaderTests
{
    private static JsonElement Resource(string json)
    {
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    /// <summary>
    /// The flattened core User schema plus the common attributes, exactly as discovery would build it.
    /// </summary>
    private static List<ScimFlattenedAttribute> UserSchema()
    {
        var attributes = ScimCommonAttributes.For(ScimUrns.User);
        attributes.AddRange(ScimAttributeMapper.FlattenSchema(ScimCoreSchemas.User()));
        attributes.AddRange(ScimAttributeMapper.FlattenSchema(ScimCoreSchemas.EnterpriseUser(), "enterpriseUser"));
        return attributes;
    }

    private static ScimResourceReadResult ReadUser(string json)
    {
        return ScimResourceReader.Read(Resource(json), UserSchema());
    }

    #region simple values and common attributes
    [Test]
    public void Read_CommonAttributes_AreStaged()
    {
        var result = ReadUser("""
        {
          "id": "2819c223-7f76-453a-919d-413861904646",
          "externalId": "701984",
          "userName": "bjensen@example.com",
          "meta": { "resourceType": "User", "lastModified": "2026-02-04T13:53:42Z", "version": "W/\"a330bc54f0671c9\"" }
        }
        """);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Attributes.Single(a => a.Name == "id").StringValues, Is.EqualTo(new[] { "2819c223-7f76-453a-919d-413861904646" }));
            Assert.That(result.Attributes.Single(a => a.Name == "externalId").StringValues, Is.EqualTo(new[] { "701984" }));
            Assert.That(result.Attributes.Single(a => a.Name == "userName").StringValues, Is.EqualTo(new[] { "bjensen@example.com" }));
            Assert.That(result.Attributes.Single(a => a.Name == "meta.version").StringValues, Is.EqualTo(new[] { "W/\"a330bc54f0671c9\"" }));
        }
    }

    [Test]
    public void Read_MetaLastModified_IsNormalisedToUtc()
    {
        // JIM stores every date and time in UTC; a provider is free to send an offset.
        var result = ReadUser("""{ "id": "1", "meta": { "lastModified": "2026-02-04T15:53:42+02:00" } }""");

        Assert.That(result.Attributes.Single(a => a.Name == "meta.lastModified").DateTimeValue,
            Is.EqualTo(new DateTime(2026, 2, 4, 13, 53, 42, DateTimeKind.Utc)));
    }

    [Test]
    public void Read_AttributeAbsentFromTheResource_IsNotStaged()
    {
        // An attribute the provider did not send is unasserted, not empty. Staging it empty would look
        // like a deletion to synchronisation.
        var result = ReadUser("""{ "id": "1", "userName": "bjensen" }""");

        Assert.That(result.Attributes.Select(a => a.Name), Does.Not.Contain("displayName"));
    }

    [Test]
    public void Read_AttributeSentAsNull_IsNotStaged()
    {
        var result = ReadUser("""{ "id": "1", "displayName": null }""");

        Assert.That(result.Attributes.Select(a => a.Name), Does.Not.Contain("displayName"));
    }

    [Test]
    public void Read_BooleanAttribute_IsStagedAsABoolean()
    {
        var result = ReadUser("""{ "id": "1", "active": true }""");

        Assert.That(result.Attributes.Single(a => a.Name == "active").BoolValue, Is.True);
    }

    [Test]
    public void Read_BooleanSentAsAString_IsStillUnderstood()
    {
        // Providers do this. Rejecting it would lose the account-enabled state, which is the single most
        // consequential attribute JIM synchronises.
        var result = ReadUser("""{ "id": "1", "active": "false" }""");

        Assert.That(result.Attributes.Single(a => a.Name == "active").BoolValue, Is.False);
    }

    [Test]
    public void Read_MixedCaseAttributeNames_AreMatched()
    {
        // RFC 7643 section 2.1: attribute names are case insensitive.
        var result = ReadUser("""{ "ID": "1", "UserName": "bjensen" }""");

        Assert.That(result.Attributes.Select(a => a.Name), Is.SupersetOf(new[] { "id", "userName" }));
    }

    [Test]
    public void Read_ResourceThatIsNotAnObject_IsRejected()
    {
        var result = ScimResourceReader.Read(Resource("""[ "not a resource" ]"""), UserSchema());

        Assert.That(result.Error, Is.Not.Null);
    }
    #endregion

    #region complex and canonical values
    [Test]
    public void Read_ComplexSubAttributes_AreStagedUnderTheirDottedNames()
    {
        var result = ReadUser("""
        { "id": "1", "name": { "givenName": "Barbara", "familyName": "Jensen", "formatted": "Ms. Barbara J Jensen" } }
        """);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Attributes.Single(a => a.Name == "name.givenName").StringValues, Is.EqualTo(new[] { "Barbara" }));
            Assert.That(result.Attributes.Single(a => a.Name == "name.familyName").StringValues, Is.EqualTo(new[] { "Jensen" }));
        }
    }

    [Test]
    public void Read_CanonicallyTypedEntries_LandInTheirMatchingSlots()
    {
        var result = ReadUser("""
        { "id": "1", "emails": [
            { "value": "work@example.com", "type": "work", "primary": true },
            { "value": "home@example.com", "type": "home" } ] }
        """);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Attributes.Single(a => a.Name == "emails.work").StringValues, Is.EqualTo(new[] { "work@example.com" }));
            Assert.That(result.Attributes.Single(a => a.Name == "emails.home").StringValues, Is.EqualTo(new[] { "home@example.com" }));
            Assert.That(result.Attributes.Single(a => a.Name == "emails.primary").StringValues, Is.EqualTo(new[] { "work@example.com" }));
        }
    }

    [Test]
    public void Read_CanonicalTypeInDifferentCase_StillMatchesItsSlot()
    {
        var result = ReadUser("""{ "id": "1", "emails": [ { "value": "work@example.com", "type": "Work" } ] }""");

        Assert.That(result.Attributes.Single(a => a.Name == "emails.work").StringValues, Is.EqualTo(new[] { "work@example.com" }));
    }

    [Test]
    public void Read_TwoEntriesSharingACanonicalType_ImportsTheFirstAndWarns()
    {
        // The slot holds one value by design, so the second entry is data it cannot take. Reporting it
        // is the difference between a known gap and silent divergence.
        var result = ReadUser("""
        { "id": "1", "emails": [
            { "value": "first@example.com", "type": "work" },
            { "value": "second@example.com", "type": "work" } ] }
        """);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Attributes.Single(a => a.Name == "emails.work").StringValues, Is.EqualTo(new[] { "first@example.com" }));
            Assert.That(result.Warnings, Has.Exactly(1).Contains("emails"));
        }
    }

    [Test]
    public void Read_NoEntryOfACanonicalType_LeavesThatSlotUnstaged()
    {
        var result = ReadUser("""{ "id": "1", "emails": [ { "value": "work@example.com", "type": "work" } ] }""");

        Assert.That(result.Attributes.Select(a => a.Name), Does.Not.Contain("emails.home"));
    }

    [Test]
    public void Read_NoPrimaryEntry_LeavesThePrimarySlotUnstaged()
    {
        var result = ReadUser("""{ "id": "1", "emails": [ { "value": "work@example.com", "type": "work" } ] }""");

        Assert.That(result.Attributes.Select(a => a.Name), Does.Not.Contain("emails.primary"));
    }

    [Test]
    public void Read_AddressesWhichHaveNoSingleValue_StageEachSubAttributeOfTheMatchingEntry()
    {
        var result = ReadUser("""
        { "id": "1", "addresses": [
            { "streetAddress": "100 Universal City Plaza", "locality": "Hollywood", "postalCode": "91608", "type": "work" } ] }
        """);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Attributes.Single(a => a.Name == "addresses.work.streetAddress").StringValues, Is.EqualTo(new[] { "100 Universal City Plaza" }));
            Assert.That(result.Attributes.Single(a => a.Name == "addresses.work.locality").StringValues, Is.EqualTo(new[] { "Hollywood" }));
        }
    }

    [Test]
    public void Read_MultiValuedComplexWithoutCanonicalTypes_StagesEverySubAttributeValue()
    {
        var result = ReadUser("""
        { "id": "1", "roles": [ { "value": "Auditor" }, { "value": "Approver" } ] }
        """);

        Assert.That(result.Attributes.Single(a => a.Name == "roles.value").StringValues,
            Is.EqualTo(new[] { "Auditor", "Approver" }));
    }

    [Test]
    public void Read_ComplexAttributeSentAsAnObjectWhereTheSchemaSaysMultiValued_IsStillRead()
    {
        // Providers do send a bare object where the schema says a list, and the reverse. Refusing either
        // shape would lose the value over a formatting difference.
        var result = ReadUser("""{ "id": "1", "emails": { "value": "work@example.com", "type": "work" } }""");

        Assert.That(result.Attributes.Single(a => a.Name == "emails.work").StringValues, Is.EqualTo(new[] { "work@example.com" }));
    }
    #endregion

    #region references
    [Test]
    public void Read_ReferenceCollection_StagesTheReferencedIdentifiers()
    {
        var groupSchema = ScimCommonAttributes.For(ScimUrns.Group);
        groupSchema.AddRange(ScimAttributeMapper.FlattenSchema(ScimCoreSchemas.Group()));

        var result = ScimResourceReader.Read(Resource("""
        { "id": "g1", "displayName": "Engineering", "members": [
            { "value": "u1", "$ref": "https://example.com/scim/v2/Users/u1", "type": "User" },
            { "value": "u2", "$ref": "https://example.com/scim/v2/Users/u2", "type": "User" } ] }
        """), groupSchema);

        var members = result.Attributes.Single(a => a.Name == "members");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(members.Type, Is.EqualTo(AttributeDataType.Reference));
            Assert.That(members.ReferenceValues, Is.EqualTo(new[] { "u1", "u2" }));
        }
    }

    [Test]
    public void Read_ReferenceWithNoValueSubAttribute_FallsBackToTheUri()
    {
        // The id is preferred because that is what JIM resolves against, but a provider that sends only
        // $ref still carries a usable reference.
        var groupSchema = ScimAttributeMapper.FlattenSchema(ScimCoreSchemas.Group());

        var result = ScimResourceReader.Read(Resource("""
        { "id": "g1", "members": [ { "$ref": "https://example.com/scim/v2/Users/u1" } ] }
        """), groupSchema);

        Assert.That(result.Attributes.Single(a => a.Name == "members").ReferenceValues,
            Is.EqualTo(new[] { "https://example.com/scim/v2/Users/u1" }));
    }

    [Test]
    public void Read_SingleValuedReference_StagesOneIdentifier()
    {
        var result = ReadUser($$"""
        { "id": "1", "{{ScimUrns.EnterpriseUser}}": { "manager": { "value": "boss1", "displayName": "The Boss" } } }
        """);

        Assert.That(result.Attributes.Single(a => a.Name == "enterpriseUser.manager").ReferenceValues, Is.EqualTo(new[] { "boss1" }));
    }
    #endregion

    #region extensions
    [Test]
    public void Read_ExtensionAttribute_IsReadFromInsideItsUrnMember()
    {
        var result = ReadUser($$"""
        { "id": "1", "{{ScimUrns.EnterpriseUser}}": { "department": "Tour Operations", "employeeNumber": "701984" } }
        """);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Attributes.Single(a => a.Name == "enterpriseUser.department").StringValues, Is.EqualTo(new[] { "Tour Operations" }));
            Assert.That(result.Attributes.Single(a => a.Name == "enterpriseUser.employeeNumber").StringValues, Is.EqualTo(new[] { "701984" }));
        }
    }

    [Test]
    public void Read_ExtensionMemberAbsent_StagesNoExtensionAttributes()
    {
        var result = ReadUser("""{ "id": "1", "userName": "bjensen" }""");

        Assert.That(result.Attributes.Select(a => a.Name), Has.None.StartsWith("enterpriseUser."));
    }

    [Test]
    public void Read_ExtensionAttributeAtTheTopLevel_IsNotMistakenForACoreAttribute()
    {
        // A department sitting outside its URN member is not the extension attribute, and reading it as
        // one would let a provider bug flow through as real data.
        var result = ReadUser("""{ "id": "1", "department": "Tour Operations" }""");

        Assert.That(result.Attributes.Select(a => a.Name), Does.Not.Contain("enterpriseUser.department"));
    }
    #endregion

    #region typed values
    private static List<ScimFlattenedAttribute> TypedSchema(string scimType, bool multiValued = false)
    {
        var schema = new ScimSchema
        {
            Id = "urn:example:schemas:Typed",
            Name = "Typed",
            Attributes = [new ScimSchemaAttribute { Name = "field", Type = scimType, MultiValued = multiValued }]
        };
        return ScimAttributeMapper.FlattenSchema(schema);
    }

    [Test]
    public void Read_IntegerAttribute_IsStagedAsALongSoLargeValuesSurvive()
    {
        var result = ScimResourceReader.Read(Resource("""{ "field": 9007199254740993 }"""), TypedSchema(ScimAttributeTypes.Integer));

        Assert.That(result.Attributes.Single().LongValues, Is.EqualTo(new[] { 9007199254740993L }));
    }

    [Test]
    public void Read_DecimalAttribute_KeepsItsFullPrecision()
    {
        // Routing the value through double would silently round it; the whole point of the Decimal type.
        var result = ScimResourceReader.Read(Resource("""{ "field": 12345678901234567890.12345 }"""), TypedSchema(ScimAttributeTypes.Decimal));

        Assert.That(result.Attributes.Single().DecimalValues, Is.EqualTo(new[] { 12345678901234567890.12345m }));
    }

    [Test]
    public void Read_DecimalInExponentNotation_IsAccepted()
    {
        var result = ScimResourceReader.Read(Resource("""{ "field": "1.5e3" }"""), TypedSchema(ScimAttributeTypes.Decimal));

        Assert.That(result.Attributes.Single().DecimalValues, Is.EqualTo(new[] { 1500m }));
    }

    [Test]
    public void Read_DecimalBeyondWhatJimCanHold_FailsTheObjectRatherThanRounding()
    {
        // Rounding would corrupt the value with nothing to show for it. Synchronisation integrity
        // outranks importing the object at all.
        var result = ScimResourceReader.Read(Resource("""{ "field": "1e40" }"""), TypedSchema(ScimAttributeTypes.Decimal));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Error, Is.Not.Null);
            Assert.That(result.Attributes, Is.Empty);
        }
    }

    [Test]
    public void Read_BinaryAttribute_IsBase64Decoded()
    {
        var encoded = System.Convert.ToBase64String(Encoding.UTF8.GetBytes("certificate bytes"));

        var result = ScimResourceReader.Read(Resource($$"""{ "field": "{{encoded}}" }"""), TypedSchema(ScimAttributeTypes.Binary));

        Assert.That(Encoding.UTF8.GetString(result.Attributes.Single().ByteValues.Single()), Is.EqualTo("certificate bytes"));
    }

    [Test]
    public void Read_BinaryAttributeThatIsNotBase64_IsSkippedRatherThanStagedAsRubbish()
    {
        var result = ScimResourceReader.Read(Resource("""{ "field": "not base 64 !!" }"""), TypedSchema(ScimAttributeTypes.Binary));

        Assert.That(result.Attributes, Is.Empty);
    }

    [Test]
    public void Read_MultiValuedSimpleAttribute_StagesEveryValue()
    {
        var result = ScimResourceReader.Read(Resource("""{ "field": [ "one", "two", "three" ] }"""),
            TypedSchema(ScimAttributeTypes.String, multiValued: true));

        Assert.That(result.Attributes.Single().StringValues, Is.EqualTo(new[] { "one", "two", "three" }));
    }

    [Test]
    public void Read_NumberSentForAStringAttribute_IsStagedAsItsText()
    {
        // A schema-typed string attribute holding a number is a provider quirk, not a reason to lose it.
        var result = ScimResourceReader.Read(Resource("""{ "field": 42 }"""), TypedSchema(ScimAttributeTypes.String));

        Assert.That(result.Attributes.Single().StringValues, Is.EqualTo(new[] { "42" }));
    }
    #endregion
}
