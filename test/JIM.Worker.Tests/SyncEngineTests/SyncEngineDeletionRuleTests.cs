// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Sync;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// Pure unit tests for SyncEngine.EvaluateMvoDeletionRule and SyncEngine.ShouldCancelScheduledDeletion:
/// no mocking, no database.
/// </summary>
public class SyncEngineDeletionRuleTests
{
    private Application.Servers.SyncEngine _engine = null!;

    [SetUp]
    public void SetUp()
    {
        _engine = new Application.Servers.SyncEngine();
    }

    /// <summary>
    /// Creates an MVO configured with the WhenAuthoritativeSourceDisconnected deletion rule
    /// for the trigger mode matrix tests.
    /// </summary>
    private static MetaverseObject CreateAuthoritativeSourceMvo(
        AuthoritativeSourceTriggerMode triggerMode,
        List<int> triggerSystemIds,
        TimeSpan? gracePeriod = null,
        int? deletionTriggeredBySystemId = null)
    {
        return new MetaverseObject
        {
            Id = Guid.NewGuid(),
            DeletionTriggeredBySystemId = deletionTriggeredBySystemId,
            Type = new MetaverseObjectType
            {
                DeletionRule = MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
                DeletionTriggerMode = triggerMode,
                DeletionTriggerConnectedSystemIds = triggerSystemIds,
                DeletionGracePeriod = gracePeriod
            }
        };
    }

    #region EvaluateMvoDeletionRule: non-authoritative rules

    [Test]
    public void EvaluateMvoDeletionRule_NullType_ReturnsNotDeleted()
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        mvo.Type = null!;

        var decision = _engine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId: 1, remainingConnectedSystemIds: []);

        Assert.That(decision.Fate, Is.EqualTo(MvoDeletionFate.NotDeleted));
    }

    [Test]
    public void EvaluateMvoDeletionRule_InternalOrigin_ReturnsNotDeleted()
    {
        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Origin = MetaverseObjectOrigin.Internal,
            Type = new MetaverseObjectType { DeletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected }
        };

        var decision = _engine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId: 1, remainingConnectedSystemIds: []);

        Assert.That(decision.Fate, Is.EqualTo(MvoDeletionFate.NotDeleted));
    }

    [Test]
    public void EvaluateMvoDeletionRule_ManualRule_ReturnsNotDeleted()
    {
        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = new MetaverseObjectType { DeletionRule = MetaverseObjectDeletionRule.Manual }
        };

        var decision = _engine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId: 1, remainingConnectedSystemIds: []);

        Assert.That(decision.Fate, Is.EqualTo(MvoDeletionFate.NotDeleted));
    }

    [Test]
    public void EvaluateMvoDeletionRule_WhenLastDisconnected_RemainingConnectors_ReturnsNotDeleted()
    {
        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = new MetaverseObjectType { DeletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected }
        };

        var decision = _engine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId: 1, remainingConnectedSystemIds: [2, 3]);

        Assert.That(decision.Fate, Is.EqualTo(MvoDeletionFate.NotDeleted));
    }

    [Test]
    public void EvaluateMvoDeletionRule_WhenLastDisconnected_NoRemaining_NoGracePeriod_ReturnsDeleteImmediately()
    {
        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = new MetaverseObjectType
            {
                DeletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
                DeletionGracePeriod = null
            }
        };

        var decision = _engine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId: 1, remainingConnectedSystemIds: []);

        Assert.That(decision.Fate, Is.EqualTo(MvoDeletionFate.DeletedImmediately));
        Assert.That(decision.Reason, Does.Contain("last connector disconnected"));
    }

    [Test]
    public void EvaluateMvoDeletionRule_WhenLastDisconnected_NoRemaining_ZeroGracePeriod_ReturnsDeleteImmediately()
    {
        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = new MetaverseObjectType
            {
                DeletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
                DeletionGracePeriod = TimeSpan.Zero
            }
        };

        var decision = _engine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId: 1, remainingConnectedSystemIds: []);

        Assert.That(decision.Fate, Is.EqualTo(MvoDeletionFate.DeletedImmediately));
    }

    [Test]
    public void EvaluateMvoDeletionRule_WhenLastDisconnected_NoRemaining_WithGracePeriod_ReturnsDeletionScheduled()
    {
        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = new MetaverseObjectType
            {
                DeletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
                DeletionGracePeriod = TimeSpan.FromDays(7)
            }
        };

        var decision = _engine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId: 1, remainingConnectedSystemIds: []);

        Assert.That(decision.Fate, Is.EqualTo(MvoDeletionFate.DeletionScheduled));
        Assert.That(decision.GracePeriod, Is.EqualTo(TimeSpan.FromDays(7)));
    }

    #endregion

    #region EvaluateMvoDeletionRule: empty source list fallback

    [Test]
    public void EvaluateMvoDeletionRule_AuthoritativeSource_NoTriggerIds_NoRemaining_FallsBackToLastConnector()
    {
        var mvo = CreateAuthoritativeSourceMvo(
            AuthoritativeSourceTriggerMode.AllSourcesDisconnect,
            triggerSystemIds: []);

        var decision = _engine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId: 1, remainingConnectedSystemIds: []);

        Assert.That(decision.Fate, Is.EqualTo(MvoDeletionFate.DeletedImmediately));
    }

    [Test]
    public void EvaluateMvoDeletionRule_AuthoritativeSource_NoTriggerIds_RemainingConnectors_ReturnsNotDeleted()
    {
        var mvo = CreateAuthoritativeSourceMvo(
            AuthoritativeSourceTriggerMode.AllSourcesDisconnect,
            triggerSystemIds: []);

        var decision = _engine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId: 1, remainingConnectedSystemIds: [2]);

        Assert.That(decision.Fate, Is.EqualTo(MvoDeletionFate.NotDeleted));
    }

    #endregion

    #region EvaluateMvoDeletionRule: Specific sources mode

    [Test]
    public void EvaluateMvoDeletionRule_SpecificMode_ListedSourceDisconnects_UnlistedSystemsRemain_ReturnsDeleteImmediately()
    {
        var mvo = CreateAuthoritativeSourceMvo(
            AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect,
            triggerSystemIds: [5]);

        var decision = _engine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId: 5, remainingConnectedSystemIds: [7, 8, 9]);

        Assert.That(decision.Fate, Is.EqualTo(MvoDeletionFate.DeletedImmediately));
    }

    [Test]
    public void EvaluateMvoDeletionRule_AuthoritativeSource_WithSystemName_ReasonNamesTheSystem()
    {
        var mvo = CreateAuthoritativeSourceMvo(
            AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect,
            triggerSystemIds: [5]);

        var decision = _engine.EvaluateMvoDeletionRule(
            mvo, disconnectingSystemId: 5, remainingConnectedSystemIds: [7, 8, 9], disconnectingSystemName: "APAC LDAP");

        Assert.That(decision.Reason, Does.Contain("APAC LDAP"));
        Assert.That(decision.Reason, Does.Not.Contain("system ID"));
    }

    [Test]
    public void EvaluateMvoDeletionRule_AuthoritativeSource_WithoutSystemName_ReasonFallsBackToId()
    {
        var mvo = CreateAuthoritativeSourceMvo(
            AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect,
            triggerSystemIds: [5]);

        var decision = _engine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId: 5, remainingConnectedSystemIds: [7, 8, 9]);

        Assert.That(decision.Reason, Does.Contain("system ID 5"));
    }

    [Test]
    public void EvaluateMvoDeletionRule_SpecificMode_ListedSourceDisconnects_AnotherListedSourceRemains_ReturnsDeleteImmediately()
    {
        var mvo = CreateAuthoritativeSourceMvo(
            AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect,
            triggerSystemIds: [5, 6]);

        var decision = _engine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId: 5, remainingConnectedSystemIds: [6, 7]);

        Assert.That(decision.Fate, Is.EqualTo(MvoDeletionFate.DeletedImmediately));
    }

    [Test]
    public void EvaluateMvoDeletionRule_SpecificMode_ListedSourceDisconnects_NoRemaining_ReturnsDeleteImmediately()
    {
        var mvo = CreateAuthoritativeSourceMvo(
            AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect,
            triggerSystemIds: [5]);

        var decision = _engine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId: 5, remainingConnectedSystemIds: []);

        Assert.That(decision.Fate, Is.EqualTo(MvoDeletionFate.DeletedImmediately));
    }

    [Test]
    public void EvaluateMvoDeletionRule_SpecificMode_UnlistedSystemDisconnects_ListedSourceRemains_ReturnsNotDeleted()
    {
        var mvo = CreateAuthoritativeSourceMvo(
            AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect,
            triggerSystemIds: [5]);

        var decision = _engine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId: 99, remainingConnectedSystemIds: [5]);

        Assert.That(decision.Fate, Is.EqualTo(MvoDeletionFate.NotDeleted));
    }

    [Test]
    public void EvaluateMvoDeletionRule_SpecificMode_ListedSourceDisconnects_WithGracePeriod_ReturnsDeletionScheduled()
    {
        var mvo = CreateAuthoritativeSourceMvo(
            AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect,
            triggerSystemIds: [5],
            gracePeriod: TimeSpan.FromDays(14));

        var decision = _engine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId: 5, remainingConnectedSystemIds: [7]);

        Assert.That(decision.Fate, Is.EqualTo(MvoDeletionFate.DeletionScheduled));
        Assert.That(decision.GracePeriod, Is.EqualTo(TimeSpan.FromDays(14)));
    }

    [Test]
    public void EvaluateMvoDeletionRule_SpecificMode_ListedSourceDisconnects_ReasonNamesSpecificSourcesMode()
    {
        var mvo = CreateAuthoritativeSourceMvo(
            AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect,
            triggerSystemIds: [5, 6]);

        var decision = _engine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId: 5, remainingConnectedSystemIds: [6]);

        Assert.That(decision.Reason, Does.Contain("Specific sources mode"));
        Assert.That(decision.Reason, Does.Contain("system ID 5"));
    }

    #endregion

    #region EvaluateMvoDeletionRule: All sources mode

    [Test]
    public void EvaluateMvoDeletionRule_AllMode_ListedSourceDisconnects_AnotherListedSourceRemains_ReturnsNotDeleted()
    {
        var mvo = CreateAuthoritativeSourceMvo(
            AuthoritativeSourceTriggerMode.AllSourcesDisconnect,
            triggerSystemIds: [5, 6]);

        var decision = _engine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId: 5, remainingConnectedSystemIds: [6, 7]);

        Assert.That(decision.Fate, Is.EqualTo(MvoDeletionFate.NotDeleted));
    }

    [Test]
    public void EvaluateMvoDeletionRule_AllMode_ListedSourceDisconnects_OnlyUnlistedSystemsRemain_ReturnsDeleteImmediately()
    {
        var mvo = CreateAuthoritativeSourceMvo(
            AuthoritativeSourceTriggerMode.AllSourcesDisconnect,
            triggerSystemIds: [5, 6]);

        var decision = _engine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId: 5, remainingConnectedSystemIds: [7, 8]);

        Assert.That(decision.Fate, Is.EqualTo(MvoDeletionFate.DeletedImmediately));
    }

    [Test]
    public void EvaluateMvoDeletionRule_AllMode_LastListedSourceDisconnects_NoRemaining_ReturnsDeleteImmediately()
    {
        var mvo = CreateAuthoritativeSourceMvo(
            AuthoritativeSourceTriggerMode.AllSourcesDisconnect,
            triggerSystemIds: [5, 6]);

        var decision = _engine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId: 6, remainingConnectedSystemIds: []);

        Assert.That(decision.Fate, Is.EqualTo(MvoDeletionFate.DeletedImmediately));
    }

    [Test]
    public void EvaluateMvoDeletionRule_AllMode_UnlistedSystemDisconnects_ListedSourceRemains_ReturnsNotDeleted()
    {
        var mvo = CreateAuthoritativeSourceMvo(
            AuthoritativeSourceTriggerMode.AllSourcesDisconnect,
            triggerSystemIds: [5]);

        var decision = _engine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId: 99, remainingConnectedSystemIds: [5]);

        Assert.That(decision.Fate, Is.EqualTo(MvoDeletionFate.NotDeleted));
    }

    [Test]
    public void EvaluateMvoDeletionRule_AllMode_UnlistedSystemDisconnects_NoListedSourcesRemain_ReturnsNotDeleted()
    {
        // The disconnecting system is not a listed source, so it never triggers deletion,
        // even when no listed source remains connected.
        var mvo = CreateAuthoritativeSourceMvo(
            AuthoritativeSourceTriggerMode.AllSourcesDisconnect,
            triggerSystemIds: [5]);

        var decision = _engine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId: 99, remainingConnectedSystemIds: [7]);

        Assert.That(decision.Fate, Is.EqualTo(MvoDeletionFate.NotDeleted));
    }

    [Test]
    public void EvaluateMvoDeletionRule_AllMode_DisconnectingSystemHasSecondCsoRemaining_ReturnsNotDeleted()
    {
        // The disconnecting system still has a second joined CSO, so its id remains in the
        // remaining list and the "all sources gone" condition does not hold.
        var mvo = CreateAuthoritativeSourceMvo(
            AuthoritativeSourceTriggerMode.AllSourcesDisconnect,
            triggerSystemIds: [5]);

        var decision = _engine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId: 5, remainingConnectedSystemIds: [5]);

        Assert.That(decision.Fate, Is.EqualTo(MvoDeletionFate.NotDeleted));
    }

    [Test]
    public void EvaluateMvoDeletionRule_AllMode_LastListedSourceDisconnects_WithGracePeriod_ReturnsDeletionScheduled()
    {
        var mvo = CreateAuthoritativeSourceMvo(
            AuthoritativeSourceTriggerMode.AllSourcesDisconnect,
            triggerSystemIds: [5, 6],
            gracePeriod: TimeSpan.FromDays(30));

        var decision = _engine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId: 5, remainingConnectedSystemIds: [7]);

        Assert.That(decision.Fate, Is.EqualTo(MvoDeletionFate.DeletionScheduled));
        Assert.That(decision.GracePeriod, Is.EqualTo(TimeSpan.FromDays(30)));
    }

    [Test]
    public void EvaluateMvoDeletionRule_AllMode_ListedSourceDisconnects_SourcesRemain_ReasonNamesAllSourcesMode()
    {
        var mvo = CreateAuthoritativeSourceMvo(
            AuthoritativeSourceTriggerMode.AllSourcesDisconnect,
            triggerSystemIds: [5, 6]);

        var decision = _engine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId: 5, remainingConnectedSystemIds: [6, 7]);

        Assert.That(decision.Reason, Does.Contain("All sources mode"));
        Assert.That(decision.Reason, Does.Contain("1 of 2 sources remains connected"));
    }

    [Test]
    public void EvaluateMvoDeletionRule_AllMode_LastListedSourceDisconnects_ReasonNamesAllSourcesMode()
    {
        var mvo = CreateAuthoritativeSourceMvo(
            AuthoritativeSourceTriggerMode.AllSourcesDisconnect,
            triggerSystemIds: [5, 6]);

        var decision = _engine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId: 6, remainingConnectedSystemIds: [7]);

        Assert.That(decision.Reason, Does.Contain("All sources mode"));
        Assert.That(decision.Reason, Does.Contain("system ID 6"));
    }

    #endregion

    #region ShouldCancelScheduledDeletion

    [Test]
    public void ShouldCancelScheduledDeletion_LastConnectorRule_AnyRejoiningSystem_ReturnsTrue()
    {
        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = new MetaverseObjectType { DeletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected }
        };

        Assert.That(_engine.ShouldCancelScheduledDeletion(mvo, rejoiningSystemId: 99), Is.True);
    }

    [Test]
    public void ShouldCancelScheduledDeletion_SpecificMode_TriggeringSystemRejoins_ReturnsTrue()
    {
        var mvo = CreateAuthoritativeSourceMvo(
            AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect,
            triggerSystemIds: [5, 6],
            deletionTriggeredBySystemId: 5);

        Assert.That(_engine.ShouldCancelScheduledDeletion(mvo, rejoiningSystemId: 5), Is.True);
    }

    [Test]
    public void ShouldCancelScheduledDeletion_SpecificMode_OtherListedSourceRejoins_ReturnsFalse()
    {
        var mvo = CreateAuthoritativeSourceMvo(
            AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect,
            triggerSystemIds: [5, 6],
            deletionTriggeredBySystemId: 5);

        Assert.That(_engine.ShouldCancelScheduledDeletion(mvo, rejoiningSystemId: 6), Is.False);
    }

    [Test]
    public void ShouldCancelScheduledDeletion_SpecificMode_UnlistedSystemRejoins_ReturnsFalse()
    {
        var mvo = CreateAuthoritativeSourceMvo(
            AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect,
            triggerSystemIds: [5, 6],
            deletionTriggeredBySystemId: 5);

        Assert.That(_engine.ShouldCancelScheduledDeletion(mvo, rejoiningSystemId: 99), Is.False);
    }

    [Test]
    public void ShouldCancelScheduledDeletion_AllMode_TriggeringSystemRejoins_ReturnsTrue()
    {
        var mvo = CreateAuthoritativeSourceMvo(
            AuthoritativeSourceTriggerMode.AllSourcesDisconnect,
            triggerSystemIds: [5, 6],
            deletionTriggeredBySystemId: 5);

        Assert.That(_engine.ShouldCancelScheduledDeletion(mvo, rejoiningSystemId: 5), Is.True);
    }

    [Test]
    public void ShouldCancelScheduledDeletion_AllMode_AnyListedSourceRejoins_ReturnsTrue()
    {
        var mvo = CreateAuthoritativeSourceMvo(
            AuthoritativeSourceTriggerMode.AllSourcesDisconnect,
            triggerSystemIds: [5, 6],
            deletionTriggeredBySystemId: 5);

        Assert.That(_engine.ShouldCancelScheduledDeletion(mvo, rejoiningSystemId: 6), Is.True);
    }

    [Test]
    public void ShouldCancelScheduledDeletion_AllMode_UnlistedSystemRejoins_ReturnsFalse()
    {
        var mvo = CreateAuthoritativeSourceMvo(
            AuthoritativeSourceTriggerMode.AllSourcesDisconnect,
            triggerSystemIds: [5, 6],
            deletionTriggeredBySystemId: 5);

        Assert.That(_engine.ShouldCancelScheduledDeletion(mvo, rejoiningSystemId: 99), Is.False);
    }

    [Test]
    public void ShouldCancelScheduledDeletion_NullDeletionTriggeredBySystemId_ReturnsTrue()
    {
        // Rows marked before #119 shipped carry no recorded triggering system; fall back to the
        // pre-existing cancel-on-any-rejoin behaviour rather than stranding a scheduled deletion.
        var mvo = CreateAuthoritativeSourceMvo(
            AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect,
            triggerSystemIds: [5, 6],
            deletionTriggeredBySystemId: null);

        Assert.That(_engine.ShouldCancelScheduledDeletion(mvo, rejoiningSystemId: 99), Is.True);
    }

    [Test]
    public void ShouldCancelScheduledDeletion_AuthoritativeSource_EmptySourceList_ReturnsTrue()
    {
        // With no sources configured, scheduling fell back to WhenLastConnectorDisconnected
        // semantics, so cancellation follows the same any-rejoin rule.
        var mvo = CreateAuthoritativeSourceMvo(
            AuthoritativeSourceTriggerMode.AllSourcesDisconnect,
            triggerSystemIds: [],
            deletionTriggeredBySystemId: 5);

        Assert.That(_engine.ShouldCancelScheduledDeletion(mvo, rejoiningSystemId: 99), Is.True);
    }

    [Test]
    public void ShouldCancelScheduledDeletion_ManualRule_ReturnsTrue()
    {
        // A Manual-rule MVO should never carry a disconnection-scheduled deletion; if one exists
        // the state is inconsistent, and cancelling on rejoin clears it (matches the pre-#119
        // cancel-on-any-rejoin behaviour).
        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = new MetaverseObjectType { DeletionRule = MetaverseObjectDeletionRule.Manual }
        };

        Assert.That(_engine.ShouldCancelScheduledDeletion(mvo, rejoiningSystemId: 1), Is.True);
    }

    [Test]
    public void ShouldCancelScheduledDeletion_NullType_ReturnsTrue()
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        mvo.Type = null!;

        Assert.That(_engine.ShouldCancelScheduledDeletion(mvo, rejoiningSystemId: 1), Is.True);
    }

    #endregion
}
