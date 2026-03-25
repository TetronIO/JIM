using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Sync;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// Pure unit tests for SyncEngine.EvaluateMvoDeletionRule — no mocking, no database.
/// </summary>
public class SyncEngineDeletionRuleTests
{
    private Application.Servers.SyncEngine _engine = null!;

    [SetUp]
    public void SetUp()
    {
        _engine = new Application.Servers.SyncEngine();
    }

    [Test]
    public void EvaluateMvoDeletionRule_NullType_ReturnsNotDeleted()
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        mvo.Type = null!;

        var decision = _engine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId: 1, remainingCsoCount: 0);

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

        var decision = _engine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId: 1, remainingCsoCount: 0);

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

        var decision = _engine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId: 1, remainingCsoCount: 0);

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

        var decision = _engine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId: 1, remainingCsoCount: 2);

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

        var decision = _engine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId: 1, remainingCsoCount: 0);

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

        var decision = _engine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId: 1, remainingCsoCount: 0);

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

        var decision = _engine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId: 1, remainingCsoCount: 0);

        Assert.That(decision.Fate, Is.EqualTo(MvoDeletionFate.DeletionScheduled));
        Assert.That(decision.GracePeriod, Is.EqualTo(TimeSpan.FromDays(7)));
    }

    [Test]
    public void EvaluateMvoDeletionRule_AuthoritativeSource_Disconnected_ReturnsDeleteImmediately()
    {
        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = new MetaverseObjectType
            {
                DeletionRule = MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
                DeletionTriggerConnectedSystemIds = [5],
                DeletionGracePeriod = null
            }
        };

        var decision = _engine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId: 5, remainingCsoCount: 3);

        Assert.That(decision.Fate, Is.EqualTo(MvoDeletionFate.DeletedImmediately));
    }

    [Test]
    public void EvaluateMvoDeletionRule_NonAuthoritativeSource_Disconnected_ReturnsNotDeleted()
    {
        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = new MetaverseObjectType
            {
                DeletionRule = MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
                DeletionTriggerConnectedSystemIds = [5]
            }
        };

        var decision = _engine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId: 99, remainingCsoCount: 1);

        Assert.That(decision.Fate, Is.EqualTo(MvoDeletionFate.NotDeleted));
    }

    [Test]
    public void EvaluateMvoDeletionRule_AuthoritativeSource_NoTriggerIds_FallsBackToLastConnector()
    {
        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = new MetaverseObjectType
            {
                DeletionRule = MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
                DeletionTriggerConnectedSystemIds = [],
                DeletionGracePeriod = null
            }
        };

        var decision = _engine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId: 1, remainingCsoCount: 0);

        Assert.That(decision.Fate, Is.EqualTo(MvoDeletionFate.DeletedImmediately));
    }
}
