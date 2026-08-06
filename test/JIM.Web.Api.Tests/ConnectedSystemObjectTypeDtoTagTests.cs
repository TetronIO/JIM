// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using System.Linq;
using JIM.Models.Staging;
using JIM.Web.Models.Api;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The REST surface returns every object type a Connected System reported, and says how each was classified. The
/// portal and the PowerShell cmdlet hide internal object types by default; the API does not, because a surface that
/// silently omits discovered data gives an automation author no way to find out what is missing.
/// </summary>
[TestFixture]
public class ConnectedSystemObjectTypeDtoTagTests
{
    [Test]
    public void FromEntity_CarriesTheClassificationTagsThroughToTheDto()
    {
        var objectType = new ConnectedSystemObjectType
        {
            Id = 4,
            Name = "olcGlobal",
            Tags =
            [
                new ConnectedSystemObjectTypeTag { Key = ObjectTypeTags.Keys.ClassKind, Value = ObjectTypeTags.Values.ClassKindStructural },
                new ConnectedSystemObjectTypeTag { Key = ObjectTypeTags.Keys.Visibility, Value = ObjectTypeTags.Values.VisibilityInternal }
            ]
        };

        var dto = ConnectedSystemObjectTypeDto.FromEntity(objectType);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.Tags, Has.Count.EqualTo(2));
            Assert.That(dto.Tags.Any(t => t.Key == ObjectTypeTags.Keys.ClassKind && t.Value == ObjectTypeTags.Values.ClassKindStructural), Is.True);
            Assert.That(dto.Tags.Any(t => t.Key == ObjectTypeTags.Keys.Visibility && t.Value == ObjectTypeTags.Values.VisibilityInternal), Is.True);
        }
    }

    [Test]
    public void FromEntity_ForAnInternalObjectType_ReportsItInternal()
    {
        var objectType = new ConnectedSystemObjectType
        {
            Id = 4,
            Name = "auditAdd",
            Tags = [new ConnectedSystemObjectTypeTag { Key = ObjectTypeTags.Keys.Visibility, Value = ObjectTypeTags.Values.VisibilityInternal }]
        };

        Assert.That(ConnectedSystemObjectTypeDto.FromEntity(objectType).IsInternal, Is.True);
    }

    [Test]
    public void FromEntity_ForAnUnclassifiedObjectType_ReportsEmptyTagsAndNotInternal()
    {
        // Connectors that classify nothing (File, SCIM) must not have their object types treated as internal, and
        // Tags must be an empty collection rather than null so callers can enumerate it unconditionally.
        var objectType = new ConnectedSystemObjectType { Id = 4, Name = "User", Tags = new List<ConnectedSystemObjectTypeTag>() };

        var dto = ConnectedSystemObjectTypeDto.FromEntity(objectType);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.Tags, Is.Empty);
            Assert.That(dto.IsInternal, Is.False);
        }
    }
}
