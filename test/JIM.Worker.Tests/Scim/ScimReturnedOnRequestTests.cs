// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Scim;
using JIM.Scim.Discovery;
using JIM.Scim.Schema;

namespace JIM.Worker.Tests.Scim;

/// <summary>
/// An attribute the provider declares <c>returned: request</c> (RFC 7643 section 7) comes back only when a request names
/// it in <c>attributes</c> (#2094). Keycloak declares a Group's <c>members</c> this way, so flattening has to carry the
/// declaration through for import to ask for it.
/// </summary>
public class ScimReturnedOnRequestTests
{
    private const string CustomUrn = "urn:example:params:scim:schemas:extension:custom:2.0:Group";

    private static ScimSchema GroupSchemaReturningMembersOnRequest()
    {
        var schema = ScimCoreSchemas.Group();
        schema.Attributes.Single(a => a.Name == "members").Returned = "request";
        return schema;
    }

    private static ScimSchema CustomSchema() => new()
    {
        Id = CustomUrn,
        Name = "Custom",
        Attributes =
        [
            new ScimSchemaAttribute
            {
                Name = "owner",
                Type = ScimAttributeTypes.Complex,
                Returned = "request",
                SubAttributes =
                [
                    new ScimSchemaAttribute { Name = "name", Type = ScimAttributeTypes.String },
                    new ScimSchemaAttribute { Name = "email", Type = ScimAttributeTypes.String }
                ]
            },
            new ScimSchemaAttribute { Name = "costCentre", Type = ScimAttributeTypes.String, Returned = "Request" },
            new ScimSchemaAttribute { Name = "region", Type = ScimAttributeTypes.String }
        ]
    };

    private static List<ScimFlattenedAttribute> GroupSchema()
    {
        var attributes = ScimCommonAttributes.For(ScimUrns.Group);
        attributes.AddRange(ScimAttributeMapper.FlattenSchema(GroupSchemaReturningMembersOnRequest()));
        attributes.AddRange(ScimAttributeMapper.FlattenSchema(CustomSchema(), "custom"));
        return attributes;
    }

    private static ScimFlattenedAttribute Attribute(string name) => GroupSchema().Single(a => a.Name == name);

    [TestCase("members")]
    [TestCase("custom.costCentre")]
    [TestCase("custom.owner.name")]
    [TestCase("custom.owner.email")]
    public void Flatten_ReturnedOnRequest_IsMarkedReturnedOnRequest(string name)
    {
        Assert.That(Attribute(name).IsReturnedOnRequest, Is.True);
    }

    [TestCase("displayName")]
    [TestCase("custom.region")]
    [TestCase("id")]
    [TestCase("meta.lastModified")]
    public void Flatten_ReturnedByDefault_IsNotMarkedReturnedOnRequest(string name)
    {
        Assert.That(Attribute(name).IsReturnedOnRequest, Is.False);
    }
}
