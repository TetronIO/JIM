// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Models.Tests.Staging;

/// <summary>
/// A Connected System Object Type and its classification tags point at each other, and the pair is reachable from
/// the REST API's responses. Nothing in the model layer breaks that cycle for a serialiser, so the back-reference
/// has to be excluded at the property.
/// </summary>
/// <remarks>
/// Guarding this at all is worth a word, because the cost of not doing so is not a wrong payload; it is no product.
/// The jim.web image bakes its OpenAPI document in at build time, and the schema generator walks the type graph
/// with no cycle breaking: it followed this pair to System.Text.Json's 256-level depth limit, failed the whole
/// document, and failed the image build with it. None of the seven checks required to merge builds that image and
/// the local build alias skips the stage, so the break reached main and sat there.
/// </remarks>
[TestFixture]
public class ConnectedSystemObjectTypeTagSerialisationTests
{
    private static ConnectedSystemObjectType TaggedObjectType()
    {
        var objectType = new ConnectedSystemObjectType { Id = 1, Name = "user" };
        objectType.Tags.Add(new ConnectedSystemObjectTypeTag
        {
            Id = 1,
            Key = ObjectTypeTags.Keys.ClassKind,
            Value = ObjectTypeTags.Values.ClassKindStructural,
            ConnectedSystemObjectTypeId = objectType.Id,
            ConnectedSystemObjectType = objectType
        });

        return objectType;
    }

    [Test]
    public void ConnectedSystemObjectType_WithTags_SerialisesWithoutCyclingBackThroughThem()
    {
        var objectType = TaggedObjectType();

        Assert.That(() => JsonSerializer.Serialize(objectType), Throws.Nothing);
    }

    [Test]
    public void ConnectedSystemObjectTypeTag_SerialisedAlone_OmitsItsParentRatherThanRepeatingIt()
    {
        // A tag is only ever reached as a child of its Object Type, so writing the parent back out says nothing a
        // caller does not already have; it only offers the serialiser somewhere to loop.
        var tag = TaggedObjectType().Tags[0];

        var json = JsonSerializer.Serialize(tag);

        // Matched with the closing quote and colon, so the scalar foreign key beside it, whose name has the
        // navigation's name as a prefix, does not satisfy the assertion on its own.
        Assert.That(json, Does.Not.Contain($"\"{nameof(ConnectedSystemObjectTypeTag.ConnectedSystemObjectType)}\":"));
    }
}
