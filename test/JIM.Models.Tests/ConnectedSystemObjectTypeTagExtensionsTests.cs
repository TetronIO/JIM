// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using NUnit.Framework;
using System.Linq;

namespace JIM.Models.Tests;

/// <summary>
/// Hiding an object type is the one thing this classification does that a user can see, so the question "is this
/// internal?" is answered in one place and pinned here. Getting it wrong in the permissive direction shows noise;
/// getting it wrong in the other direction hides a class an administrator needs, which is far worse.
/// </summary>
[TestFixture]
public class ConnectedSystemObjectTypeTagExtensionsTests
{
    [Test]
    public void IsInternal_WhenTheTypeCarriesTheInternalVisibilityTag_IsTrue()
    {
        var objectType = ObjectTypeWithTags((ObjectTypeTags.Keys.Visibility, ObjectTypeTags.Values.VisibilityInternal));

        Assert.That(objectType.IsInternal(), Is.True);
    }

    [Test]
    public void IsInternal_WhenTheTypeCarriesNoTagsAtAll_IsFalse()
    {
        // An unclassified object type must always be shown. Connectors that do not classify visibility (the File and
        // SCIM connectors today) rely on this.
        Assert.That(ObjectTypeWithTags().IsInternal(), Is.False);
    }

    [Test]
    public void IsInternal_WhenTheTypeIsClassifiedOnlyByClassKind_IsFalse()
    {
        var objectType = ObjectTypeWithTags((ObjectTypeTags.Keys.ClassKind, ObjectTypeTags.Values.ClassKindStructural));

        Assert.That(objectType.IsInternal(), Is.False);
    }

    [Test]
    public void IsInternal_WhenTheTypeIsExplicitlyStandard_IsFalse()
    {
        var objectType = ObjectTypeWithTags((ObjectTypeTags.Keys.Visibility, ObjectTypeTags.Values.VisibilityStandard));

        Assert.That(objectType.IsInternal(), Is.False);
    }

    [Test]
    public void IsInternal_WhenAnotherKeyCarriesTheInternalValue_IsFalse()
    {
        // A connector may add keys of its own alongside JIM's. The value alone must not decide the answer.
        var objectType = ObjectTypeWithTags(("some-connector-key", ObjectTypeTags.Values.VisibilityInternal));

        Assert.That(objectType.IsInternal(), Is.False);
    }

    [Test]
    public void IsInternal_WhenTheInternalTagSitsAlongsideOthers_IsTrue()
    {
        var objectType = ObjectTypeWithTags(
            (ObjectTypeTags.Keys.ClassKind, ObjectTypeTags.Values.ClassKindStructural),
            (ObjectTypeTags.Keys.Visibility, ObjectTypeTags.Values.VisibilityInternal));

        Assert.That(objectType.IsInternal(), Is.True);
    }

    private static ConnectedSystemObjectType ObjectTypeWithTags(params (string Key, string Value)[] tags)
    {
        return new ConnectedSystemObjectType
        {
            Name = "testObjectClass",
            Tags = tags.Select(tag => new ConnectedSystemObjectTypeTag { Key = tag.Key, Value = tag.Value }).ToList()
        };
    }
}
