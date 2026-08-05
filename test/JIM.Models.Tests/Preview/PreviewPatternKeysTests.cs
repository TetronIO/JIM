// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Preview;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace JIM.Models.Tests.Preview;

/// <summary>
/// The pattern vocabulary shared by every surface (#827 Phase 4b).
///
/// The stored key is a stable identifier that scripts match on, so it must never drift; the display name is what an
/// administrator reads. The pairing is what this fixture guards: a new detector whose key nobody added a display
/// name for would render as nothing at all in the portal, and the omission would be invisible.
/// </summary>
[TestFixture]
public class PreviewPatternKeysTests
{
    private static IEnumerable<string> AllKeys => typeof(PreviewPatternKeys)
        .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
        .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
        .Select(f => (string)f.GetRawConstantValue()!);

    [Test]
    public void GetDisplayName_EveryDeclaredKey_HasOne()
    {
        var keys = AllKeys.ToList();

        Assert.That(keys, Is.Not.Empty, "the reflection above is the whole test; if it finds nothing it is broken, not passing");
        Assert.Multiple(() =>
        {
            foreach (var key in keys)
            {
                Assert.That(PreviewPatternKeys.GetDisplayName(key), Is.Not.Null.And.Not.Empty,
                    $"{key} has no display name, so the portal would show a detected pattern as blank");
            }
        });
    }

    [Test]
    public void Keys_AreDistinct()
    {
        var keys = AllKeys.ToList();

        Assert.That(keys.Distinct(), Has.Exactly(keys.Count).Items,
            "two patterns sharing a key are indistinguishable once persisted");
    }

    [Test]
    public void GetDisplayName_AKeyThisBuildDoesNotKnow_IsNull()
    {
        Assert.That(PreviewPatternKeys.GetDisplayName("SomethingElseEntirely"), Is.Null,
            "an unrecognised key is not something to put in front of an administrator");
    }

    [Test]
    public void GetDisplayName_NoPattern_IsNull()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PreviewPatternKeys.GetDisplayName(null), Is.Null);
            Assert.That(PreviewPatternKeys.GetDisplayName(string.Empty), Is.Null);
        });
    }
}
