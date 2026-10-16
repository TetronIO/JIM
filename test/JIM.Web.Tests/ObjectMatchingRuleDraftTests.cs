// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The portal's Object Matching Rule draft takes its case sensitivity from the chosen source until the administrator
/// sets it (#2051), so a rule composed in the portal defaults exactly as one created through the REST API.
/// </summary>
[TestFixture]
public class ObjectMatchingRuleDraftTests
{
    private static readonly ConnectedSystemObjectTypeAttribute CaseExactUid =
        new() { Id = 1, Name = "uid", Type = AttributeDataType.Text, CaseExact = true };

    private static readonly ConnectedSystemObjectTypeAttribute Mail =
        new() { Id = 2, Name = "mail", Type = AttributeDataType.Text };

    [Test]
    public void ChoosingACaseExactSourceAttribute_RespectsCase()
    {
        var draft = new ObjectMatchingRuleDraft { SourceAttribute = CaseExactUid };

        Assert.That(draft.CaseSensitive, Is.True);
    }

    [Test]
    public void ChangingToASourceThatIgnoresCase_FollowsIt()
    {
        var draft = new ObjectMatchingRuleDraft { SourceAttribute = CaseExactUid };

        draft.SourceAttribute = Mail;

        Assert.That(draft.CaseSensitive, Is.False);
    }

    [Test]
    public void SwitchingToAnExpressionSource_IgnoresCase()
    {
        var draft = new ObjectMatchingRuleDraft { SourceAttribute = CaseExactUid };

        draft.SourceKind = ObjectMatchingRuleSourceKind.Expression;

        Assert.That(draft.CaseSensitive, Is.False);
    }

    [Test]
    public void AnAdministratorsChoice_IsNotOverwrittenByALaterSourceChange()
    {
        var draft = new ObjectMatchingRuleDraft { SourceAttribute = Mail };
        draft.CaseSensitive = false;

        draft.SourceAttribute = CaseExactUid;

        Assert.That(draft.CaseSensitive, Is.False);
    }

    [Test]
    public void Reset_ReturnsToFollowingTheSource()
    {
        var draft = new ObjectMatchingRuleDraft { SourceAttribute = Mail };
        draft.CaseSensitive = true;

        draft.Reset();
        draft.SourceAttribute = Mail;

        Assert.That(draft.CaseSensitive, Is.EqualTo(ObjectMatchingDefaults.CaseSensitive));
    }
}
