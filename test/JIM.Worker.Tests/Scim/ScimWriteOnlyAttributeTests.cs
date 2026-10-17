// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Nodes;
using JIM.Scim;
using JIM.Scim.Discovery;
using JIM.Scim.Messages;
using JIM.Scim.Schema;

namespace JIM.Worker.Tests.Scim;

/// <summary>
/// A SCIM attribute the provider declares write-only (<c>mutability: writeOnly</c> or <c>returned: never</c>), above
/// all the core User's <c>password</c>, is never written by an export (#2091). A password reaches a service provider
/// only through Password Synchronisation; written through an Attribute Flow it would be held in clear text in Pending
/// Exports and history, and the provider never returns it, so the export could never be confirmed either.
/// </summary>
public class ScimWriteOnlyAttributeTests
{
    private const string CustomUrn = "urn:example:params:scim:schemas:extension:custom:2.0:User";

    private static ScimSchema CustomSchema() => new()
    {
        Id = CustomUrn,
        Name = "Custom",
        Attributes =
        [
            new ScimSchemaAttribute { Name = "pin", Type = ScimAttributeTypes.String, Mutability = ScimMutability.WriteOnly },
            new ScimSchemaAttribute { Name = "recoveryCode", Type = ScimAttributeTypes.String, Returned = "never" },
            new ScimSchemaAttribute
            {
                Name = "secretQuestion",
                Type = ScimAttributeTypes.Complex,
                Mutability = ScimMutability.WriteOnly,
                SubAttributes =
                [
                    new ScimSchemaAttribute { Name = "question", Type = ScimAttributeTypes.String },
                    new ScimSchemaAttribute { Name = "answer", Type = ScimAttributeTypes.String }
                ]
            },
            new ScimSchemaAttribute { Name = "costCentre", Type = ScimAttributeTypes.String }
        ]
    };

    private static List<ScimFlattenedAttribute> UserSchema()
    {
        var attributes = ScimCommonAttributes.For(ScimUrns.User);
        attributes.AddRange(ScimAttributeMapper.FlattenSchema(ScimCoreSchemas.User()));
        attributes.AddRange(ScimAttributeMapper.FlattenSchema(CustomSchema(), "custom"));
        return attributes;
    }

    private static ScimFlattenedAttribute Attribute(string name) => UserSchema().Single(a => a.Name == name);

    #region flattening
    [TestCase("password")]
    [TestCase("custom.pin")]
    [TestCase("custom.recoveryCode")]
    [TestCase("custom.secretQuestion.question")]
    [TestCase("custom.secretQuestion.answer")]
    public void Flatten_WriteOnlyOrNeverReturned_IsMarkedWriteOnly(string name)
    {
        Assert.That(Attribute(name).IsWriteOnly, Is.True);
    }

    [TestCase("userName")]
    [TestCase("name.givenName")]
    [TestCase("emails.work")]
    [TestCase("custom.costCentre")]
    [TestCase("id")]
    public void Flatten_OrdinaryAttribute_IsNotWriteOnly(string name)
    {
        Assert.That(Attribute(name).IsWriteOnly, Is.False);
    }

    [Test]
    public void Flatten_WriteOnlyAttribute_StaysInTheSchema()
    {
        // Dropping it here would hide it from the credential denylist's report and from the service provider's
        // intake, both of which have to see it to refuse it.
        Assert.That(UserSchema().Select(a => a.Name), Does.Contain("password"));
    }
    #endregion

    #region PATCH
    [TestCase("password")]
    [TestCase("custom.pin")]
    public void Build_ChangeToAWriteOnlyAttribute_IsRefusedAndNothingIsBuilt(string name)
    {
        var result = ScimPatchBuilder.Build(
        [
            new ScimAttributeChange("title", ScimPatchOperations.Replace, "Engineer"),
            new ScimAttributeChange(name, ScimPatchOperations.Replace, "S3cret!")
        ], UserSchema());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.WriteOnlyAttributes, Is.EqualTo(new[] { name }));
            Assert.That(result.UnknownAttributes, Is.Empty);
            Assert.That(result.Operations.Select(o => o.Path), Does.Not.Contain(name));
        }
    }

    [Test]
    public void Build_RemovingAWriteOnlyAttribute_IsRefusedToo()
    {
        var result = ScimPatchBuilder.Build([new ScimAttributeChange("password", ScimPatchOperations.Remove, null)], UserSchema());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.WriteOnlyAttributes, Is.EqualTo(new[] { "password" }));
            Assert.That(result.Operations, Is.Empty);
        }
    }
    #endregion

    #region POST and PUT
    [Test]
    public void BuildResource_WriteOnlyAttribute_IsRefusedRatherThanSilentlyDropped()
    {
        // A read-only attribute is left out of a create; a write-only one has to fail it, or JIM would record a
        // password as exported that was never sent.
        var result = ScimResourceWriter.BuildResource(
        [
            new ScimAttributeWrite("userName", ["alice"]),
            new ScimAttributeWrite("password", ["S3cret!"])
        ], UserSchema(), ScimUrns.User);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.WriteOnlyAttributes, Is.EqualTo(new[] { "password" }));
            Assert.That(result.Resource.ToJsonString(), Does.Not.Contain("S3cret!").And.Not.Contain("password"));
        }
    }

    [Test]
    public void ApplyChanges_WriteOnlyAttribute_IsRefusedAndTheResourceKeepsNoValue()
    {
        var resource = JsonNode.Parse("""{ "id": "alice", "userName": "alice" }""")!.AsObject();

        var result = ScimResourceWriter.ApplyChanges(resource, [new ScimAttributeChange("password", ScimPatchOperations.Replace, "S3cret!")], UserSchema());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.WriteOnlyAttributes, Is.EqualTo(new[] { "password" }));
            Assert.That(resource.ToJsonString(), Does.Not.Contain("S3cret!"));
        }
    }
    #endregion
}
