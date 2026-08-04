// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Preview;
using NUnit.Framework;
using System;
using System.Collections.Generic;

namespace JIM.Models.Tests.Preview;

/// <summary>
/// Whether two deletion settings proposals describe the same settings (#1114).
///
/// This is what decides whether a preview on screen still answers the question the administrator is about to ask.
/// Getting it wrong in one direction labels a valid preview stale, which is merely irritating; getting it wrong in
/// the other leaves a stale preview presented as current, which is how a change gets approved on the strength of
/// numbers about different settings.
/// </summary>
[TestFixture]
public class MetaverseObjectTypeDeletionSettingsProposalTests
{
    [Test]
    public void DescribesSameSettingsAs_IdenticalValuesInDifferentInstances_IsTrue()
    {
        var a = Proposal(triggerIds: [1, 2]);
        var b = Proposal(triggerIds: [1, 2]);

        Assert.That(a.DescribesSameSettingsAs(b), Is.True,
            "record equality would compare the two lists by reference and report a false change on every render");
    }

    [Test]
    public void DescribesSameSettingsAs_TriggerSourcesInADifferentOrder_IsTrue()
    {
        var a = Proposal(triggerIds: [1, 2, 3]);
        var b = Proposal(triggerIds: [3, 1, 2]);

        Assert.That(a.DescribesSameSettingsAs(b), Is.True,
            "the sources are a set; the order they were ticked in is not a configuration change");
    }

    [Test]
    public void DescribesSameSettingsAs_NullAndZeroGracePeriod_IsTrue()
    {
        var a = Proposal(gracePeriod: null);
        var b = Proposal(gracePeriod: TimeSpan.Zero);

        Assert.That(a.DescribesSameSettingsAs(b), Is.True,
            "deletion treats both as no grace period, so an edit between them changes nothing and must not invalidate a preview");
    }

    [Test]
    public void DescribesSameSettingsAs_DifferentRule_IsFalse()
    {
        var a = Proposal(rule: MetaverseObjectDeletionRule.WhenLastConnectorDisconnected);
        var b = Proposal(rule: MetaverseObjectDeletionRule.Manual);

        Assert.That(a.DescribesSameSettingsAs(b), Is.False);
    }

    [Test]
    public void DescribesSameSettingsAs_DifferentGracePeriod_IsFalse()
    {
        var a = Proposal(gracePeriod: TimeSpan.FromDays(30));
        var b = Proposal(gracePeriod: TimeSpan.FromDays(60));

        Assert.That(a.DescribesSameSettingsAs(b), Is.False);
    }

    [Test]
    public void DescribesSameSettingsAs_DifferentTriggerSources_IsFalse()
    {
        var a = Proposal(triggerIds: [1, 2]);
        var b = Proposal(triggerIds: [1, 3]);

        Assert.That(a.DescribesSameSettingsAs(b), Is.False);
    }

    [Test]
    public void DescribesSameSettingsAs_ATriggerSourceRemoved_IsFalse()
    {
        var a = Proposal(triggerIds: [1, 2]);
        var b = Proposal(triggerIds: [1]);

        Assert.That(a.DescribesSameSettingsAs(b), Is.False,
            "a subset must not compare equal; the counts are the same only if every source is");
    }

    [Test]
    public void DescribesSameSettingsAs_DifferentTriggerMode_IsFalse()
    {
        var a = Proposal(mode: AuthoritativeSourceTriggerMode.AllSourcesDisconnect);
        var b = Proposal(mode: AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect);

        Assert.That(a.DescribesSameSettingsAs(b), Is.False,
            "the mode moves no deletion date today, but it is still a different proposal from the one previewed");
    }

    [Test]
    public void DescribesSameSettingsAs_Null_IsFalse()
    {
        Assert.That(Proposal().DescribesSameSettingsAs(null), Is.False);
    }

    private static MetaverseObjectTypeDeletionSettingsProposal Proposal(
        MetaverseObjectDeletionRule rule = MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
        TimeSpan? gracePeriod = null,
        IReadOnlyList<int>? triggerIds = null,
        AuthoritativeSourceTriggerMode mode = AuthoritativeSourceTriggerMode.AllSourcesDisconnect) =>
        new(rule, gracePeriod, triggerIds ?? [], mode);
}
