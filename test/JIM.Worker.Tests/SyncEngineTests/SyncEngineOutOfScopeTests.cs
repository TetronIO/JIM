using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// Pure unit tests for SyncEngine.DetermineOutOfScopeAction — no mocking, no database.
/// </summary>
public class SyncEngineOutOfScopeTests
{
    private Application.Servers.SyncEngine _engine = null!;

    [SetUp]
    public void SetUp()
    {
        _engine = new Application.Servers.SyncEngine();
    }

    [Test]
    public void DetermineOutOfScopeAction_NoImportRules_ReturnsDisconnect()
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), TypeId = 1 };
        var syncRules = new List<SyncRule>
        {
            new() { Direction = SyncRuleDirection.Export, Enabled = true, ConnectedSystemObjectTypeId = 1 }
        };

        var action = _engine.DetermineOutOfScopeAction(cso, syncRules);

        Assert.That(action, Is.EqualTo(InboundOutOfScopeAction.Disconnect));
    }

    [Test]
    public void DetermineOutOfScopeAction_ImportRuleWithDisconnect_ReturnsDisconnect()
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), TypeId = 1 };
        var syncRules = new List<SyncRule>
        {
            new()
            {
                Direction = SyncRuleDirection.Import,
                Enabled = true,
                ConnectedSystemObjectTypeId = 1,
                InboundOutOfScopeAction = InboundOutOfScopeAction.Disconnect
            }
        };

        var action = _engine.DetermineOutOfScopeAction(cso, syncRules);

        Assert.That(action, Is.EqualTo(InboundOutOfScopeAction.Disconnect));
    }

    [Test]
    public void DetermineOutOfScopeAction_ImportRuleWithRemainJoined_ReturnsRemainJoined()
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), TypeId = 1 };
        var syncRules = new List<SyncRule>
        {
            new()
            {
                Direction = SyncRuleDirection.Import,
                Enabled = true,
                ConnectedSystemObjectTypeId = 1,
                InboundOutOfScopeAction = InboundOutOfScopeAction.RemainJoined
            }
        };

        var action = _engine.DetermineOutOfScopeAction(cso, syncRules);

        Assert.That(action, Is.EqualTo(InboundOutOfScopeAction.RemainJoined));
    }

    [Test]
    public void DetermineOutOfScopeAction_WrongObjectType_ReturnsDisconnect()
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), TypeId = 1 };
        var syncRules = new List<SyncRule>
        {
            new()
            {
                Direction = SyncRuleDirection.Import,
                Enabled = true,
                ConnectedSystemObjectTypeId = 99,
                InboundOutOfScopeAction = InboundOutOfScopeAction.RemainJoined
            }
        };

        var action = _engine.DetermineOutOfScopeAction(cso, syncRules);

        Assert.That(action, Is.EqualTo(InboundOutOfScopeAction.Disconnect));
    }

    [Test]
    public void DetermineOutOfScopeAction_DisabledRule_ReturnsDisconnect()
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), TypeId = 1 };
        var syncRules = new List<SyncRule>
        {
            new()
            {
                Direction = SyncRuleDirection.Import,
                Enabled = false,
                ConnectedSystemObjectTypeId = 1,
                InboundOutOfScopeAction = InboundOutOfScopeAction.RemainJoined
            }
        };

        var action = _engine.DetermineOutOfScopeAction(cso, syncRules);

        Assert.That(action, Is.EqualTo(InboundOutOfScopeAction.Disconnect));
    }
}
