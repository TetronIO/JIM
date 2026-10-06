// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Security;
using JIM.Models.Staging;
using JIM.TestSupport;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests that every mapping-save path in <see cref="ConnectedSystemServer"/> accepts generated configuration (Unique
/// Value Generation, #242) on a deployment with no feature flags set: a brand new generated mapping, an existing
/// ordinary mapping converted to one, an existing generated mapping's own settings, and a new generated mapping
/// arriving through the whole-rule save. These paths were gated on the <c>Features.UniqueValueGeneration</c> flag
/// while the feature was in development; the flag was removed when it shipped (#1803), and these tests stand guard
/// against a gate creeping back.
/// </summary>
[TestFixture]
public class ConnectedSystemServerGeneratedMappingSaveTests
{
    private Mock<IRepository> _repo = null!;
    private Mock<IConnectedSystemRepository> _csRepo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private MetaverseObject _initiator = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _repo = new Mock<IRepository>();
        _csRepo = new Mock<IConnectedSystemRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _repo.Setup(r => r.ConnectedSystems).Returns(_csRepo.Object);
        // The import rules of a Metaverse Object Type, read to find Attribute Flows deriving Metaverse attributes that a
        // change leaves with a missing input (#1750, FR 3): none here.
        _csRepo.Setup(r => r.GetImportSyncRulesForMetaverseObjectTypeAsync(It.IsAny<int>())).ReturnsAsync(() => []);
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);

        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.GetMaxConfigurationChangeVersionAsync(It.IsAny<ActivityTargetType>(), It.IsAny<int>())).ReturnsAsync(0);

        // The duplicate-target check (#1532) reads the rule's existing mappings on every mapping create/update;
        // these tests are about the save paths accepting generated configuration, so default the list to empty.
        _csRepo.Setup(r => r.GetSyncRuleMappingsAsync(It.IsAny<int>())).ReturnsAsync(new List<SyncRuleMapping>());
        // A sole contributor short-circuits import-mapping priority auto-assignment before it reads anything else.
        _csRepo.Setup(r => r.GetImportSyncRuleMappingsForMetaverseAttributeAsync(It.IsAny<int>(), It.IsAny<int>())).ReturnsAsync(new List<SyncRuleMapping>());
        _csRepo.Setup(r => r.CreateSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>())).Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.UpdateSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>())).Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.CreateSyncRuleAsync(It.IsAny<SyncRule>())).Returns(Task.CompletedTask);
        // The stored generation settings, read on every save of a generated flow to tell whether a Needs Decision is
        // released (#242, release 4): none stored, so nothing is compared or released.
        _csRepo.Setup(r => r.GetSyncRuleMappingGenerationsAsync(It.IsAny<IReadOnlyCollection<int>>()))
            .ReturnsAsync(() => new Dictionary<int, SyncRuleMappingGeneration>());

        _initiator = TestUtilities.GetInitiatedBy();
    }

    private JimApplication BuildApplication()
    {
        // An empty settings store: no Service Setting (feature flag or otherwise) has been set.
        _repo.Setup(r => r.ServiceSettings).Returns(new InMemoryServiceSettingsRepository());
        // A real (in-memory) Sync repository, not a mock: the save-time Sequence counter skip-ahead
        // (RaiseSequenceStartIfHigherAsync) needs one to raise against.
        return new JimApplication(_repo.Object, syncRepository: new JIM.InMemoryData.SyncRepository());
    }

    // A valid, minimal generated mapping: no base expression needed, single-valued Text target. Random/Guid
    // rather than Sequence, so the save-time counter skip-ahead (RaiseSequenceStartIfHigherAsync) no-ops without
    // needing a Sync repository mock; that raise is not what these tests are about.
    private static SyncRuleMapping NewGeneratedMapping(int syncRuleId = 1) => new()
    {
        SyncRuleId = syncRuleId,
        SyncRule = new SyncRule { Id = syncRuleId, Name = "Import Rule", Direction = SyncRuleDirection.Import },
        TargetMetaverseAttribute = new MetaverseAttribute
        {
            Id = 5, Name = "Employee Number", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued
        },
        TargetMetaverseAttributeId = 5,
        Generation = new SyncRuleMappingGeneration { TokenKind = GeneratedValueTokenKind.Random, RandomFormat = GeneratedValueRandomFormat.Guid }
    };

    // ---- CreateSyncRuleMappingAsync: a brand new generated mapping ----

    [Test]
    public async Task CreateSyncRuleMappingAsync_NewGeneratedMapping_IsSavedAsync()
    {
        var jim = BuildApplication();
        var mapping = NewGeneratedMapping();

        await jim.ConnectedSystems.CreateSyncRuleMappingAsync(mapping, _initiator);

        _csRepo.Verify(r => r.CreateSyncRuleMappingAsync(mapping), Times.Once);
    }

    // ---- UpdateSyncRuleMappingAsync: converting an existing ordinary mapping to generated ----

    [Test]
    public async Task UpdateSyncRuleMappingAsync_ConvertingOrdinaryMappingToGenerated_IsSavedAsync()
    {
        var jim = BuildApplication();
        var mapping = NewGeneratedMapping();
        mapping.Id = 10; // an existing, persisted mapping newly carrying a Generation row (Id == 0): a conversion.

        await jim.ConnectedSystems.UpdateSyncRuleMappingAsync(mapping, _initiator);

        _csRepo.Verify(r => r.UpdateSyncRuleMappingAsync(mapping), Times.Once);
    }

    // ---- UpdateSyncRuleMappingSettingsAsync: editing an existing generation's own settings ----

    [Test]
    public async Task UpdateSyncRuleMappingSettingsAsync_ExistingGeneration_IsSavedAsync()
    {
        var jim = BuildApplication();
        var existingGeneration = new SyncRuleMappingGeneration { Id = 900, TokenKind = GeneratedValueTokenKind.Sequence, SequenceStart = 1 };
        var mapping = new SyncRuleMapping
        {
            Id = 12,
            SyncRuleId = 1,
            SyncRule = new SyncRule { Id = 1, Name = "Import Rule", Direction = SyncRuleDirection.Import },
            TargetMetaverseAttribute = new MetaverseAttribute
            {
                Id = 5, Name = "Employee Number", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued
            },
            TargetMetaverseAttributeId = 5,
            Generation = existingGeneration
        };
        _csRepo.Setup(r => r.GetSyncRuleMappingForUpdateAsync(mapping.Id)).ReturnsAsync(mapping);

        var settings = new SyncRuleMappingSettingsUpdate { Generation = new SyncRuleMappingGenerationSettingsUpdate { SequenceStart = 500 } };
        var result = await jim.ConnectedSystems.UpdateSyncRuleMappingSettingsAsync(mapping.Id, settings, _initiator);

        Assert.That(result, Is.Not.Null, "editing an existing generation's settings must be allowed");
        Assert.That(result!.Generation!.SequenceStart, Is.EqualTo(500));
        _csRepo.Verify(r => r.UpdateSyncRuleMappingAsync(mapping), Times.Once);
    }

    // ---- CreateOrUpdateSyncRuleAsync: a new generated mapping arriving through the whole-rule save ----

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_NewGeneratedMappingOnTheRule_IsSavedAsync()
    {
        var jim = BuildApplication();
        var syncRule = new SyncRule
        {
            Id = 0,
            Name = "Import Rule",
            Direction = SyncRuleDirection.Import,
            Enabled = false,
            ConnectedSystem = new ConnectedSystem { Id = 3, Name = "AD" },
            ConnectedSystemId = 3,
            ConnectedSystemObjectType = new ConnectedSystemObjectType { Id = 7, Name = "user" },
            ConnectedSystemObjectTypeId = 7,
            MetaverseObjectType = new MetaverseObjectType { Id = 1, Name = "Person" },
            MetaverseObjectTypeId = 1
        };
        syncRule.AttributeFlowRules.Add(NewGeneratedMapping(syncRuleId: 0));

        await jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(syncRule, _initiator);

        _csRepo.Verify(r => r.CreateSyncRuleAsync(syncRule), Times.Once);
    }
}
