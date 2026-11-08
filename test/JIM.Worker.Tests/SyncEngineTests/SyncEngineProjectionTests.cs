// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

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

    // #2823: projection does not depend on the order the rules load in.

    [TestCase(false)]
    [TestCase(true)]
    public void EvaluateProjection_TwoRulesIntoOneType_ProjectsNamingTheFirstByName(bool reverse)
    {
        var person = new MetaverseObjectType { Id = 10, Name = "Person" };
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), TypeId = 1 };
        var a = ProjectingRule(1, "Rule A", person);
        var b = ProjectingRule(2, "Rule B", person);

        var decision = _engine.EvaluateProjection(cso, reverse ? [b, a] : [a, b]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decision.ShouldProject, Is.True);
            Assert.That(decision.IsConflict, Is.False);
            Assert.That(decision.MetaverseObjectType, Is.SameAs(person));
            Assert.That(decision.ProjectionSyncRule, Is.SameAs(a));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void EvaluateProjection_TwoRulesIntoDifferentTypes_ReturnsAConflictNamingBoth(bool reverse)
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), TypeId = 1 };
        var a = ProjectingRule(1, "Rule A", new MetaverseObjectType { Id = 10, Name = "Person" });
        var b = ProjectingRule(2, "Rule B", new MetaverseObjectType { Id = 11, Name = "Contact" });

        var decision = _engine.EvaluateProjection(cso, reverse ? [b, a] : [a, b]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decision.ShouldProject, Is.False);
            Assert.That(decision.IsConflict, Is.True);
            Assert.That(decision.ConflictingRules, Is.EqualTo(new[] { a, b }), "listed by name");
        }
    }

    private static SyncRule ProjectingRule(int id, string name, MetaverseObjectType type) => new()
    {
        Id = id,
        Name = name,
        Direction = SyncRuleDirection.Import,
        ProjectToMetaverse = true,
        ConnectedSystemObjectType = new ConnectedSystemObjectType { Id = 1 },
        MetaverseObjectType = type,
        MetaverseObjectTypeId = type.Id
    };
}
