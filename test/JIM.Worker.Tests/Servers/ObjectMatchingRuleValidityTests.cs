// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Security;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Object Matching Rules that cannot work are refused rather than stored (#1458).
///
/// <see cref="ObjectMatchingRule.IsValid"/> has always described what a workable rule looks like, and nothing
/// called it. A Simple mode rule with no Metaverse Object Type has nowhere to search, so the matching engine skips
/// it and the Connected System matches nothing at all; the accounts that should have joined an existing identity
/// project a new one each instead. Nothing fails, nothing is logged at a level anybody reads, and the duplicate
/// identities are discovered later by a human.
///
/// That is precisely the failure mode the Synchronisation Integrity rules exist to prevent, so the fix is a hard
/// refusal at the point of storage, not a warning.
/// </summary>
[TestFixture]
public class ObjectMatchingRuleValidityTests
{
    private Mock<IRepository> _repo = null!;
    private Mock<IConnectedSystemRepository> _connectedSystemRepo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private JimApplication _jim = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _repo = new Mock<IRepository>();
        _connectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _repo.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepo.Object);
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _connectedSystemRepo.Setup(r => r.CreateObjectMatchingRuleAsync(It.IsAny<ObjectMatchingRule>())).Returns(Task.CompletedTask);
        _connectedSystemRepo.Setup(r => r.UpdateObjectMatchingRuleAsync(It.IsAny<ObjectMatchingRule>())).Returns(Task.CompletedTask);

        _jim = new JimApplication(_repo.Object);
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    [Test]
    public void CreateObjectMatchingRuleAsync_SimpleModeRuleWithNoMetaverseObjectType_IsRefusedAsync()
    {
        // The portal's Add Matching Rule form produced exactly this shape (#1458): everything filled in except the
        // one field that tells the rule where to look.
        var rule = SimpleModeRule();
        rule.MetaverseObjectTypeId = null;
        rule.MetaverseObjectType = null;

        Assert.That(async () => await _jim.ConnectedSystems.CreateObjectMatchingRuleAsync(rule, ApiKey()),
            Throws.TypeOf<InvalidDataException>().And.Message.Contains("Metaverse Object Type"));

        _connectedSystemRepo.Verify(r => r.CreateObjectMatchingRuleAsync(It.IsAny<ObjectMatchingRule>()), Times.Never,
            "a rule that can never match must not reach the database");
    }

    [Test]
    public void CreateObjectMatchingRuleAsync_RuleWithNoSource_IsRefusedAsync()
    {
        var rule = SimpleModeRule();
        rule.Sources.Clear();

        Assert.That(async () => await _jim.ConnectedSystems.CreateObjectMatchingRuleAsync(rule, ApiKey()),
            Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void CreateObjectMatchingRuleAsync_RuleWithNoTargetAttribute_IsRefusedAsync()
    {
        var rule = SimpleModeRule();
        rule.TargetMetaverseAttributeId = null;
        rule.TargetMetaverseAttribute = null;

        Assert.That(async () => await _jim.ConnectedSystems.CreateObjectMatchingRuleAsync(rule, ApiKey()),
            Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void CreateObjectMatchingRuleAsync_AdvancedModeRuleCarryingAMetaverseObjectType_IsRefusedAsync()
    {
        // The inverse mistake: an Advanced mode rule takes its type from the Synchronisation Rule that owns it, and
        // one carrying its own would search somewhere the rule does not manage.
        var rule = new ObjectMatchingRule
        {
            SyncRuleId = 42,
            MetaverseObjectTypeId = 3,
            TargetMetaverseAttributeId = 201,
            Sources = [new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttributeId = 101 }]
        };

        Assert.That(async () => await _jim.ConnectedSystems.CreateObjectMatchingRuleAsync(rule, ApiKey()),
            Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public async Task CreateObjectMatchingRuleAsync_WorkableRule_IsStoredAsync()
    {
        await _jim.ConnectedSystems.CreateObjectMatchingRuleAsync(SimpleModeRule(), ApiKey());

        _connectedSystemRepo.Verify(r => r.CreateObjectMatchingRuleAsync(It.IsAny<ObjectMatchingRule>()), Times.Once);
    }

    [Test]
    public void CreateObjectMatchingRuleAsync_SyncRuleScopedRuleWhileSystemInSimpleMode_IsRefusedAsync()
    {
        // The #1569 footgun: the system only consults type-scoped rules in simple mode, so a Synchronisation
        // Rule-scoped rule created against it is silently inert; synchronisation joins nothing and nothing reports
        // why. Refusal must name the active mode so the administrator knows which switch to reach for.
        var rule = AdvancedModeRule(ObjectMatchingRuleMode.ConnectedSystem);

        Assert.That(async () => await _jim.ConnectedSystems.CreateObjectMatchingRuleAsync(rule, ApiKey()),
            Throws.TypeOf<InvalidDataException>().And.Message.Contains("simple matching mode"));

        _connectedSystemRepo.Verify(r => r.CreateObjectMatchingRuleAsync(It.IsAny<ObjectMatchingRule>()), Times.Never,
            "a rule the active matching mode would never consult must not reach the database");
    }

    [Test]
    public void CreateObjectMatchingRuleAsync_TypeScopedRuleWhileSystemInAdvancedMode_IsRefusedAsync()
    {
        // The inverse direction: in advanced mode only each Synchronisation Rule's own rules are consulted, so a
        // new type-scoped rule would be equally inert.
        var rule = SimpleModeRule();
        rule.ConnectedSystemObjectType!.ConnectedSystem!.ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule;

        Assert.That(async () => await _jim.ConnectedSystems.CreateObjectMatchingRuleAsync(rule, ApiKey()),
            Throws.TypeOf<InvalidDataException>().And.Message.Contains("advanced matching mode"));

        _connectedSystemRepo.Verify(r => r.CreateObjectMatchingRuleAsync(It.IsAny<ObjectMatchingRule>()), Times.Never);
    }

    [Test]
    public async Task CreateObjectMatchingRuleAsync_SyncRuleScopedRuleWhileSystemInAdvancedMode_IsStoredAsync()
    {
        var rule = AdvancedModeRule(ObjectMatchingRuleMode.SyncRule);

        await _jim.ConnectedSystems.CreateObjectMatchingRuleAsync(rule, ApiKey());

        _connectedSystemRepo.Verify(r => r.CreateObjectMatchingRuleAsync(It.IsAny<ObjectMatchingRule>()), Times.Once);
    }

    [Test]
    public void UpdateObjectMatchingRuleAsync_RuleEditedIntoAnUnworkableShape_IsRefusedAsync()
    {
        // Storage refuses the same shapes on the way in and on the way out; a rule edited into uselessness is no
        // better than one created that way.
        var rule = SimpleModeRule();
        rule.MetaverseObjectTypeId = null;
        rule.MetaverseObjectType = null;

        Assert.That(async () => await _jim.ConnectedSystems.UpdateObjectMatchingRuleAsync(rule, ApiKey()),
            Throws.TypeOf<InvalidDataException>());

        _connectedSystemRepo.Verify(r => r.UpdateObjectMatchingRuleAsync(It.IsAny<ObjectMatchingRule>()), Times.Never);
    }

    private static ObjectMatchingRule SimpleModeRule() => new()
    {
        Id = 9,
        Order = 0,
        ConnectedSystemObjectTypeId = 7,
        ConnectedSystemObjectType = new ConnectedSystemObjectType
        {
            Id = 7,
            Name = "User",
            ConnectedSystemId = 3,
            ConnectedSystem = new ConnectedSystem { Id = 3, Name = "AD" }
        },
        MetaverseObjectTypeId = 3,
        MetaverseObjectType = new MetaverseObjectType { Id = 3, Name = "Person" },
        TargetMetaverseAttributeId = 201,
        TargetMetaverseAttribute = new MetaverseAttribute
        {
            Id = 201,
            Name = "Employee ID",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued
        },
        Sources = [new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttributeId = 101 }]
    };

    private static ObjectMatchingRule AdvancedModeRule(ObjectMatchingRuleMode systemMode) => new()
    {
        Id = 11,
        Order = 0,
        SyncRuleId = 42,
        SyncRule = new SyncRule
        {
            Id = 42,
            Name = "Import Users",
            Direction = SyncRuleDirection.Import,
            ConnectedSystemId = 3,
            ConnectedSystem = new ConnectedSystem { Id = 3, Name = "AD", ObjectMatchingRuleMode = systemMode }
        },
        TargetMetaverseAttributeId = 201,
        TargetMetaverseAttribute = new MetaverseAttribute
        {
            Id = 201,
            Name = "Employee ID",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued
        },
        Sources = [new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttributeId = 101 }]
    };

    private static ApiKey ApiKey() => new()
    {
        Id = Guid.NewGuid(),
        Name = "TestApiKey",
        KeyHash = "hash",
        KeyPrefix = "test",
        IsEnabled = true,
        Created = DateTime.UtcNow
    };
}
