// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Scim;
using JIM.Scim.Discovery;
using JIM.Scim.Schema;

namespace JIM.Worker.Tests.Scim;

/// <summary>
/// A provider that publishes an RFC 7643 core attribute as complex with no sub-attributes (#2094). Keycloak 26.8 does
/// this for a Group's <c>members</c>, and a User's <c>groups</c> and <c>emails</c>. A complex attribute with nothing inside
/// would flatten to nothing, so the Connected System would have no group membership to import or export at all; the
/// RFC's own definition of the attribute fills the gap, keeping what the provider did say about it.
/// </summary>
public class ScimEmptyComplexAttributeTests
{
    private static ScimSchemaAttribute EmptyComplex(string name, string returned = "default") => new()
    {
        Name = name,
        Type = ScimAttributeTypes.Complex,
        MultiValued = true,
        Mutability = ScimMutability.ReadWrite,
        Returned = returned
    };

    private static ScimResourceTypeComposition ComposeKeycloakLike()
    {
        var group = new ScimSchema
        {
            Id = ScimUrns.Group,
            Name = "Group",
            Attributes =
            [
                new ScimSchemaAttribute { Name = "displayName", Type = ScimAttributeTypes.String },
                EmptyComplex("members", returned: "request")
            ]
        };
        var user = new ScimSchema
        {
            Id = ScimUrns.User,
            Name = "User",
            Attributes =
            [
                new ScimSchemaAttribute { Name = "userName", Type = ScimAttributeTypes.String },
                EmptyComplex("emails"),
                EmptyComplex("groups", returned: "request")
            ]
        };

        return ScimResourceTypeComposer.Compose(
            [
                new ScimResourceType { Name = "Group", Endpoint = "/Groups", Schema = ScimUrns.Group },
                new ScimResourceType { Name = "User", Endpoint = "/Users", Schema = ScimUrns.User }
            ],
            [group, user]);
    }

    [Test]
    public void Compose_CoreReferenceAttributeWithNoSubAttributes_IsAReferenceKeepingWhatTheProviderSaid()
    {
        var members = ComposeKeycloakLike().FlattenedAttributes["Group"].SingleOrDefault(a => a.Name == "members");

        Assert.That(members, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(members!.Type, Is.EqualTo(AttributeDataType.Reference));
            Assert.That(members!.AttributePlurality, Is.EqualTo(AttributePlurality.MultiValued));
            Assert.That(members!.Writability, Is.EqualTo(AttributeWritability.Writable));
            Assert.That(members!.IsReturnedOnRequest, Is.True, "the provider's returned: request is kept");
        }
    }

    [Test]
    public void Compose_CoreMultiValuedAttributeWithNoSubAttributes_IsFlattenedAsTheRfcDefinesIt()
    {
        var names = ComposeKeycloakLike().FlattenedAttributes["User"].Select(a => a.Name).ToList();

        Assert.That(names, Does.Contain("emails.work").And.Contain("emails.primary").And.Contain("groups"));
    }

    [Test]
    public void Compose_CoreAttributeWithNoSubAttributes_WarnsThatTheRfcDefinitionWasUsed()
    {
        var warnings = ComposeKeycloakLike().Warnings;

        Assert.That(warnings, Has.Some.Contains("members").And.Some.Contains("RFC 7643"));
    }

    [Test]
    public void Compose_ExtensionComplexAttributeWithNoSubAttributes_IsStillLeftOut()
    {
        // JIM has no definition to borrow for an attribute it does not know.
        var custom = new ScimSchema
        {
            Id = "urn:example:params:scim:schemas:extension:custom:2.0:User",
            Name = "Custom",
            Attributes = [EmptyComplex("badges")]
        };

        var composition = ScimResourceTypeComposer.Compose(
            [
                new ScimResourceType
                {
                    Name = "User", Endpoint = "/Users", Schema = ScimUrns.User,
                    SchemaExtensions = [new ScimSchemaExtension { Schema = custom.Id }]
                }
            ],
            [ScimCoreSchemas.User(), custom]);

        Assert.That(composition.FlattenedAttributes["User"].Select(a => a.Name), Has.None.StartsWith("custom.badges"));
    }
}
