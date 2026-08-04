// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using NUnit.Framework;

namespace JIM.Models.Tests.Core;

/// <summary>
/// The Metaverse half of the shared naming policy (see <see cref="ObjectNaming"/>):
/// <see cref="MetaverseObject.Name"/> resolves Display Name then Common Name, falling back to the
/// denormalised <see cref="MetaverseObject.CachedDisplayName"/> when attribute values are not loaded,
/// and <see cref="MetaverseObject.NameOrId"/> falls through to the id. Metaverse attribute names are
/// curated by JIM, so matching is exact. <see cref="MetaverseObjectHeader"/> must resolve identically.
/// </summary>
[TestFixture]
public class MetaverseObjectNamingTests
{
    private static MetaverseObjectAttributeValue AttributeValue(string attributeName, string? value) => new()
    {
        Id = Guid.NewGuid(),
        Attribute = new MetaverseAttribute { Name = attributeName },
        StringValue = value
    };

    private static MetaverseObject CreateMvo(params MetaverseObjectAttributeValue[] attributeValues)
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        mvo.AttributeValues.AddRange(attributeValues);
        return mvo;
    }

    [Test]
    public void Name_DisplayNamePresent_PrefersDisplayName()
    {
        var mvo = CreateMvo(
            AttributeValue(Constants.BuiltInAttributes.CommonName, "Project-GlobalGateway"),
            AttributeValue(Constants.BuiltInAttributes.DisplayName, "Global Gateway Project"));

        Assert.That(mvo.Name, Is.EqualTo("Global Gateway Project"));
    }

    [Test]
    public void Name_NoDisplayNameButCommonNamePresent_ReturnsCommonName()
    {
        // Group Metaverse Objects commonly carry a Common Name and no Display Name.
        var mvo = CreateMvo(AttributeValue(Constants.BuiltInAttributes.CommonName, "Project-GlobalGateway"));

        Assert.That(mvo.Name, Is.EqualTo("Project-GlobalGateway"));
    }

    [Test]
    public void Name_DisplayNameWhitespaceOnly_FallsThroughToCommonName()
    {
        var mvo = CreateMvo(
            AttributeValue(Constants.BuiltInAttributes.DisplayName, "   "),
            AttributeValue(Constants.BuiltInAttributes.CommonName, "Project-GlobalGateway"));

        Assert.That(mvo.Name, Is.EqualTo("Project-GlobalGateway"));
    }

    [Test]
    public void Name_AttributeValuesNotLoaded_FallsBackToCachedDisplayName()
    {
        // The Metaverse list projects the denormalised sort cache without materialising attribute values.
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), CachedDisplayName = "Erin Byrne" };

        Assert.That(mvo.Name, Is.EqualTo("Erin Byrne"));
    }

    [Test]
    public void Name_AttributeValuesLoadedButNoNameAttributes_FallsBackToCachedDisplayName()
    {
        var mvo = CreateMvo(AttributeValue(Constants.BuiltInAttributes.Department, "Finance"));
        mvo.CachedDisplayName = "Erin Byrne";

        Assert.That(mvo.Name, Is.EqualTo("Erin Byrne"));
    }

    [Test]
    public void Name_NothingResolvable_ReturnsNull()
    {
        var mvo = CreateMvo(AttributeValue(Constants.BuiltInAttributes.Department, "Finance"));

        Assert.That(mvo.Name, Is.Null);
    }

    [Test]
    public void NameOrId_NameAbsent_FallsBackToId()
    {
        var mvo = CreateMvo(AttributeValue(Constants.BuiltInAttributes.Department, "Finance"));

        Assert.That(mvo.NameOrId, Is.EqualTo(mvo.Id.ToString()));
    }

    [Test]
    public void NameOrId_NamePresent_ReturnsName()
    {
        var mvo = CreateMvo(AttributeValue(Constants.BuiltInAttributes.CommonName, "Project-GlobalGateway"));

        Assert.That(mvo.NameOrId, Is.EqualTo("Project-GlobalGateway"));
    }

    [Test]
    public void Header_NoDisplayNameButCommonNamePresent_ResolvesIdenticallyToMetaverseObject()
    {
        var header = new MetaverseObjectHeader { Id = Guid.NewGuid() };
        header.AttributeValues.Add(AttributeValue(Constants.BuiltInAttributes.CommonName, "Project-GlobalGateway"));

        Assert.That(header.Name, Is.EqualTo("Project-GlobalGateway"));
        Assert.That(header.NameOrId, Is.EqualTo("Project-GlobalGateway"));
    }

    [Test]
    public void Header_AttributeValuesNotLoaded_FallsBackToCachedDisplayName()
    {
        var header = new MetaverseObjectHeader { Id = Guid.NewGuid(), CachedDisplayName = "Erin Byrne" };

        Assert.That(header.Name, Is.EqualTo("Erin Byrne"));
    }
}
