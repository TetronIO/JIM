// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Sync;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// Pins the machine-readable reason code each deleting branch of EvaluateMvoDeletionRule returns (#1223).
///
/// Causal edges group into cohorts on an attribution tuple whose reason element is this code, never the
/// decision's human-readable Reason sentence. That sentence interpolates the Connected System name, which the
/// tuple already carries separately, so grouping on it would be redundant and would silently change behaviour
/// whenever the wording changed. These tests are what stop the code and the sentence drifting apart: a branch
/// that gains a new sentence but keeps the wrong code would attribute a whole cascade to the wrong cause with
/// nothing failing.
/// </summary>
public class SyncEngineDeletionReasonCodeTests
{
    private Application.Servers.SyncEngine _engine = null!;

    [SetUp]
    public void SetUp()
    {
        _engine = new Application.Servers.SyncEngine();
    }

    private static MetaverseObject CreateMvo(
        MetaverseObjectDeletionRule deletionRule,
        AuthoritativeSourceTriggerMode triggerMode = AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect,
        List<int>? triggerSystemIds = null)
    {
        return new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = new MetaverseObjectType
            {
                DeletionRule = deletionRule,
                DeletionTriggerMode = triggerMode,
                DeletionTriggerConnectedSystemIds = triggerSystemIds ?? []
            }
        };
    }

    [Test]
    public void EvaluateMvoDeletionRule_LastConnectorDisconnected_ReturnsLastConnectorReasonCode()
    {
        var mvo = CreateMvo(MetaverseObjectDeletionRule.WhenLastConnectorDisconnected);

        var decision = _engine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId: 5, remainingConnectedSystemIds: []);

        Assert.That(decision.Fate, Is.EqualTo(MvoDeletionFate.DeletedImmediately));
        Assert.That(decision.ReasonCode, Is.EqualTo(CausalReasonCode.LastConnectorDisconnected));
    }

    /// <summary>
    /// A Metaverse Object Type set to delete on authoritative source disconnection but with no sources
    /// configured falls back to last-connector behaviour. That fallback gets its own code because it signals a
    /// misconfiguration an administrator investigating a cascade needs to see, and folding it into the plain
    /// last-connector code would hide it inside a cohort that looks deliberately configured.
    /// </summary>
    [Test]
    public void EvaluateMvoDeletionRule_AuthoritativeRuleWithNoSourcesConfigured_ReturnsTheFallbackReasonCode()
    {
        var mvo = CreateMvo(MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected, triggerSystemIds: []);

        var decision = _engine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId: 5, remainingConnectedSystemIds: []);

        Assert.That(decision.Fate, Is.EqualTo(MvoDeletionFate.DeletedImmediately));
        Assert.That(decision.ReasonCode, Is.EqualTo(CausalReasonCode.LastConnectorDisconnectedNoSourcesConfigured));
    }

    [Test]
    public void EvaluateMvoDeletionRule_AllSourcesModeLastSourceDisconnected_ReturnsAllSourcesReasonCode()
    {
        var mvo = CreateMvo(
            MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
            AuthoritativeSourceTriggerMode.AllSourcesDisconnect,
            triggerSystemIds: [5, 6]);

        // System 6 is an authoritative source but is no longer joined; 9 remains but is not a source.
        var decision = _engine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId: 5, remainingConnectedSystemIds: [9]);

        Assert.That(decision.Fate, Is.EqualTo(MvoDeletionFate.DeletedImmediately));
        Assert.That(decision.ReasonCode, Is.EqualTo(CausalReasonCode.AllAuthoritativeSourcesDisconnected));
    }

    [Test]
    public void EvaluateMvoDeletionRule_SpecificSourcesModeSourceDisconnected_ReturnsAuthoritativeSourceReasonCode()
    {
        var mvo = CreateMvo(
            MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
            AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect,
            triggerSystemIds: [5]);

        var decision = _engine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId: 5, remainingConnectedSystemIds: [7, 8]);

        Assert.That(decision.Fate, Is.EqualTo(MvoDeletionFate.DeletedImmediately));
        Assert.That(decision.ReasonCode, Is.EqualTo(CausalReasonCode.AuthoritativeSourceDisconnected));
    }

    /// <summary>
    /// A scheduled deletion is the same decision with a grace period attached, so it must carry the same reason
    /// code as its immediate counterpart. Losing the code on this path would leave every grace-period deployment
    /// with uncohorted edges.
    /// </summary>
    [Test]
    public void EvaluateMvoDeletionRule_WithGracePeriod_KeepsTheSameReasonCode()
    {
        var mvo = CreateMvo(
            MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
            AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect,
            triggerSystemIds: [5]);
        mvo.Type!.DeletionGracePeriod = TimeSpan.FromDays(7);

        var decision = _engine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId: 5, remainingConnectedSystemIds: []);

        Assert.That(decision.Fate, Is.EqualTo(MvoDeletionFate.DeletionScheduled));
        Assert.That(decision.ReasonCode, Is.EqualTo(CausalReasonCode.AuthoritativeSourceDisconnected));
    }

    /// <summary>
    /// A decision not to delete writes no edge, because nothing happened for an edge to point at. It must
    /// therefore carry no reason code rather than a misleading one.
    /// </summary>
    [Test]
    public void EvaluateMvoDeletionRule_NotDeleted_ReturnsNoReasonCode()
    {
        var mvo = CreateMvo(MetaverseObjectDeletionRule.Manual);

        var decision = _engine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId: 5, remainingConnectedSystemIds: []);

        Assert.That(decision.Fate, Is.EqualTo(MvoDeletionFate.NotDeleted));
        Assert.That(decision.ReasonCode, Is.EqualTo(CausalReasonCode.NotSet));
    }
}
