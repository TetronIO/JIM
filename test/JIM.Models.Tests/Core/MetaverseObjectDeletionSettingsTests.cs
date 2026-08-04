// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Models.Core;
using NUnit.Framework;

namespace JIM.Models.Tests.Core;

/// <summary>
/// The deletion rule as a scalar function of one object's standing state. This is what a Metaverse Object Type
/// deletion-settings preview (#1114) compares between the current and the proposed settings, and it must answer
/// exactly what the housekeeping sweep would do; a preview that disagrees with the housekeeper is worse than no
/// preview, because it is a confident wrong answer about deletions.
/// </summary>
[TestFixture]
public class MetaverseObjectDeletionSettingsTests
{
    private static readonly DateTime Now = new(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime TenDaysAgo = Now.AddDays(-10);

    [Test]
    public void DeletionEligibleAt_ManualRule_IsNever()
    {
        var settings = new MetaverseObjectDeletionSettings(MetaverseObjectDeletionRule.Manual, TimeSpan.FromDays(7));

        Assert.That(settings.DeletionEligibleAt(TenDaysAgo, hasConnectedSystemObjects: false), Is.Null,
            "Manual means JIM never deletes automatically, whatever else is configured beside it.");
    }

    [Test]
    public void DeletionEligibleAt_NeverDisconnected_IsNever()
    {
        var settings = new MetaverseObjectDeletionSettings(MetaverseObjectDeletionRule.WhenLastConnectorDisconnected, null);

        Assert.That(settings.DeletionEligibleAt(null, hasConnectedSystemObjects: true), Is.Null);
    }

    [Test]
    public void DeletionEligibleAt_LastConnectorRuleWithConnectorsRemaining_IsNever()
    {
        // The object carries a disconnection date from an earlier disconnect but has since been reconnected to, or
        // was marked under a different rule. Either way this rule will not delete an object that still has a
        // connector.
        var settings = new MetaverseObjectDeletionSettings(MetaverseObjectDeletionRule.WhenLastConnectorDisconnected, null);

        Assert.That(settings.DeletionEligibleAt(TenDaysAgo, hasConnectedSystemObjects: true), Is.Null);
    }

    [Test]
    public void DeletionEligibleAt_AuthoritativeSourceRuleWithConnectorsRemaining_IsTheDisconnectionDate()
    {
        // The difference that makes switching between the two automatic rules dangerous: this one deletes an object
        // whose authoritative source has gone even though it still has connectors elsewhere.
        var settings = new MetaverseObjectDeletionSettings(MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected, null);

        Assert.That(settings.DeletionEligibleAt(TenDaysAgo, hasConnectedSystemObjects: true), Is.EqualTo(TenDaysAgo));
    }

    [Test]
    public void DeletionEligibleAt_GracePeriod_IsTheDisconnectionDatePlusTheGracePeriod()
    {
        var settings = new MetaverseObjectDeletionSettings(MetaverseObjectDeletionRule.WhenLastConnectorDisconnected, TimeSpan.FromDays(30));

        Assert.That(settings.DeletionEligibleAt(TenDaysAgo, hasConnectedSystemObjects: false),
            Is.EqualTo(TenDaysAgo.AddDays(30)));
    }

    [Test]
    public void DeletionEligibleAt_ZeroGracePeriod_IsTheDisconnectionDate()
    {
        var settings = new MetaverseObjectDeletionSettings(MetaverseObjectDeletionRule.WhenLastConnectorDisconnected, TimeSpan.Zero);

        Assert.That(settings.DeletionEligibleAt(TenDaysAgo, hasConnectedSystemObjects: false), Is.EqualTo(TenDaysAgo));
    }

    [Test]
    public void IsEligibleAt_GracePeriodStillRunning_IsFalse()
    {
        var settings = new MetaverseObjectDeletionSettings(MetaverseObjectDeletionRule.WhenLastConnectorDisconnected, TimeSpan.FromDays(30));

        Assert.That(settings.IsEligibleAt(TenDaysAgo, hasConnectedSystemObjects: false, Now), Is.False);
    }

    [Test]
    public void IsEligibleAt_GracePeriodElapsed_IsTrue()
    {
        var settings = new MetaverseObjectDeletionSettings(MetaverseObjectDeletionRule.WhenLastConnectorDisconnected, TimeSpan.FromDays(7));

        Assert.That(settings.IsEligibleAt(TenDaysAgo, hasConnectedSystemObjects: false, Now), Is.True);
    }

    [Test]
    public void IsEligibleAt_GracePeriodExpiringExactlyNow_IsTrue()
    {
        // The housekeeping sweep uses "eligible date has arrived", not "has passed"; an object on the boundary is
        // deleted on this pass, so a preview that called it safe would be wrong by one sweep.
        var settings = new MetaverseObjectDeletionSettings(MetaverseObjectDeletionRule.WhenLastConnectorDisconnected, TimeSpan.FromDays(10));

        Assert.That(settings.IsEligibleAt(TenDaysAgo, hasConnectedSystemObjects: false, Now), Is.True);
    }

    [Test]
    public void IsEligibleAt_ManualRule_IsFalseEvenLongAfterDisconnection()
    {
        var settings = new MetaverseObjectDeletionSettings(MetaverseObjectDeletionRule.Manual, null);

        Assert.That(settings.IsEligibleAt(TenDaysAgo, hasConnectedSystemObjects: false, Now), Is.False);
    }
}
