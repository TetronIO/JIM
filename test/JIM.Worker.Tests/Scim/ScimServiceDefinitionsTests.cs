// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;
using JIM.Models.Scim;
using JIM.Scim;
using JIM.Scim.Discovery;
using JIM.Scim.Schema;
using JIM.Scim.Serialisation;
using JIM.Scim.Service;

namespace JIM.Worker.Tests.Scim;

/// <summary>
/// The Service Provider's view of a Connected System's stored definitions (D18): each resource type's schemas, the
/// attributes whose values must be unique, and the attributes held as references. Nothing here names User or Group;
/// every rule comes from the definitions, so a custom resource type is served by the same code.
/// </summary>
public class ScimServiceDefinitionsTests
{
    private const string DeviceUrn = "urn:example:params:scim:schemas:Device";
    private const string BadgeUrn = "urn:example:params:scim:schemas:extension:badge:2.0:User";

    [Test]
    public void CoreRows_DescribeUserGroupAndEnterpriseUser()
    {
        var (schemas, resourceTypes) = ScimCoreDefinitions.Rows();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(schemas.Select(s => s.Urn), Is.EquivalentTo(new[] { ScimUrns.User, ScimUrns.Group, ScimUrns.EnterpriseUser }));
            Assert.That(resourceTypes.Select(t => (t.Name, t.Endpoint)), Is.EquivalentTo(new[] { ("User", "/Users"), ("Group", "/Groups") }));
        }
    }

    [Test]
    public void CoreRows_KeepPasswordSoIntakeCanRefuseIt()
    {
        var (schemas, _) = ScimCoreDefinitions.Rows();

        var user = JsonSerializer.Deserialize<ScimSchema>(schemas.Single(s => s.Urn == ScimUrns.User).Definition, ScimJson.Options)!;

        Assert.That(user.Attributes.Single(a => a.Name == "password").Mutability, Is.EqualTo(ScimMutability.WriteOnly));
    }

    [Test]
    public void CoreSchemas_UserName_IsUniqueAcrossTheServer()
    {
        Assert.That(ScimCoreSchemas.User().Attributes.Single(a => a.Name == "userName").Uniqueness, Is.EqualTo("server"));
    }

    [Test]
    public void Compile_Core_FindsResourceTypesByEndpointAndId()
    {
        var definitions = CompileCore();

        var user = definitions.FindByEndpoint("/Users");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(user, Is.Not.Null);
            Assert.That(user!.Name, Is.EqualTo("User"));
            Assert.That(user.Schema.BaseSchemaUrn, Is.EqualTo(ScimUrns.User));
            Assert.That(user.Schema.ExtensionUrns, Is.EqualTo(new[] { ScimUrns.EnterpriseUser }));
            Assert.That(definitions.FindByEndpoint("users")?.Name, Is.EqualTo("User"), "matched without regard to case or leading slash");
            Assert.That(definitions.FindById(user.Id)?.Name, Is.EqualTo("User"));
            Assert.That(definitions.FindByEndpoint("/Devices"), Is.Null);
        }
    }

    [Test]
    public void Compile_Core_UniqueAttributesAreUserNameAndExternalId()
    {
        var definitions = CompileCore();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(definitions.FindByName("User")!.UniqueAttributes.Select(a => a.Name), Is.EquivalentTo(new[] { "userName", "externalId" }));
            Assert.That(definitions.FindByName("Group")!.UniqueAttributes.Select(a => a.Name), Is.EquivalentTo(new[] { "externalId" }),
                "a Group's displayName is not unique in RFC 7643");
        }
    }

    [Test]
    public void Compile_Core_ReferenceAttributesAreMembersAndManager()
    {
        var definitions = CompileCore();

        var members = definitions.FindByName("Group")!.ReferenceAttributes.Single();
        var manager = definitions.FindByName("User")!.ReferenceAttributes.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(members.Name, Is.EqualTo("members"));
            Assert.That(members.MultiValued, Is.True);
            Assert.That(members.TargetTypeNames, Is.EquivalentTo(new[] { "User", "Group" }));
            Assert.That(manager.Name, Is.EqualTo($"{ScimUrns.EnterpriseUser}:manager"));
            Assert.That(manager.SchemaUrn, Is.EqualTo(ScimUrns.EnterpriseUser));
            Assert.That(manager.AttributeName, Is.EqualTo("manager"));
            Assert.That(manager.MultiValued, Is.False);
            Assert.That(manager.TargetTypeNames, Is.EqualTo(new[] { "User" }));
        }
    }

    [Test]
    public void Compile_ReadOnlyReferenceAttribute_IsNotHeldAsReferences()
    {
        // Like a User's groups, a read-only reference is the server's to derive, so a client never writes it.
        var device = new ScimSchema
        {
            Id = DeviceUrn,
            Name = "Device",
            Attributes =
            [
                new ScimSchemaAttribute
                {
                    Name = "viewers",
                    Type = ScimAttributeTypes.Complex,
                    MultiValued = true,
                    Mutability = ScimMutability.ReadOnly,
                    SubAttributes = [new ScimSchemaAttribute { Name = "$ref", Type = ScimAttributeTypes.Reference, ReferenceTypes = ["Device"] }]
                }
            ]
        };

        var definitions = Compile([SchemaRow(device)], [TypeRow(new ScimResourceType { Name = "Device", Endpoint = "/Devices", Schema = DeviceUrn })]);

        Assert.That(definitions.FindByName("Device")!.ReferenceAttributes, Is.Empty);
    }

    [Test]
    public void Compile_CustomTypeAndExtension_DerivesTheSameRulesFromData()
    {
        var device = new ScimSchema
        {
            Id = DeviceUrn,
            Name = "Device",
            Attributes =
            [
                new ScimSchemaAttribute { Name = "serialNumber", Type = ScimAttributeTypes.String, Uniqueness = "global", CaseExact = true },
                new ScimSchemaAttribute { Name = "label", Type = ScimAttributeTypes.String },
                new ScimSchemaAttribute
                {
                    Name = "owner",
                    Type = ScimAttributeTypes.Complex,
                    SubAttributes =
                    [
                        new ScimSchemaAttribute { Name = "value", Type = ScimAttributeTypes.String },
                        new ScimSchemaAttribute { Name = "$ref", Type = ScimAttributeTypes.Reference, ReferenceTypes = ["User", "external"] }
                    ]
                }
            ]
        };
        var badge = new ScimSchema
        {
            Id = BadgeUrn,
            Name = "Badge",
            Attributes = [new ScimSchemaAttribute { Name = "badgeNumber", Type = ScimAttributeTypes.String, Uniqueness = "server" }]
        };
        var (coreSchemas, coreTypes) = ScimCoreDefinitions.Rows();
        var userType = coreTypes.Single(t => t.Name == "User");
        var userDefinition = JsonSerializer.Deserialize<ScimResourceType>(userType.Definition, ScimJson.Options)!;
        userDefinition.SchemaExtensions.Add(new ScimSchemaExtension { Schema = BadgeUrn, Required = false });
        userType.Definition = JsonSerializer.Serialize(userDefinition, ScimJson.Options);

        var definitions = Compile(
            [.. coreSchemas, SchemaRow(device), SchemaRow(badge)],
            [.. coreTypes, TypeRow(new ScimResourceType { Name = "Device", Endpoint = "/Devices", Schema = DeviceUrn })]);

        var compiledDevice = definitions.FindByEndpoint("/Devices")!;
        var serial = compiledDevice.UniqueAttributes.Single(a => a.Name == "serialNumber");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(compiledDevice.UniqueAttributes.Select(a => a.Name), Is.EquivalentTo(new[] { "serialNumber", "externalId" }));
            Assert.That(serial.CaseExact, Is.True);
            Assert.That(compiledDevice.ReferenceAttributes.Single().TargetTypeNames, Is.EqualTo(new[] { "User" }),
                "a reference type that is not a resource type of this store (external, uri) is not held as a reference");
            Assert.That(definitions.FindByName("User")!.UniqueAttributes.Select(a => a.Name),
                Is.EquivalentTo(new[] { "userName", "externalId", $"{BadgeUrn}:badgeNumber" }));
        }
    }

    [Test]
    public void Compile_ResourceTypeNamingAMissingSchema_Throws()
    {
        var (schemas, types) = ScimCoreDefinitions.Rows();

        Assert.That(() => Compile(schemas.Where(s => s.Urn != ScimUrns.Group).ToList(), types),
            Throws.InvalidOperationException.With.Message.Contains(ScimUrns.Group));
    }

    [TestCase("Ada.Lovelace", false, "ADA.LOVELACE")]
    [TestCase("Ada.Lovelace", true, "Ada.Lovelace")]
    public void UniqueAttribute_Normalise_FoldsCaseUnlessCaseExact(string value, bool caseExact, string expected)
    {
        var attribute = new ScimUniqueAttribute("userName", null, "userName", caseExact);

        Assert.That(attribute.Normalise(JsonValue.Create(value)), Is.EqualTo(expected));
    }

    [Test]
    public void UniqueAttribute_Normalise_NonStringUsesItsJsonText()
    {
        var attribute = new ScimUniqueAttribute("seat", null, "seat", false);

        Assert.That(attribute.Normalise(JsonValue.Create(42)), Is.EqualTo("42"));
    }

    [Test]
    public void UniqueValues_TakesEachUniqueAttributeFromTheResource()
    {
        var user = CompileCore().FindByName("User")!;
        var resource = new JsonObject
        {
            ["userName"] = "Ada",
            ["externalId"] = "ext-1",
            ["displayName"] = "Ada Lovelace"
        };

        var values = user.UniqueValues(resource);

        Assert.That(values.Select(v => (v.Attribute, v.Value)), Is.EquivalentTo(new[] { ("userName", "ADA"), ("externalId", "ext-1") }));
    }

    [Test]
    public void UniqueValues_ExtensionAttribute_IsReadFromItsExtensionMember()
    {
        var badge = new ScimSchema
        {
            Id = BadgeUrn,
            Name = "Badge",
            Attributes = [new ScimSchemaAttribute { Name = "badgeNumber", Type = ScimAttributeTypes.String, Uniqueness = "server", CaseExact = true }]
        };
        var (coreSchemas, coreTypes) = ScimCoreDefinitions.Rows();
        var userType = coreTypes.Single(t => t.Name == "User");
        var userDefinition = JsonSerializer.Deserialize<ScimResourceType>(userType.Definition, ScimJson.Options)!;
        userDefinition.SchemaExtensions = [new ScimSchemaExtension { Schema = BadgeUrn }];
        userType.Definition = JsonSerializer.Serialize(userDefinition, ScimJson.Options);
        var user = Compile([.. coreSchemas, SchemaRow(badge)], coreTypes).FindByName("User")!;

        var values = user.UniqueValues(new JsonObject { ["userName"] = "ada", [BadgeUrn] = new JsonObject { ["badgeNumber"] = "B-7" } });

        Assert.That(values.Select(v => (v.Attribute, v.Value)), Does.Contain(($"{BadgeUrn}:badgeNumber", "B-7")));
    }

    [Test]
    public void UniqueValues_MultiValuedAttribute_GivesOneRowPerDistinctValue()
    {
        var tags = new ScimSchema
        {
            Id = DeviceUrn,
            Name = "Device",
            Attributes = [new ScimSchemaAttribute { Name = "tags", Type = ScimAttributeTypes.String, MultiValued = true, Uniqueness = "server" }]
        };
        var device = Compile([SchemaRow(tags)], [TypeRow(new ScimResourceType { Name = "Device", Endpoint = "/Devices", Schema = DeviceUrn })])
            .FindByName("Device")!;

        var values = device.UniqueValues(new JsonObject { ["tags"] = new JsonArray("a", "A", "b") });

        Assert.That(values.Select(v => v.Value), Is.EquivalentTo(new[] { "A", "B" }), "values equal under the comparison are one value");
    }

    private static ScimServiceDefinitions CompileCore()
    {
        var (schemas, types) = ScimCoreDefinitions.Rows();
        return Compile(schemas, types);
    }

    // The store assigns row ids; the compiler only needs them to be distinct.
    private static ScimServiceDefinitions Compile(IReadOnlyList<ScimStoreSchema> schemas, IReadOnlyList<ScimStoreResourceType> types)
    {
        for (var i = 0; i < types.Count; i++)
            types[i].Id = i + 1;
        return ScimServiceDefinitions.Compile(types, schemas);
    }

    private static ScimStoreSchema SchemaRow(ScimSchema schema) =>
        new() { Urn = schema.Id!, Definition = JsonSerializer.Serialize(schema, ScimJson.Options) };

    private static ScimStoreResourceType TypeRow(ScimResourceType type) =>
        new() { Name = type.Name!, Endpoint = type.Endpoint!, Definition = JsonSerializer.Serialize(type, ScimJson.Options) };
}
