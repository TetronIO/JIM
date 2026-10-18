// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Nodes;
using JIM.Scim;
using JIM.Scim.Messages;
using JIM.Scim.Schema;

namespace JIM.Worker.Tests.Scim;

/// <summary>
/// The Service Provider's PATCH applier (RFC 7644 section 3.5.2) against what identity providers send, beyond the forms
/// JIM's own client emits (those are proved by <see cref="ScimPatchApplierDifferentialTests"/>): operations with no path,
/// value filters, the interoperability leniency the plan names, atomicity, the refusal of pushed passwords (D5), and
/// errors that never repeat a value.
/// </summary>
public class ScimPatchApplierTests
{
    private const string Secret = "S3cret-Value!";

    private static ScimResourceSchema User() => ScimResourceSchema.For(ScimCoreSchemas.User(), [ScimCoreSchemas.EnterpriseUser()]);

    private static ScimResourceSchema Group() => ScimResourceSchema.For(ScimCoreSchemas.Group(), []);

    private static JsonObject Alice() => JsonNode.Parse("""
        {
          "schemas": ["urn:ietf:params:scim:schemas:core:2.0:User"],
          "id": "2819c223-7f76-453a-919d-413861904646",
          "userName": "alice",
          "name": { "givenName": "Alice", "familyName": "Smith" },
          "active": true,
          "emails": [
            { "type": "work", "value": "alice@example.com", "primary": true },
            { "type": "home", "value": "alice@home.example" }
          ],
          "meta": { "resourceType": "User", "version": "W/\"3\"" }
        }
        """)!.AsObject();

    private static JsonObject Engineers() => JsonNode.Parse("""
        {
          "schemas": ["urn:ietf:params:scim:schemas:core:2.0:Group"],
          "id": "e9e30dba-f08f-4109-8486-d5c6a331660a",
          "displayName": "Engineers",
          "members": [ { "value": "a1" }, { "value": "b2" }, { "value": "c3" } ]
        }
        """)!.AsObject();

    private static ScimPatchOperation Op(string op, string? path, string? json = null) =>
        new() { Op = op, Path = path, Value = json == null ? null : JsonNode.Parse(json) };

    [Test]
    public void Apply_PathOnAnUndeclaredSchema_IsNamedAsIgnored()
    {
        var result = ScimPatchApplier.Apply(Alice(),
            [Op("replace", "urn:example:params:scim:schemas:extension:undeclared:2.0:User:badge", "\"B-1\"")], User());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Error, Is.Null);
            Assert.That(result.IgnoredAttributes, Is.EqualTo(new[] { "urn:example:params:scim:schemas:extension:undeclared:2.0:User:badge" }));
        }
    }

    [Test]
    public void Apply_NoPathValueNamingAnUndeclaredSchema_NamesItsBareValueByTheUrn()
    {
        var result = ScimPatchApplier.Apply(Alice(),
            [Op("replace", null, """{ "urn:example:params:scim:schemas:extension:undeclared:2.0:User": "x" }""")], User());

        Assert.That(result.IgnoredAttributes, Is.EqualTo(new[] { "urn:example:params:scim:schemas:extension:undeclared:2.0:User" }));
    }

    [Test]
    public void Apply_ReadOnlyAttribute_IsNotReportedAsIgnored()
    {
        var result = ScimPatchApplier.Apply(Alice(), [Op("add", "groups", """[ { "value": "g1" } ]""")], User());

        Assert.That(result.IgnoredAttributes, Is.Empty);
    }

    private static JsonObject Applied(ScimPatchResult result)
    {
        Assert.That(result.Error, Is.Null, () => $"{result.Error?.ScimType}: {result.Error?.Detail}");
        return result.Resource!;
    }

    private static string?[] Members(JsonObject group) =>
        group["members"]!.AsArray().Select(m => m!["value"]!.GetValue<string>()).ToArray();

    #region operations
    [Test]
    public void Apply_ReplaceWithNoPath_ReplacesEachAttributeNamedAndMergesComplexOnes()
    {
        var resource = Applied(ScimPatchApplier.Apply(Alice(), [Op("replace", null, """{ "displayName": "Alice S", "name": { "givenName": "Ali" } }""")], User()));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resource["displayName"]!.GetValue<string>(), Is.EqualTo("Alice S"));
            Assert.That(resource["name"]!["givenName"]!.GetValue<string>(), Is.EqualTo("Ali"));
            Assert.That(resource["name"]!["familyName"]!.GetValue<string>(), Is.EqualTo("Smith"),
                "sub-attributes not named are left as they were (RFC 7644 section 3.5.2.3)");
        }
    }

    [Test]
    public void Apply_AddWithNoPath_ReachesAnExtensionThroughItsUrn()
    {
        var resource = Applied(ScimPatchApplier.Apply(Alice(),
            [Op("add", null, $$"""{ "{{ScimUrns.EnterpriseUser}}": { "department": "Engineering" } }""")], User()));

        Assert.That(resource[ScimUrns.EnterpriseUser]!["department"]!.GetValue<string>(), Is.EqualTo("Engineering"));
    }

    [Test]
    public void Apply_ReplaceByUrnQualifiedPath_ReachesTheExtension()
    {
        var resource = Applied(ScimPatchApplier.Apply(Alice(), [Op("replace", $"{ScimUrns.EnterpriseUser}:department", "\"Finance\"")], User()));

        Assert.That(resource[ScimUrns.EnterpriseUser]!["department"]!.GetValue<string>(), Is.EqualTo("Finance"));
    }

    [Test]
    public void Apply_AttributeNamesInAnyCase_AreStoredUnderTheSchemasName()
    {
        var resource = Applied(ScimPatchApplier.Apply(Alice(),
            [Op("replace", "DisplayName", "\"A\""), Op("replace", null, """{ "NICKNAME": "Al" }""")], User()));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resource["displayName"]!.GetValue<string>(), Is.EqualTo("A"));
            Assert.That(resource["nickName"]!.GetValue<string>(), Is.EqualTo("Al"));
            Assert.That(resource.ContainsKey("DisplayName") || resource.ContainsKey("NICKNAME"), Is.False);
        }
    }

    [Test]
    public void Apply_AddAValueAMultiValuedAttributeAlreadyHolds_LeavesItHeldOnce()
    {
        var group = Applied(ScimPatchApplier.Apply(Engineers(), [Op("add", "members", """[ { "value": "b2" }, { "value": "d4" } ]""")], Group()));

        Assert.That(Members(group), Is.EqualTo(new[] { "a1", "b2", "c3", "d4" }));
    }

    [Test]
    public void Apply_RemoveByValueFilter_RemovesOnlyTheMatchingMember()
    {
        var group = Applied(ScimPatchApplier.Apply(Engineers(), [Op("remove", "members[value eq \"b2\"]")], Group()));

        Assert.That(Members(group), Is.EqualTo(new[] { "a1", "c3" }));
    }

    [Test]
    public void Apply_RemoveAMultiValuedAttributeWithNoValue_RemovesEveryValue()
    {
        var group = Applied(ScimPatchApplier.Apply(Engineers(), [Op("remove", "members")], Group()));

        Assert.That(group.ContainsKey("members"), Is.False);
    }

    [Test]
    public void Apply_RemoveAValueNoLongerHeld_SucceedsAndChangesNothing()
    {
        // A client re-sends a removal its last export did not see confirmed; failing it would fail the export.
        var group = Applied(ScimPatchApplier.Apply(Engineers(), [Op("remove", "members[value eq \"zz\"]")], Group()));

        Assert.That(Members(group), Is.EqualTo(new[] { "a1", "b2", "c3" }));
    }

    [Test]
    public void Apply_ReplaceASubAttributeOfFilteredEntries_ReplacesItInEveryMatch()
    {
        var resource = Applied(ScimPatchApplier.Apply(Alice(), [Op("replace", "emails[type eq \"work\"].value", "\"a@corp.example\"")], User()));

        Assert.That(resource["emails"]![0]!["value"]!.GetValue<string>(), Is.EqualTo("a@corp.example"));
    }

    [Test]
    public void Apply_CompoundValueFilter_MatchesOnEveryCondition()
    {
        var resource = Applied(ScimPatchApplier.Apply(Alice(),
            [Op("remove", "emails[type eq \"home\" and value ew \"home.example\"]")], User()));

        Assert.That(resource["emails"]!.AsArray().Select(e => e!["type"]!.GetValue<string>()), Is.EqualTo(new[] { "work" }));
    }

    [Test]
    public void Apply_ReplaceOnAnEntryNoneMatchesByEquality_CreatesItFromTheFilter()
    {
        var resource = Applied(ScimPatchApplier.Apply(Alice(), [Op("replace", "emails[type eq \"other\"].value", "\"a@other.example\"")], User()));

        var created = resource["emails"]!.AsArray().OfType<JsonObject>().Single(e => e["type"]?.GetValue<string>() == "other");
        Assert.That(created["value"]!.GetValue<string>(), Is.EqualTo("a@other.example"));
    }

    [Test]
    public void Apply_ReplaceOnAnEntryNoneMatchesByANonEqualityFilter_IsNoTarget()
    {
        var result = ScimPatchApplier.Apply(Alice(), [Op("replace", "emails[value co \"nowhere\"].value", "\"x@example.com\"")], User());

        Assert.That(result.Error?.ScimType, Is.EqualTo(ScimErrorTypes.NoTarget));
    }
    #endregion

    #region leniency
    [Test]
    public void Apply_CapitalisedOp_IsAcceptedAndRecorded()
    {
        var result = ScimPatchApplier.Apply(Alice(), [Op("Replace", "displayName", "\"A\"")], User());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Applied(result)["displayName"]!.GetValue<string>(), Is.EqualTo("A"));
            Assert.That(result.Coercions, Is.Not.Empty);
        }
    }

    [TestCase("\"False\"", false)]
    [TestCase("\"true\"", true)]
    public void Apply_BooleanSentAsAString_IsStoredAsABoolean(string json, bool expected)
    {
        var resource = Applied(ScimPatchApplier.Apply(Alice(), [Op("replace", "active", json)], User()));

        Assert.That(resource["active"]!.GetValue<bool>(), Is.EqualTo(expected));
    }

    [Test]
    public void Apply_SingleElementArrayForASingleValuedAttribute_IsUnwrapped()
    {
        var resource = Applied(ScimPatchApplier.Apply(Alice(), [Op("replace", "displayName", "[ \"A\" ]")], User()));

        Assert.That(resource["displayName"]!.GetValue<string>(), Is.EqualTo("A"));
    }

    [Test]
    public void Apply_EmptyStringForASingleValuedAttribute_ClearsIt()
    {
        var resource = Applied(ScimPatchApplier.Apply(Alice(), [Op("replace", "userName", "\"\""), Op("replace", "name.givenName", "\"\"")], User()));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resource.ContainsKey("userName"), Is.False);
            Assert.That(resource["name"]!.AsObject().ContainsKey("givenName"), Is.False);
        }
    }

    [Test]
    public void Apply_ReplaceWithNoValue_ClearsTheAttribute()
    {
        var resource = Applied(ScimPatchApplier.Apply(Alice(), [Op("replace", "name.familyName")], User()));

        Assert.That(resource["name"]!.AsObject().ContainsKey("familyName"), Is.False);
    }

    [Test]
    public void Apply_RemoveAMultiValuedAttributeWithAValueList_RemovesOnlyTheListedValues()
    {
        // RFC 7644 reads this as removing every member; some identity providers send it meaning one. Taking it
        // literally would wipe a group's membership.
        var group = Applied(ScimPatchApplier.Apply(Engineers(), [Op("remove", "members", """[ { "value": "a1" }, { "value": "c3" } ]""")], Group()));

        Assert.That(Members(group), Is.EqualTo(new[] { "b2" }));
    }

    [Test]
    public void Apply_ReadOnlyAttributes_AreIgnoredAndRecorded()
    {
        var result = ScimPatchApplier.Apply(Alice(),
            [Op("replace", "id", "\"changed\""), Op("replace", null, """{ "meta": { "version": "W/\"9\"" }, "groups": [ { "value": "g1" } ], "title": "Eng" }""")],
            User());

        var resource = Applied(result);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(resource["id"]!.GetValue<string>(), Is.EqualTo("2819c223-7f76-453a-919d-413861904646"));
            Assert.That(resource["meta"]!["version"]!.GetValue<string>(), Is.EqualTo("W/\"3\""));
            Assert.That(resource.ContainsKey("groups"), Is.False);
            Assert.That(resource["title"]!.GetValue<string>(), Is.EqualTo("Eng"));
            Assert.That(result.Coercions, Is.Not.Empty);
        }
    }

    [Test]
    public void Apply_UndeclaredExtension_IsIgnoredAndRecorded()
    {
        var result = ScimPatchApplier.Apply(Alice(),
            [Op("add", "urn:example:undeclared:2.0:User:badge", "\"gold\""), Op("add", null, """{ "urn:example:undeclared:2.0:User": { "badge": "gold" } }""")],
            User());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Applied(result).ContainsKey("urn:example:undeclared:2.0:User"), Is.False);
            Assert.That(result.Coercions, Is.Not.Empty);
        }
    }
    #endregion

    #region refusals
    [TestCase("add", "password", "\"" + Secret + "\"")]
    [TestCase("replace", "PASSWORD", "\"" + Secret + "\"")]
    [TestCase("replace", null, "{ \"password\": \"" + Secret + "\" }")]
    [TestCase("add", null, "{ \"urn:ietf:params:scim:schemas:core:2.0:User:password\": \"" + Secret + "\" }")]
    [TestCase("remove", "password", null)]
    public void Apply_AnyOperationOnPassword_IsRefusedWithoutRepeatingTheValue(string op, string? path, string? json)
    {
        var result = ScimPatchApplier.Apply(Alice(), [Op(op, path, json)], User());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Error?.StatusCode, Is.EqualTo(400));
            Assert.That(result.Error?.ScimType, Is.EqualTo(ScimErrorTypes.InvalidValue));
            Assert.That(result.Error?.Detail, Does.Contain("password").And.Not.Contain(Secret));
            Assert.That(result.Resource, Is.Null);
        }
    }

    [Test]
    public void Apply_ALaterOperationFails_NothingApplies()
    {
        var original = Alice();
        var before = original.ToJsonString();

        var result = ScimPatchApplier.Apply(original, [Op("replace", "displayName", "\"Changed\""), Op("replace", "noSuchAttribute", "\"x\"")], User());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Error?.ScimType, Is.EqualTo(ScimErrorTypes.InvalidPath));
            Assert.That(result.Resource, Is.Null);
            Assert.That(original.ToJsonString(), Is.EqualTo(before), "the caller's resource is never touched");
        }
    }

    [Test]
    public void Apply_RemoveWithNoPath_IsNoTarget()
    {
        Assert.That(ScimPatchApplier.Apply(Alice(), [Op("remove", null)], User()).Error?.ScimType, Is.EqualTo(ScimErrorTypes.NoTarget));
    }

    [Test]
    public void Apply_UnknownOp_IsInvalidSyntax()
    {
        Assert.That(ScimPatchApplier.Apply(Alice(), [Op("move", "displayName", "\"A\"")], User()).Error?.ScimType, Is.EqualTo(ScimErrorTypes.InvalidSyntax));
    }

    [Test]
    public void Apply_MalformedPath_IsInvalidPathWithoutRepeatingTheFilterValue()
    {
        var result = ScimPatchApplier.Apply(Alice(), [Op("remove", $"emails[value eq \"{Secret}\"")], User());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Error?.ScimType, Is.EqualTo(ScimErrorTypes.InvalidPath));
            Assert.That(result.Error?.Detail, Does.Not.Contain(Secret));
        }
    }

    [TestCase("active", "\"maybe\"")]
    [TestCase("name", "\"not an object\"")]
    [TestCase("userName", "42")]
    public void Apply_ValueOfTheWrongType_IsInvalidValueWithoutRepeatingIt(string path, string json)
    {
        var result = ScimPatchApplier.Apply(Alice(), [Op("replace", path, json)], User());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Error?.ScimType, Is.EqualTo(ScimErrorTypes.InvalidValue));
            Assert.That(result.Error?.Detail, Does.Contain(path).And.Not.Contain(json.Trim('"')));
        }
    }

    [Test]
    public void Apply_AddWithNoPathAndNoObject_IsInvalidValue()
    {
        Assert.That(ScimPatchApplier.Apply(Alice(), [Op("add", null, "\"x\"")], User()).Error?.ScimType, Is.EqualTo(ScimErrorTypes.InvalidValue));
    }
    #endregion
}
