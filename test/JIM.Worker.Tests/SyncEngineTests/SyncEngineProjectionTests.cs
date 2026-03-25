using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// Pure unit tests for SyncEngine.EvaluateProjection — no mocking, no database.
/// </summary>
public class SyncEngineProjectionTests
{
    private Application.Servers.SyncEngine _engine = null!;

    [SetUp]
    public void SetUp()
    {
        _engine = new Application.Servers.SyncEngine();
    }

    [Test]
    public void EvaluateProjection_NoProjectionRules_ReturnsNoProjection()
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), TypeId = 1 };
        var syncRules = new List<SyncRule>
        {
            new() { ProjectToMetaverse = false, ConnectedSystemObjectType = new ConnectedSystemObjectType { Id = 1 } }
        };

        var decision = _engine.EvaluateProjection(cso, syncRules);

        Assert.That(decision.ShouldProject, Is.False);
        Assert.That(decision.MetaverseObjectType, Is.Null);
    }

    [Test]
    public void EvaluateProjection_ProjectionRuleExists_ReturnsProject()
    {
        var mvoType = new MetaverseObjectType { Id = 10, Name = "Person" };
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), TypeId = 1 };
        var syncRules = new List<SyncRule>
        {
            new()
            {
                ProjectToMetaverse = true,
                ConnectedSystemObjectType = new ConnectedSystemObjectType { Id = 1 },
                MetaverseObjectType = mvoType
            }
        };

        var decision = _engine.EvaluateProjection(cso, syncRules);

        Assert.That(decision.ShouldProject, Is.True);
        Assert.That(decision.MetaverseObjectType, Is.SameAs(mvoType));
    }

    [Test]
    public void EvaluateProjection_WrongObjectType_ReturnsNoProjection()
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), TypeId = 1 };
        var syncRules = new List<SyncRule>
        {
            new()
            {
                ProjectToMetaverse = true,
                ConnectedSystemObjectType = new ConnectedSystemObjectType { Id = 99 },
                MetaverseObjectType = new MetaverseObjectType { Id = 10, Name = "Person" }
            }
        };

        var decision = _engine.EvaluateProjection(cso, syncRules);

        Assert.That(decision.ShouldProject, Is.False);
    }

    [Test]
    public void EvaluateProjection_EmptySyncRules_ReturnsNoProjection()
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), TypeId = 1 };

        var decision = _engine.EvaluateProjection(cso, Array.Empty<SyncRule>());

        Assert.That(decision.ShouldProject, Is.False);
    }
}
