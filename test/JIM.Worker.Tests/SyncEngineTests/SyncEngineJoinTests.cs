using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Sync;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// Pure unit tests for SyncEngine.EvaluateJoin — no mocking, no database.
/// </summary>
public class SyncEngineJoinTests
{
    private Application.Servers.SyncEngine _engine = null!;

    [SetUp]
    public void SetUp()
    {
        _engine = new Application.Servers.SyncEngine();
    }

    [Test]
    public void EvaluateJoin_NullCandidate_ReturnsNoMatch()
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid() };

        var decision = _engine.EvaluateJoin(cso, null, 0);

        Assert.That(decision.ShouldJoin, Is.False);
        Assert.That(decision.TargetMvo, Is.Null);
        Assert.That(decision.Error, Is.Null);
    }

    [Test]
    public void EvaluateJoin_ValidCandidate_ZeroExisting_ReturnsJoin()
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid() };
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };

        var decision = _engine.EvaluateJoin(cso, mvo, existingJoinCount: 0);

        Assert.That(decision.ShouldJoin, Is.True);
        Assert.That(decision.TargetMvo, Is.SameAs(mvo));
        Assert.That(decision.Error, Is.Null);
    }

    [Test]
    public void EvaluateJoin_ExistingJoinCount_One_ReturnsExistingJoinError()
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid() };
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };

        var decision = _engine.EvaluateJoin(cso, mvo, existingJoinCount: 1);

        Assert.That(decision.ShouldJoin, Is.False);
        Assert.That(decision.Error, Is.Not.Null);
        Assert.That(decision.Error!.ErrorType, Is.EqualTo(JoinErrorType.ExistingJoin));
    }

    [Test]
    public void EvaluateJoin_ExistingJoinCount_MoreThanOne_ReturnsExistingJoinError()
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid() };
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };

        var decision = _engine.EvaluateJoin(cso, mvo, existingJoinCount: 3);

        Assert.That(decision.ShouldJoin, Is.False);
        Assert.That(decision.Error, Is.Not.Null);
        Assert.That(decision.Error!.ErrorType, Is.EqualTo(JoinErrorType.ExistingJoin));
    }
}
