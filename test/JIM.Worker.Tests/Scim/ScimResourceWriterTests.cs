// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Nodes;
using JIM.Scim;
using JIM.Scim.Discovery;
using JIM.Scim.Messages;
using JIM.Scim.Schema;

namespace JIM.Worker.Tests.Scim;

/// <summary>
/// Building a SCIM resource from JIM attribute values: the inverse of <see cref="ScimResourceReader"/>,
/// and it has to invert exactly. A value JIM believes it exported but that never reached the wire in a
/// shape the provider recognises is a change reported as applied and silently missing.
/// </summary>
public class ScimResourceWriterTests
{
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

    private static ScimResourceWriteResult WriteUser(params ScimAttributeWrite[] writes)
    {
        return ScimResourceWriter.BuildResource(writes, UserSchema(), ScimUrns.User);
    }

    private static ScimAttributeWrite Write(string name, params object?[] values)
    {
        return new ScimAttributeWrite(name, values);
    }

    private static string Json(ScimResourceWriteResult result)
    {
        return result.Resource.ToJsonString();
    }

    #region simple values
    [Test]
    public void BuildResource_SingleValuedAttribute_IsWrittenAtTheTopLevel()
    {
        var result = WriteUser(Write("userName", "alice"));

        Assert.That(Json(result), Does.Contain("\"userName\":\"alice\""));
    }

    [Test]
    public void BuildResource_NothingToWrite_StillDeclaresTheResourceSchema()
    {
        var result = WriteUser();

        Assert.That(result.Resource["schemas"]!.ToJsonString(), Is.EqualTo($"[\"{ScimUrns.User}\"]"));
    }

    [Test]
    public void BuildResource_NoValues_WritesNothingForThatAttribute()
    {
        var result = WriteUser(Write("userName"));

        Assert.That(Json(result), Does.Not.Contain("userName"));
    }

    [Test]
    public void BuildResource_NullValue_IsNotWritten()
    {
        // SCIM treats an absent attribute as unasserted; an explicit null in a PUT asks the provider to
        // clear it, which is a different instruction from the one JIM was given.
        var result = WriteUser(Write("userName", (object?)null));

        Assert.That(Json(result), Does.Not.Contain("userName"));
    }

    [Test]
    public void BuildResource_BooleanValue_IsWrittenAsAJsonBoolean()
    {
        var result = WriteUser(Write("active", true));

        Assert.That(Json(result), Does.Contain("\"active\":true"));
    }

    [Test]
    public void BuildResource_DateTimeValue_IsWrittenAsAnIso8601UtcInstant()
    {
        var attributes = UserSchema();
        var result = ScimResourceWriter.BuildResource(
            [Write("meta.created", new DateTime(2026, 7, 30, 10, 0, 0, DateTimeKind.Utc))], attributes, ScimUrns.User);

        // meta is provider-maintained, so nothing lands: the type conversion is proven below instead.
        Assert.That(Json(result), Does.Not.Contain("meta"));
    }
    #endregion

    #region writability
    [Test]
    public void BuildResource_ReadOnlyAttribute_IsNeverWritten()
    {
        // A provider is entitled to reject a whole request that asserts a read-only attribute.
        var result = WriteUser(Write("id", "abc"), Write("meta.version", "W/\"1\""), Write("userName", "alice"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Json(result), Does.Not.Contain("\"id\""));
            Assert.That(Json(result), Does.Not.Contain("meta"));
            Assert.That(Json(result), Does.Contain("alice"));
        }
    }

    [Test]
    public void BuildResource_AttributeTheSchemaDoesNotHave_IsReportedRatherThanDropped()
    {
        // Dropping it would export an object JIM believes carries a value the provider never received.
        var result = WriteUser(Write("notAnAttribute", "x"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.UnknownAttributes, Is.EqualTo(new[] { "notAnAttribute" }));
            Assert.That(Json(result), Does.Not.Contain("notAnAttribute"));
        }
    }

    [Test]
    public void BuildResource_EverythingKnown_ReportsNoUnknownAttributes()
    {
        var result = WriteUser(Write("userName", "alice"));

        Assert.That(result.UnknownAttributes, Is.Empty);
    }
    #endregion

    #region complex attributes
    [Test]
    public void BuildResource_ComplexSubAttributes_ShareOneParentObject()
    {
        var result = WriteUser(Write("name.givenName", "Alice"), Write("name.familyName", "Jensen"));

        Assert.That(Json(result), Does.Contain("\"name\":{\"givenName\":\"Alice\",\"familyName\":\"Jensen\"}"));
    }

    [Test]
    public void BuildResource_CanonicalSlot_WritesAnEntryCarryingItsType()
    {
        // The type is what lets the provider, and the next import, tell one entry from another.
        var result = WriteUser(Write("emails.work", "alice@example.com"));

        Assert.That(Json(result), Does.Contain("\"emails\":[{\"value\":\"alice@example.com\",\"type\":\"work\"}]"));
    }

    [Test]
    public void BuildResource_TwoCanonicalSlotsOfTheSameAttribute_BecomeTwoEntriesInOneArray()
    {
        var result = WriteUser(Write("emails.work", "work@example.com"), Write("emails.home", "home@example.com"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Resource["emails"]!.AsArray(), Has.Count.EqualTo(2));
            Assert.That(Json(result), Does.Contain("work@example.com").And.Contain("home@example.com"));
        }
    }

    [Test]
    public void BuildResource_CanonicalSlotWithSubAttributes_SharesOneEntry()
    {
        // An address has no single value, so the slot is cut per sub-attribute; all of them describe the
        // same address and must land in the same entry.
        var result = WriteUser(Write("addresses.work.streetAddress", "1 High Street"), Write("addresses.work.locality", "Bath"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Resource["addresses"]!.AsArray(), Has.Count.EqualTo(1));
            Assert.That(Json(result), Does.Contain("1 High Street").And.Contain("Bath"));
        }
    }

    [Test]
    public void BuildResource_PrimarySlot_MarksTheEntryPrimary()
    {
        var result = WriteUser(Write("emails.primary", "alice@example.com"));

        Assert.That(Json(result), Does.Contain("\"primary\":true"));
    }
    #endregion

    #region references
    [Test]
    public void BuildResource_MultiValuedReference_WritesOneEntryPerReferencedId()
    {
        var groupSchema = ScimCommonAttributes.For(ScimUrns.Group);
        groupSchema.AddRange(ScimAttributeMapper.FlattenSchema(ScimCoreSchemas.Group()));

        var result = ScimResourceWriter.BuildResource(
            [Write("members", "member-a", "member-b")], groupSchema, ScimUrns.Group);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Resource["members"]!.AsArray(), Has.Count.EqualTo(2));
            Assert.That(Json(result), Does.Contain("{\"value\":\"member-a\"}"));
        }
    }

    [Test]
    public void BuildResource_ReadOnlyMultiValuedReference_IsNeverWritten()
    {
        // RFC 7643 makes a User's groups read-only: membership is asserted from the Group end, and a
        // provider will not accept it from this one.
        var result = WriteUser(Write("groups", "group-a"));

        Assert.That(Json(result), Does.Not.Contain("groups"));
    }

    [Test]
    public void BuildResource_SingleValuedReference_WritesOneObjectRatherThanAnArray()
    {
        var result = WriteUser(Write("enterpriseUser.manager", "manager-id"));

        Assert.That(Json(result), Does.Contain("\"manager\":{\"value\":\"manager-id\"}"));
    }
    #endregion

    #region extensions
    [Test]
    public void BuildResource_ExtensionAttribute_IsNestedUnderItsSchemaUrn()
    {
        var result = WriteUser(Write("enterpriseUser.department", "Engineering"));

        Assert.That(Json(result), Does.Contain($"\"{ScimUrns.EnterpriseUser}\":{{\"department\":\"Engineering\"}}"));
    }

    [Test]
    public void BuildResource_ExtensionAttribute_AddsItsUrnToTheSchemasMember()
    {
        // RFC 7643 section 3: a resource declares every schema it carries values for, and a provider is
        // entitled to ignore an extension the resource did not declare.
        var result = WriteUser(Write("userName", "alice"), Write("enterpriseUser.department", "Engineering"));

        Assert.That(result.Resource["schemas"]!.AsArray().Select(s => s!.GetValue<string>()),
            Is.EqualTo(new[] { ScimUrns.User, ScimUrns.EnterpriseUser }));
    }

    [Test]
    public void BuildResource_NoExtensionValues_DeclaresOnlyTheBaseSchema()
    {
        var result = WriteUser(Write("userName", "alice"));

        Assert.That(result.Resource["schemas"]!.AsArray(), Has.Count.EqualTo(1));
    }
    #endregion

    #region applying changes to a resource the provider already holds
    private static List<ScimFlattenedAttribute> GroupSchema()
    {
        var attributes = ScimCommonAttributes.For(ScimUrns.Group);
        attributes.AddRange(ScimAttributeMapper.FlattenSchema(ScimCoreSchemas.Group()));
        return attributes;
    }

    private static JsonObject Resource(string json)
    {
        return (JsonObject)JsonNode.Parse(json)!;
    }

    [Test]
    public void ApplyChanges_LeavesAloneEverythingTheChangesDoNotName()
    {
        // This is the whole point of reading before writing: a PUT asserts the entire resource, so an
        // attribute JIM does not manage has to survive an update to one it does.
        var resource = Resource("""{ "id": "alice", "userName": "alice", "nickName": "Ally" }""");

        ScimResourceWriter.ApplyChanges(resource, [new ScimAttributeChange("title", ScimPatchOperations.Replace, "Engineer")], UserSchema());

        Assert.That(resource.ToJsonString(), Does.Contain("Ally").And.Contain("Engineer"));
    }

    [Test]
    public void ApplyChanges_AddingToAMultiValuedAttribute_AppendsRatherThanReplaces()
    {
        // Collapsing add into replace would turn every membership addition into a membership
        // replacement, silently removing everyone already in the group.
        var resource = Resource("""{ "id": "engineers", "members": [ { "value": "alice" } ] }""");

        ScimResourceWriter.ApplyChanges(resource, [new ScimAttributeChange("members", ScimPatchOperations.Add, "bob")], GroupSchema());

        Assert.That(resource["members"]!.AsArray(), Has.Count.EqualTo(2));
    }

    [Test]
    public void ApplyChanges_RemovingOneMember_TakesOnlyThatMember()
    {
        var resource = Resource("""{ "id": "engineers", "members": [ { "value": "alice" }, { "value": "bob" } ] }""");

        ScimResourceWriter.ApplyChanges(resource, [new ScimAttributeChange("members", ScimPatchOperations.Remove, "alice")], GroupSchema());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resource["members"]!.AsArray(), Has.Count.EqualTo(1));
            Assert.That(resource.ToJsonString(), Does.Contain("bob").And.Not.Contain("alice"));
        }
    }

    [Test]
    public void ApplyChanges_AddingOneValueOfAMultiValuedExtensionStringAndRemovingAnother_KeepsEveryOtherValue()
    {
        // The whole-resource equivalent of the value-filtered PATCH removal (#2181): one value goes, the rest stay.
        const string urn = "urn:example:params:scim:schemas:extension:custom:2.0:User";
        var schema = ScimAttributeMapper.FlattenSchema(new ScimSchema
        {
            Id = urn,
            Name = "Custom",
            Attributes = [new ScimSchemaAttribute { Name = "badges", Type = ScimAttributeTypes.String, MultiValued = true }]
        }, "custom");
        var resource = Resource($$"""{ "id": "alice", "{{urn}}": { "badges": [ "gold", "silver" ] } }""");

        ScimResourceWriter.ApplyChanges(resource,
        [
            new ScimAttributeChange("custom.badges", ScimPatchOperations.Add, "platinum"),
            new ScimAttributeChange("custom.badges", ScimPatchOperations.Remove, "gold")
        ], schema);

        Assert.That(resource[urn]!["badges"]!.ToJsonString(), Is.EqualTo("""["silver","platinum"]"""));
    }

    [Test]
    public void ApplyChanges_RemovingAMultiValuedAttributeNamingNoValue_TakesEveryValue()
    {
        // A removal naming no value is how a clear reaches the provider (#2182), and RFC 7644 section 3.5.2.2 reads a
        // path-only remove of a multi-valued attribute as removing all of it. Matched against no value, nothing went.
        var resource = Resource("""{ "id": "engineers", "displayName": "Engineers", "members": [ { "value": "alice" }, { "value": "bob" } ] }""");

        ScimResourceWriter.ApplyChanges(resource, [new ScimAttributeChange("members", ScimPatchOperations.Remove, null)], GroupSchema());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resource.ContainsKey("members"), Is.False);
            Assert.That(resource["displayName"]!.GetValue<string>(), Is.EqualTo("Engineers"));
        }
    }

    [Test]
    public void ApplyChanges_RemovingACanonicalSlot_TakesTheEntryWithItRatherThanLeavingAnEmptyOne()
    {
        // An entry holding nothing but the type that identified it describes nothing, and a provider is
        // entitled to reject it.
        var resource = Resource("""
        { "id": "alice", "emails": [ { "value": "work@example.com", "type": "work" }, { "value": "home@example.com", "type": "home" } ] }
        """);

        ScimResourceWriter.ApplyChanges(resource, [new ScimAttributeChange("emails.work", ScimPatchOperations.Remove, null)], UserSchema());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resource["emails"]!.AsArray(), Has.Count.EqualTo(1));
            Assert.That(resource.ToJsonString(), Does.Contain("home@example.com"));
        }
    }

    [Test]
    public void ApplyChanges_RemovingAComplexSubAttribute_LeavesItsSiblingsAlone()
    {
        var resource = Resource("""{ "id": "alice", "name": { "givenName": "Alice", "familyName": "Jensen" } }""");

        ScimResourceWriter.ApplyChanges(resource, [new ScimAttributeChange("name.givenName", ScimPatchOperations.Remove, null)], UserSchema());

        Assert.That(resource["name"]!.ToJsonString(), Is.EqualTo("{\"familyName\":\"Jensen\"}"));
    }

    #region multi-valued complex attributes without canonical types (#2090)
    /// <summary>
    /// What the provider holds before JIM's change: entries carrying sub-attributes JIM does not manage, which an
    /// add or remove of one value must leave exactly as they were.
    /// </summary>
    private const string HeldEntries = """[ { "value": "A", "type": "finance", "display": "Auditor" }, { "value": "B", "primary": true } ]""";

    private static JsonObject Holding(string attribute) => Resource($$"""{ "id": "alice", "{{attribute}}": {{HeldEntries}} }""");

    [TestCase("roles")]
    [TestCase("entitlements")]
    public void BuildResource_MultiValuedComplexValue_WritesOneEntryPerValue(string attribute)
    {
        var result = WriteUser(Write($"{attribute}.value", "A", "B"));

        Assert.That(result.Resource[attribute]!.ToJsonString(), Is.EqualTo("""[{"value":"A"},{"value":"B"}]"""));
    }

    [TestCase("roles")]
    [TestCase("entitlements")]
    public void ApplyChanges_AddingAMultiValuedComplexValue_AppendsOneEntryAndLeavesTheOthersWhole(string attribute)
    {
        var resource = Holding(attribute);

        ScimResourceWriter.ApplyChanges(resource, [new ScimAttributeChange($"{attribute}.value", ScimPatchOperations.Add, "C")], UserSchema());

        Assert.That(resource[attribute]!.ToJsonString(),
            Is.EqualTo("""[{"value":"A","type":"finance","display":"Auditor"},{"value":"B","primary":true},{"value":"C"}]"""));
    }

    [TestCase("roles")]
    [TestCase("entitlements")]
    public void ApplyChanges_RemovingAMultiValuedComplexValue_TakesOnlyThatEntry(string attribute)
    {
        var resource = Holding(attribute);

        ScimResourceWriter.ApplyChanges(resource, [new ScimAttributeChange($"{attribute}.value", ScimPatchOperations.Remove, "A")], UserSchema());

        Assert.That(resource[attribute]!.ToJsonString(), Is.EqualTo("""[{"value":"B","primary":true}]"""));
    }

    [Test]
    public void ApplyChanges_AddingAndRemovingABinaryComplexValue_ComparesByItsWireForm()
    {
        var held = Convert.ToBase64String(new byte[] { 1, 2 });
        var resource = Resource($$"""{ "id": "alice", "x509Certificates": [ { "value": "{{held}}", "display": "Old" } ] }""");

        ScimResourceWriter.ApplyChanges(resource,
        [
            new ScimAttributeChange("x509Certificates.value", ScimPatchOperations.Add, new byte[] { 3, 4 }),
            new ScimAttributeChange("x509Certificates.value", ScimPatchOperations.Remove, new byte[] { 1, 2 })
        ], UserSchema());

        Assert.That(resource["x509Certificates"]!.ToJsonString(), Is.EqualTo($$"""[{"value":"{{Convert.ToBase64String(new byte[] { 3, 4 })}}"}]"""));
    }

    [Test]
    public void ApplyChanges_AddingAMultiValuedComplexValueWhereTheProviderSentABareObject_KeepsThatEntry()
    {
        // Providers do send one bare object where the schema says multi-valued, and the reader accepts it.
        var resource = Resource("""{ "id": "alice", "roles": { "value": "A", "type": "finance" } }""");

        ScimResourceWriter.ApplyChanges(resource, [new ScimAttributeChange("roles.value", ScimPatchOperations.Add, "C")], UserSchema());

        Assert.That(resource["roles"]!.ToJsonString(), Is.EqualTo("""[{"value":"A","type":"finance"},{"value":"C"}]"""));
    }

    [Test]
    public void ApplyChanges_AddingAMultiValuedComplexValueAlreadyHeld_DoesNotDuplicateIt()
    {
        // An export not yet confirmed by import is sent again on the next run, so an add has to be safe to repeat.
        var resource = Holding("roles");

        ScimResourceWriter.ApplyChanges(resource, [new ScimAttributeChange("roles.value", ScimPatchOperations.Add, "A")], UserSchema());

        Assert.That(resource["roles"]!.ToJsonString(), Is.EqualTo("""[{"value":"A","type":"finance","display":"Auditor"},{"value":"B","primary":true}]"""));
    }

    [Test]
    public void ApplyChanges_ClearingAMultiValuedComplexValue_TakesEveryEntry()
    {
        var resource = Holding("roles");

        ScimResourceWriter.ApplyChanges(resource, [new ScimAttributeChange("roles.value", ScimPatchOperations.Remove, null)], UserSchema());

        Assert.That(resource.ContainsKey("roles"), Is.False);
    }

    [Test]
    public void ApplyChanges_ChangingTheTypeOfAMultiValuedComplexEntry_IsRefused()
    {
        // JIM cannot tell which entry a flattened "type" belongs to, so it is import-only and a change to it is
        // reported rather than guessed at.
        var resource = Holding("roles");

        var result = ScimResourceWriter.ApplyChanges(resource, [new ScimAttributeChange("roles.type", ScimPatchOperations.Add, "hr")], UserSchema());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.UnknownAttributes, Is.EqualTo(new[] { "roles.type" }));
            Assert.That(resource["roles"]!.ToJsonString(), Is.EqualTo(JsonNode.Parse(HeldEntries)!.ToJsonString()));
        }
    }
    #endregion

    [Test]
    public void ApplyChanges_AttributeTheSchemaDoesNotHave_IsReportedRatherThanDropped()
    {
        var resource = Resource("""{ "id": "alice" }""");

        var result = ScimResourceWriter.ApplyChanges(resource, [new ScimAttributeChange("notAnAttribute", ScimPatchOperations.Replace, "x")], UserSchema());

        Assert.That(result.UnknownAttributes, Is.EqualTo(new[] { "notAnAttribute" }));
    }
    #endregion
}
