// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Exceptions;
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
/// Tests the Unique Value Generation feature-flag gate in <see cref="ConnectedSystemServer"/> (#242, Phase 3.5):
/// every mapping-save path that would persist a NEW <see cref="SyncRuleMappingGeneration"/> row (a brand new
/// generated mapping, or an existing ordinary mapping being converted to one) is refused while the flag is off,
/// naming the feature via <see cref="FeatureDisabledException"/>. Editing an existing generated mapping's own
/// settings, and every other save, is never gated: the flag governs creating new generated configuration, not
/// managing configuration that already exists (see the "Feature Flags" section of
/// <c>engineering/DEVELOPER_GUIDE.md</c>).
/// </summary>
[TestFixture]
public class ConnectedSystemServerGeneratedMappingGateTests
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
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);

        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.GetMaxConfigurationChangeVersionAsync(It.IsAny<ActivityTargetType>(), It.IsAny<int>())).ReturnsAsync(0);

        // The duplicate-target check (#1532) reads the rule's existing mappings on every mapping create/update;
        // these tests are about the flag gate, so default the list to empty.
        _csRepo.Setup(r => r.GetSyncRuleMappingsAsync(It.IsAny<int>())).ReturnsAsync(new List<SyncRuleMapping>());
        // A sole contributor short-circuits import-mapping priority auto-assignment before it reads anything else.
        _csRepo.Setup(r => r.GetImportSyncRuleMappingsForMetaverseAttributeAsync(It.IsAny<int>(), It.IsAny<int>())).ReturnsAsync(new List<SyncRuleMapping>());
        _csRepo.Setup(r => r.CreateSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>())).Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.UpdateSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>())).Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.CreateSyncRuleAsync(It.IsAny<SyncRule>())).Returns(Task.CompletedTask);

        _initiator = TestUtilities.GetInitiatedBy();
    }

    private JimApplication BuildApplication(bool flagEnabled)
    {
        _repo.Setup(r => r.ServiceSettings).Returns(flagEnabled
            ? InMemoryServiceSettingsRepository.WithAllFeatureFlagsEnabled()
            : new InMemoryServiceSettingsRepository());
        // A real (in-memory) Sync repository, not a mock: the save-time Sequence counter skip-ahead
        // (RaiseSequenceStartIfHigherAsync, pre-existing behaviour outside this gate) needs one to raise against.
        return new JimApplication(_repo.Object, syncRepository: new JIM.InMemoryData.SyncRepository());
    }

    // A valid, minimal generated mapping: no base expression needed, single-valued Text target. Random/Guid
    // rather than Sequence, so the save-time counter skip-ahead (RaiseSequenceStartIfHigherAsync) no-ops without
    // needing a Sync repository mock; that raise is pre-existing behaviour, not part of the gate under test.
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
    public void CreateSyncRuleMappingAsync_NewGeneratedMapping_FlagOff_ThrowsFeatureDisabled()
    {
        var jim = BuildApplication(flagEnabled: false);
        var mapping = NewGeneratedMapping();

        var ex = Assert.ThrowsAsync<FeatureDisabledException>(async () =>
            await jim.ConnectedSystems.CreateSyncRuleMappingAsync(mapping, _initiator));

        Assert.That(ex!.Message, Does.Contain(FeatureFlagCatalogue.UniqueValueGeneration.DisplayName));
        _csRepo.Verify(r => r.CreateSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>()), Times.Never,
            "the gate must refuse before anything is written");
    }

    [Test]
    public async Task CreateSyncRuleMappingAsync_NewGeneratedMapping_FlagOn_SucceedsAsync()
    {
        var jim = BuildApplication(flagEnabled: true);
        var mapping = NewGeneratedMapping();

        await jim.ConnectedSystems.CreateSyncRuleMappingAsync(mapping, _initiator);

        _csRepo.Verify(r => r.CreateSyncRuleMappingAsync(mapping), Times.Once);
    }

    // ---- UpdateSyncRuleMappingAsync: converting an existing ordinary mapping to generated ----

    [Test]
    public void UpdateSyncRuleMappingAsync_ConvertingOrdinaryMappingToGenerated_FlagOff_ThrowsFeatureDisabled()
    {
        var jim = BuildApplication(flagEnabled: false);
        var mapping = NewGeneratedMapping();
        mapping.Id = 10; // an existing, persisted mapping newly carrying a Generation row (Id == 0): a conversion.

        var ex = Assert.ThrowsAsync<FeatureDisabledException>(async () =>
            await jim.ConnectedSystems.UpdateSyncRuleMappingAsync(mapping, _initiator));

        Assert.That(ex!.Message, Does.Contain(FeatureFlagCatalogue.UniqueValueGeneration.DisplayName));
        _csRepo.Verify(r => r.UpdateSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>()), Times.Never);
    }

    [Test]
    public async Task UpdateSyncRuleMappingAsync_ConvertingOrdinaryMappingToGenerated_FlagOn_SucceedsAsync()
    {
        var jim = BuildApplication(flagEnabled: true);
        var mapping = NewGeneratedMapping();
        mapping.Id = 11;

        await jim.ConnectedSystems.UpdateSyncRuleMappingAsync(mapping, _initiator);

        _csRepo.Verify(r => r.UpdateSyncRuleMappingAsync(mapping), Times.Once);
    }

    // ---- UpdateSyncRuleMappingSettingsAsync: editing an existing generation's own settings ----

    [Test]
    public async Task UpdateSyncRuleMappingSettingsAsync_ExistingGeneration_FlagOff_SucceedsAsync()
    {
        var jim = BuildApplication(flagEnabled: false);
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

        Assert.That(result, Is.Not.Null, "editing an existing generation's settings must be allowed while the flag is off");
        Assert.That(result!.Generation!.SequenceStart, Is.EqualTo(500));
        _csRepo.Verify(r => r.UpdateSyncRuleMappingAsync(mapping), Times.Once);
    }

    // ---- CreateOrUpdateSyncRuleAsync: a new generated mapping arriving through the whole-rule save ----

    [Test]
    public void CreateOrUpdateSyncRuleAsync_NewGeneratedMappingOnTheRule_FlagOff_ThrowsFeatureDisabled()
    {
        var jim = BuildApplication(flagEnabled: false);
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

        var ex = Assert.ThrowsAsync<FeatureDisabledException>(async () =>
            await jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(syncRule, _initiator));

        Assert.That(ex!.Message, Does.Contain(FeatureFlagCatalogue.UniqueValueGeneration.DisplayName));
        _csRepo.Verify(r => r.CreateSyncRuleAsync(It.IsAny<SyncRule>()), Times.Never,
            "the gate must refuse before the rule (or its mappings) is written");
    }

    // The flag-on counterpart is covered end to end, over a real database (the sequence skip-ahead this path also
    // performs needs one), by SyncRuleGeneratedValueSequenceSkipDatabaseTests.
}
