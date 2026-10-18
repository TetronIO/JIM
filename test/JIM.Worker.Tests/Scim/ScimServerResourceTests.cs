// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;
using JIM.Models.Core;
using JIM.Scim;
using JIM.Scim.Discovery;
using JIM.Scim.Messages;
using JIM.Scim.Resources;
using JIM.Scim.Schema;

namespace JIM.Worker.Tests.Scim;

/// <summary>
/// The Service Provider's resource intake (what a POST or PUT body becomes in the store) and projection (what a
/// response carries): the server mode of the resource reader and writer (D6). Intake takes only what a client may set
/// and refuses a write-only attribute; projection adds <c>id</c> and <c>meta</c>, never returns a write-only attribute,
/// and honours <c>attributes</c> and <c>excludedAttributes</c> (RFC 7644 section 3.9).
/// <para>
/// The round trip runs every writable attribute through the shipped client's writer, the intake, the projection and the
/// shipped client's reader: JIM's client pushing to and reading from a JIM Service Provider must see exactly the
/// values it wrote.
/// </para>
/// </summary>
public class ScimServerResourceTests
{
    private const string CustomUrn = "urn:example:params:scim:schemas:extension:custom:2.0:User";
    private const string Secret = "S3cret-Value!";
    private const string ResourceId = "2819c223-7f76-453a-919d-413861904646";

    private static ScimSchema CustomSchema() => new()
    {
        Id = CustomUrn,
        Name = "Custom",
        Attributes =
        [
            new ScimSchemaAttribute { Name = "badges", Type = ScimAttributeTypes.String, MultiValued = true },
            new ScimSchemaAttribute { Name = "costCode", Type = ScimAttributeTypes.String, Returned = "request" }
        ]
    };

    private static ScimResourceSchema User() =>
        ScimResourceSchema.For(ScimCoreSchemas.User(), [ScimCoreSchemas.EnterpriseUser(), CustomSchema()]);

    private static ScimResourceSchema Group() => ScimResourceSchema.For(ScimCoreSchemas.Group(), []);

    private static ScimResourceMetadata Metadata(string resourceType = "User") => new()
    {
        Id = ResourceId,
        ResourceType = resourceType,
        Created = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero),
        LastModified = new DateTimeOffset(2026, 10, 17, 14, 30, 5, TimeSpan.Zero),
        Version = 7,
        Location = $"https://jim.example.test/scim/v2/12/Users/{ResourceId}"
    };

    private static JsonObject Json(string json) => JsonNode.Parse(json)!.AsObject();

    #region round trip
    private static List<ScimFlattenedAttribute> UserAttributes()
    {
        var attributes = ScimCommonAttributes.For(ScimUrns.User);
        attributes.AddRange(ScimAttributeMapper.FlattenSchema(ScimCoreSchemas.User()));
        attributes.AddRange(ScimAttributeMapper.FlattenSchema(ScimCoreSchemas.EnterpriseUser(), ScimAttributeMapper.DeriveNamePrefix(ScimCoreSchemas.EnterpriseUser())));
        attributes.AddRange(ScimAttributeMapper.FlattenSchema(CustomSchema(), "custom"));
        return attributes;
    }

    private static List<ScimFlattenedAttribute> GroupAttributes()
    {
        var attributes = ScimCommonAttributes.For(ScimUrns.Group);
        attributes.AddRange(ScimAttributeMapper.FlattenSchema(ScimCoreSchemas.Group()));
        return attributes;
    }

    private static IEnumerable<TestCaseData> WritableAttributes()
    {
        foreach (var (baseUrn, attributes) in new[] { (ScimUrns.User, UserAttributes()), (ScimUrns.Group, GroupAttributes()) })
        {
            // costCode is returned only on request, so a client reading without asking never sees it; it has its own test.
            foreach (var attribute in attributes.Where(a => a.Writability != AttributeWritability.ReadOnly && !a.IsWriteOnly && a.Name != "custom.costCode"))
                yield return new TestCaseData(baseUrn, attribute.Name).SetName($"ClientRoundTrip_{baseUrn.Split(':').Last()}_{attribute.Name}");
        }
    }

    [TestCaseSource(nameof(WritableAttributes))]
    public void IntakeThenProject_EveryWritableAttribute_ReadsBackByTheClientExactlyAsItWrote(string baseUrn, string attributeName)
    {
        var user = baseUrn == ScimUrns.User;
        var attributes = user ? UserAttributes() : GroupAttributes();
        var schema = user ? User() : Group();
        var attribute = attributes.Single(a => a.Name == attributeName);
        var written = Values(attribute);

        // Every resource carries its required attribute, as a client's create does.
        var writes = new List<ScimAttributeWrite> { new(attributeName, written) };
        var required = user ? "userName" : "displayName";
        if (attributeName != required)
            writes.Add(new ScimAttributeWrite(required, ["required-value"]));

        var body = ScimResourceWriter.BuildResource(writes, attributes, baseUrn).Resource;
        var intake = ScimResourceIntake.Read(body, schema);
        Assert.That(intake.Error, Is.Null, () => $"{intake.Error?.ScimType}: {intake.Error?.Detail}");

        var response = ScimResourceProjection.Project(intake.Resource!, Metadata(user ? "User" : "Group"), schema);
        using var document = JsonDocument.Parse(response.ToJsonString());
        var read = ScimResourceReader.Read(document.RootElement, [attribute]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(read.Warnings, Is.Empty);
            Assert.That(read.UnreadableValues, Is.Empty);
            Assert.That(ReadValues(read, attributeName), Is.EquivalentTo(written.Select(ScimValueFormatter.ToText)),
                $"{attributeName} came back as {response.ToJsonString()}");
        }
    }

    [Test]
    public void IntakeThenProject_TheClientsCommonAttributes_ReadBackAsTheServerStatesThem()
    {
        var response = ScimResourceProjection.Project(ScimResourceIntake.Read(Json("""{ "userName": "alice" }"""), User()).Resource!, Metadata(), User());
        using var document = JsonDocument.Parse(response.ToJsonString());

        var read = ScimResourceReader.Read(document.RootElement, ScimCommonAttributes.For(ScimUrns.User));
        var values = read.Attributes.ToDictionary(a => a.Name, a => a.StringValues.Concat(a.DateTimeValues.Select(d => ScimValueFormatter.ToText(d)!)).Single());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(values["id"], Is.EqualTo(ResourceId));
            Assert.That(values["meta.resourceType"], Is.EqualTo("User"));
            Assert.That(values["meta.version"], Is.EqualTo(ScimETag.For(7)));
            Assert.That(values["meta.lastModified"], Is.EqualTo("2026-10-17T14:30:05Z"));
            Assert.That(ScimResourceReader.TryReadLastModified(document.RootElement, out var lastModified), Is.True);
            Assert.That(lastModified, Is.EqualTo(Metadata().LastModified), "Delta Import watermarks against meta.lastModified");
        }
    }
    #endregion

    #region intake
    [Test]
    public void Read_ClientSuppliedIdMetaAndSchemas_AreNotKept()
    {
        var result = ScimResourceIntake.Read(Json($$"""
            { "schemas": ["{{ScimUrns.User}}"], "id": "chosen-by-client", "meta": { "version": "W/\"99\"" }, "userName": "alice" }
            """), User());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Error, Is.Null);
            Assert.That(result.Resource!.Select(m => m.Key), Is.EqualTo(new[] { "userName" }));
        }
    }

    [TestCase("""{ "userName": "alice", "password": "S3cret-Value!" }""")]
    [TestCase("""{ "userName": "alice", "PassWord": "S3cret-Value!" }""")]
    [TestCase("""{ "userName": "alice", "urn:ietf:params:scim:schemas:core:2.0:User:password": "S3cret-Value!" }""")]
    public void Read_APassword_IsRefusedWithoutRepeatingIt(string body)
    {
        var result = ScimResourceIntake.Read(Json(body), User());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Error?.StatusCode, Is.EqualTo(400));
            Assert.That(result.Error?.ScimType, Is.EqualTo(ScimErrorTypes.InvalidValue));
            Assert.That(result.Error?.Detail, Does.Contain("password").And.Not.Contain(Secret));
            Assert.That(result.Resource, Is.Null);
        }
    }

    [Test]
    public void Read_ARequiredAttributeMissing_IsRefusedNamingIt()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ScimResourceIntake.Read(Json("""{ "displayName": "Alice" }"""), User()).Error?.Detail, Does.Contain("userName"));
            Assert.That(ScimResourceIntake.Read(Json("""{ "members": [] }"""), Group()).Error?.Detail, Does.Contain("displayName"));
            Assert.That(ScimResourceIntake.Read(Json("""{ "userName": "" }"""), User()).Error?.ScimType, Is.EqualTo(ScimErrorTypes.InvalidValue),
                "an empty string is no value");
        }
    }

    [Test]
    public void Read_UnknownAndReadOnlyAttributes_AreDroppedAndRecorded()
    {
        var result = ScimResourceIntake.Read(Json("""{ "userName": "alice", "favouriteColour": "green", "groups": [ { "value": "g1" } ] }"""), User());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Resource!.Select(m => m.Key), Is.EqualTo(new[] { "userName" }));
            Assert.That(result.Coercions, Has.Count.EqualTo(2));
        }
    }

    [Test]
    public void Read_UndeclaredAttributes_AreNamedAsIgnored()
    {
        // D17: what JIM does not keep is never silent. Read-only attributes (groups) are the server's to state, so they
        // are not reported; attributes the definitions do not declare are, each by its full name.
        var result = ScimResourceIntake.Read(Json($$$"""
            {
              "userName": "alice",
              "favouriteColour": "green",
              "groups": [ { "value": "g1" } ],
              "name": { "givenName": "Alice", "nickname2": "Al" },
              "{{{ScimUrns.EnterpriseUser}}}": { "department": "R&D", "shoeSize": "5" },
              "urn:example:params:scim:schemas:extension:undeclared:2.0:User": { "badge": "B-1", "floor": "3" }
            }
            """), User());

        Assert.That(result.IgnoredAttributes, Is.EquivalentTo(new[]
        {
            "favouriteColour",
            "name.nickname2",
            $"{ScimUrns.EnterpriseUser}:shoeSize",
            "urn:example:params:scim:schemas:extension:undeclared:2.0:User:badge",
            "urn:example:params:scim:schemas:extension:undeclared:2.0:User:floor"
        }));
    }

    [Test]
    public void Read_NothingUndeclared_IgnoresNothing()
    {
        Assert.That(ScimResourceIntake.Read(Json("""{ "userName": "alice", "groups": [] }"""), User()).IgnoredAttributes, Is.Empty);
    }

    [Test]
    public void Read_ABooleanSentAsAString_IsStoredAsABoolean()
    {
        var result = ScimResourceIntake.Read(Json("""{ "userName": "alice", "active": "False" }"""), User());

        Assert.That(result.Resource!["active"]!.GetValue<bool>(), Is.False);
    }

    [Test]
    public void Read_AValueOfTheWrongType_IsInvalidValue()
    {
        Assert.That(ScimResourceIntake.Read(Json("""{ "userName": 42 }"""), User()).Error?.ScimType, Is.EqualTo(ScimErrorTypes.InvalidValue));
    }

    [Test]
    public void Read_TheCallersBody_IsNeverChanged()
    {
        var body = Json("""{ "id": "x", "userName": "alice", "active": "true" }""");
        var before = body.ToJsonString();

        ScimResourceIntake.Read(body, User());

        Assert.That(body.ToJsonString(), Is.EqualTo(before));
    }

    [Test]
    public void CheckRequired_AResourceAPatchLeftWithoutItsRequiredAttribute_IsRefused()
    {
        var patched = ScimPatchApplier.Apply(Json("""{ "userName": "alice", "title": "Engineer" }"""),
            [new ScimPatchOperation { Op = "remove", Path = "userName" }], User()).Resource!;

        Assert.That(ScimResourceIntake.CheckRequired(patched, User())?.Detail, Does.Contain("userName"));
    }
    #endregion

    #region projection
    private static JsonObject Stored() => Json($$"""
        {
          "userName": "alice",
          "name": { "givenName": "Alice", "familyName": "Smith" },
          "emails": [ { "type": "work", "value": "alice@example.com", "primary": true } ],
          "{{ScimUrns.EnterpriseUser}}": { "department": "Engineering", "costCenter": "4130" },
          "{{CustomUrn}}": { "badges": [ "gold" ], "costCode": "CC-1" }
        }
        """);

    [Test]
    public void Project_AddsIdMetaAndTheSchemasTheResourceCarries()
    {
        var response = ScimResourceProjection.Project(Stored(), Metadata(), User());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response["id"]!.GetValue<string>(), Is.EqualTo(ResourceId));
            Assert.That(response["meta"]!["resourceType"]!.GetValue<string>(), Is.EqualTo("User"));
            Assert.That(response["meta"]!["version"]!.GetValue<string>(), Is.EqualTo("W/\"7\""));
            Assert.That(response["meta"]!["location"]!.GetValue<string>(), Is.EqualTo(Metadata().Location));
            Assert.That(response["schemas"]!.AsArray().Select(s => s!.GetValue<string>()),
                Is.EqualTo(new[] { ScimUrns.User, ScimUrns.EnterpriseUser, CustomUrn }));
            Assert.That(response.First().Key, Is.EqualTo("schemas"));
        }
    }

    [Test]
    public void Project_AWriteOnlyAttributeHeld_IsNeverReturnedEvenWhenAskedFor()
    {
        var stored = Stored();
        stored["password"] = Secret;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ScimResourceProjection.Project(stored, Metadata(), User()).ToJsonString(), Does.Not.Contain(Secret));
            Assert.That(ScimResourceProjection.Project(stored, Metadata(), User(), attributes: ["password", "userName"]).ToJsonString(), Does.Not.Contain(Secret));
        }
    }

    [Test]
    public void Project_ReturnedOnRequest_IsLeftOutUnlessNamed()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ScimResourceProjection.Project(Stored(), Metadata(), User())[CustomUrn]!.AsObject().ContainsKey("costCode"), Is.False);
            Assert.That(ScimResourceProjection.Project(Stored(), Metadata(), User(), attributes: [$"{CustomUrn}:costCode"])[CustomUrn]!["costCode"]!.GetValue<string>(),
                Is.EqualTo("CC-1"));
        }
    }

    [Test]
    public void Project_ExcludedAttributes_LeavesThemOutAndKeepsTheRest()
    {
        var group = ScimResourceProjection.Project(Json("""{ "displayName": "Engineers", "members": [ { "value": "a1" } ] }"""),
            Metadata("Group"), Group(), excludedAttributes: ["members"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(group.ContainsKey("members"), Is.False);
            Assert.That(group["displayName"]!.GetValue<string>(), Is.EqualTo("Engineers"));
            Assert.That(group.ContainsKey("id") && group.ContainsKey("meta"), Is.True);
        }
    }

    [Test]
    public void Project_ExcludedSubAttribute_LeavesTheRestOfTheComplexValue()
    {
        var response = ScimResourceProjection.Project(Stored(), Metadata(), User(), excludedAttributes: ["name.givenName", "ID"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response["name"]!.AsObject().Select(m => m.Key), Is.EqualTo(new[] { "familyName" }));
            Assert.That(response.ContainsKey("id"), Is.True, "id is always returned (RFC 7643 section 3.1)");
        }
    }

    [Test]
    public void Project_Attributes_ReturnsOnlyThoseAndWhatIsAlwaysReturned()
    {
        var response = ScimResourceProjection.Project(Stored(), Metadata(), User(), attributes: ["userName", "emails.value"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Select(m => m.Key), Is.EquivalentTo(new[] { "schemas", "id", "userName", "emails" }));
            Assert.That(response["emails"]![0]!.AsObject().Select(m => m.Key), Is.EqualTo(new[] { "value" }));
            Assert.That(response["schemas"]!.AsArray().Select(s => s!.GetValue<string>()), Is.EqualTo(new[] { ScimUrns.User }));
        }
    }

    [Test]
    public void Project_AttributesNamingAnExtensionOrOneOfItsAttributes_ReturnsThat()
    {
        var whole = ScimResourceProjection.Project(Stored(), Metadata(), User(), attributes: [ScimUrns.EnterpriseUser]);
        var one = ScimResourceProjection.Project(Stored(), Metadata(), User(), attributes: [$"{ScimUrns.EnterpriseUser}:department"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(whole[ScimUrns.EnterpriseUser]!.AsObject().Select(m => m.Key), Is.EquivalentTo(new[] { "department", "costCenter" }));
            Assert.That(one[ScimUrns.EnterpriseUser]!.AsObject().Select(m => m.Key), Is.EqualTo(new[] { "department" }));
            Assert.That(one.ContainsKey("userName"), Is.False);
        }
    }

    [Test]
    public void Project_TheStoredResource_IsNeverChanged()
    {
        var stored = Stored();
        var before = stored.ToJsonString();

        ScimResourceProjection.Project(stored, Metadata(), User(), excludedAttributes: ["name"]);

        Assert.That(stored.ToJsonString(), Is.EqualTo(before));
    }

    [Test]
    public void Project_BothAttributesAndExcludedAttributes_IsRefused()
    {
        // RFC 7644 section 3.9 makes them mutually exclusive; the endpoint answers 400 before projecting.
        Assert.That(() => ScimResourceProjection.Project(Stored(), Metadata(), User(), attributes: ["userName"], excludedAttributes: ["name"]),
            Throws.ArgumentException);
    }
    #endregion

    private static List<object?> Values(ScimFlattenedAttribute attribute)
    {
        var count = attribute.AttributePlurality == AttributePlurality.MultiValued ? 2 : 1;
        return Enumerable.Range(1, count).Select(i => Sample(attribute, i)).ToList();
    }

    private static object? Sample(ScimFlattenedAttribute attribute, int i) => attribute.Type switch
    {
        AttributeDataType.Boolean => true,
        AttributeDataType.Number => i,
        AttributeDataType.LongNumber => (long)i,
        AttributeDataType.Decimal => i + 0.5m,
        AttributeDataType.DateTime => new DateTime(2026, 1, i, 9, 0, 0, DateTimeKind.Utc),
        AttributeDataType.Binary => new byte[] { (byte)i, 0xAB },
        _ => $"{attribute.Name}-{i}"
    };

    private static List<string?> ReadValues(ScimResourceReadResult read, string attributeName)
    {
        var attribute = read.Attributes.SingleOrDefault(a => a.Name == attributeName);
        if (attribute == null)
            return [];

        return attribute.StringValues.Cast<object>()
            .Concat(attribute.ReferenceValues)
            .Concat(attribute.IntValues.Cast<object>())
            .Concat(attribute.LongValues.Cast<object>())
            .Concat(attribute.DecimalValues.Cast<object>())
            .Concat(attribute.HasDateTimeValues ? attribute.DateTimeValues.Cast<object>() : [])
            .Concat(attribute.GuidValues.Cast<object>())
            .Concat(attribute.ByteValues)
            .Concat(attribute.HasBoolValues ? attribute.BoolValues.Cast<object>() : [])
            .Select(ScimValueFormatter.ToText)
            .ToList();
    }
}
