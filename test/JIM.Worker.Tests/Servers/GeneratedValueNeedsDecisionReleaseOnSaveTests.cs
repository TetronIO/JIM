// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.TestSupport;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Saving a change to a generated flow's configuration releases the Needs Decision its assignments wait on (Unique
/// Value Generation, #242, release 4; FR 16), mirroring how a changed initial password configuration releases parked
/// accounts (#1221): the change is the administrator's answer, so the next export run tries again under it. Gated on
/// an actual change to the generation settings.
/// </summary>
[TestFixture]
public class GeneratedValueNeedsDecisionReleaseOnSaveTests
{
    private const int SyncRuleId = 21;
    private const int GenerationId = 7;

    private Mock<IRepository> _mockRepository = null!;
    private Mock<IConnectedSystemRepository> _mockCsRepo = null!;
    private Mock<ISyncRepository> _mockSyncRepo = null!;
    private JimApplication _jim = null!;
    private MetaverseObject _initiatedBy = null!;
    private MetaverseAttribute _accountName = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _mockRepository = new Mock<IRepository>();
        _mockCsRepo = new Mock<IConnectedSystemRepository>();
        _mockSyncRepo = TestUtilities.StubQueuedChangeWithdrawal(new Mock<ISyncRepository>());
        var mockMvRepo = new Mock<IMetaverseRepository>();
        var mockActivityRepo = new Mock<IActivityRepository>();

        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockCsRepo.Object);
        _mockRepository.Setup(r => r.ServiceSettings).Returns(new InMemoryServiceSettingsRepository());
        _mockCsRepo.Setup(r => r.GetImportSyncRulesForMetaverseObjectTypeAsync(It.IsAny<int>())).ReturnsAsync(() => []);
        _mockRepository.Setup(r => r.Metaverse).Returns(mockMvRepo.Object);
        _mockRepository.Setup(r => r.Activity).Returns(mockActivityRepo.Object);
        _mockRepository.Setup(r => r.Sync).Returns(_mockSyncRepo.Object);

        mockActivityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        mockActivityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _mockCsRepo.Setup(r => r.UpdateSyncRuleAsync(It.IsAny<SyncRule>())).Returns(Task.CompletedTask);
        _mockCsRepo.Setup(r => r.GetImportMappingTargetMetaverseAttributesAsync(It.IsAny<int>())).ReturnsAsync(new Dictionary<int, int>());
        _mockCsRepo.Setup(r => r.GetSyncRulesAsync(It.IsAny<bool>())).ReturnsAsync(() => []);

        _mockSyncRepo.Setup(r => r.GetGeneratedValueAssignmentsForGenerationAsync(GenerationId)).ReturnsAsync(() => [NeedsDecision()]);
        _mockSyncRepo.Setup(r => r.UpdateGeneratedValueAssignmentAsync(It.IsAny<GeneratedValueAssignment>())).Returns(Task.CompletedTask);
        _mockSyncRepo.Setup(r => r.ReleaseParkedPendingExportsAsync(It.IsAny<IReadOnlyCollection<Guid>>())).ReturnsAsync(1);

        _initiatedBy = TestUtilities.GetInitiatedBy();
        _accountName = new MetaverseAttribute { Id = 40, Name = "Account Name", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        _jim = new JimApplication(_mockRepository.Object, syncRepository: _mockSyncRepo.Object);
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_WhenTheGenerationSettingsChange_ReleasesTheNeedsDecisionAsync()
    {
        await SaveAsync(stored: Generation(attemptLimit: 1000), saving: Generation(attemptLimit: 2000));

        _mockSyncRepo.Verify(r => r.ReleaseParkedPendingExportsAsync(It.IsAny<IReadOnlyCollection<Guid>>()), Times.Once,
            "the changed configuration is the administrator's answer, so the parked export is released for the next export run");
        _mockSyncRepo.Verify(r => r.UpdateGeneratedValueAssignmentAsync(It.Is<GeneratedValueAssignment>(a => a.State == GeneratedValueAssignmentState.Committed)), Times.Once);
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_WhenTheGenerationSettingsAreUnchanged_LeavesTheNeedsDecisionAsync()
    {
        await SaveAsync(stored: Generation(attemptLimit: 1000), saving: Generation(attemptLimit: 1000));

        _mockSyncRepo.Verify(r => r.GetGeneratedValueAssignmentsForGenerationAsync(It.IsAny<int>()), Times.Never,
            "an unrelated edit must not set values retrying against settings the target has already answered");
    }

    [Test]
    public void WouldGenerateTheSameAs_ExclusionsDiffer_IsFalse()
    {
        var left = Generation(attemptLimit: 1000);
        var right = Generation(attemptLimit: 1000);
        right.Exclusions.Add(new SyncRuleMappingGenerationExclusion { ConnectedSystemId = 3 });

        Assert.That(SyncRuleMappingGeneration.WouldGenerateTheSameAs(left, right), Is.False);
    }

    [Test]
    public void WouldGenerateTheSameAs_CollisionRemediationSwitched_IsFalse()
    {
        var left = Generation(attemptLimit: 1000);
        var right = Generation(attemptLimit: 1000);
        right.CollisionRemediation = !left.CollisionRemediation;

        Assert.That(SyncRuleMappingGeneration.WouldGenerateTheSameAs(left, right), Is.False);
    }

    private async Task SaveAsync(SyncRuleMappingGeneration stored, SyncRuleMappingGeneration saving)
    {
        _mockCsRepo.Setup(r => r.GetSyncRuleMappingGenerationsAsync(It.IsAny<IReadOnlyCollection<int>>()))
            .ReturnsAsync(new Dictionary<int, SyncRuleMappingGeneration> { [GenerationId] = stored });

        var saved = await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(Rule(saving), _initiatedBy);

        Assert.That(saved, Is.True, "precondition: the Synchronisation Rule must have saved");
    }

    private static SyncRuleMappingGeneration Generation(int attemptLimit) => new()
    {
        Id = GenerationId,
        TokenKind = GeneratedValueTokenKind.OnlyIfTaken,
        AttemptLimit = attemptLimit,
        CollisionRemediation = true
    };

    private static GeneratedValueAssignment NeedsDecision() => new()
    {
        Id = Guid.NewGuid(),
        ConnectedSystemObjectId = Guid.NewGuid(),
        ConnectedSystemObjectTypeAttributeId = 9,
        Value = "joe",
        NormalisedValue = "joe",
        SyncRuleMappingGenerationId = GenerationId,
        State = GeneratedValueAssignmentState.NeedsDecision,
        NeedsDecisionEnteredAt = DateTime.UtcNow
    };

    private SyncRule Rule(SyncRuleMappingGeneration generation)
    {
        // An export-mode generated flow (a ticketing system's login name), so the save reconciles no import priority.
        var loginName = new ConnectedSystemObjectTypeAttribute { Id = 9, Name = "loginName", Type = AttributeDataType.Text };
        var rule = new SyncRule
        {
            Id = SyncRuleId,
            Name = "Ticketing Export",
            Direction = SyncRuleDirection.Export,
            Enabled = true,
            ConnectedSystemId = 1,
            ConnectedSystem = new ConnectedSystem { Id = 1, Name = "Ticketing" },
            MetaverseObjectType = new MetaverseObjectType { Id = 1, Name = "person", Attributes = [_accountName] },
            ConnectedSystemObjectType = new ConnectedSystemObjectType { Id = 1, Name = "user", Attributes = [loginName] }
        };
        var mapping = new SyncRuleMapping
        {
            Id = 70,
            SyncRule = rule,
            SyncRuleId = rule.Id,
            TargetConnectedSystemAttribute = loginName,
            TargetConnectedSystemAttributeId = loginName.Id,
            Generation = generation
        };
        mapping.Sources.Add(new SyncRuleMappingSource { Order = 0, Expression = "Lower(mv[\"Account Name\"])" });
        rule.AttributeFlowRules.Add(mapping);
        return rule;
    }
}
