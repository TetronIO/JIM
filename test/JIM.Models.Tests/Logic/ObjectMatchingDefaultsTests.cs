// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Models.Tests.Logic;

/// <summary>
/// The Object Matching Rule's case-sensitivity default follows its sources' case-exact declaration (#2051): a rule
/// reading an attribute whose values are case-exact compares exactly; every other rule ignores case.
/// </summary>
[TestFixture]
public class ObjectMatchingDefaultsTests
{
    [Test]
    public void CaseSensitiveFor_ACaseExactSourceAttribute_RespectsCase()
    {
        Assert.That(ObjectMatchingDefaults.CaseSensitiveFor([Source(caseExact: true)]), Is.True);
    }

    [Test]
    public void CaseSensitiveFor_ASourceAttributeThatIgnoresCase_IgnoresCase()
    {
        Assert.That(ObjectMatchingDefaults.CaseSensitiveFor([Source(caseExact: false)]), Is.False);
    }

    [Test]
    public void CaseSensitiveFor_AnExpressionSource_IgnoresCase()
    {
        // An expression computes its value; no schema declares how it compares.
        Assert.That(ObjectMatchingDefaults.CaseSensitiveFor([new ObjectMatchingRuleSource { Expression = "cs[\"uid\"]" }]), Is.False);
    }

    [Test]
    public void CaseSensitiveFor_AnyCaseExactSourceAmongSeveral_RespectsCase()
    {
        // Joining two different identities is worse than failing to join one, so a single case-exact source is
        // enough to compare exactly.
        Assert.That(ObjectMatchingDefaults.CaseSensitiveFor(
            [new ObjectMatchingRuleSource { Expression = "cs[\"uid\"]" }, Source(caseExact: false), Source(caseExact: true)]), Is.True);
    }

    [Test]
    public void CaseSensitiveFor_NoSources_TakesTheDefault()
    {
        Assert.That(ObjectMatchingDefaults.CaseSensitiveFor(Array.Empty<ObjectMatchingRuleSource>()), Is.EqualTo(ObjectMatchingDefaults.CaseSensitive));
    }

    private static ObjectMatchingRuleSource Source(bool caseExact) => new()
    {
        ConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Name = "uid", Type = AttributeDataType.Text, CaseExact = caseExact }
    };
}
