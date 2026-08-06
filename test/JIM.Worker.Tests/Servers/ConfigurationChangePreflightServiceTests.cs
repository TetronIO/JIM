// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Services;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests for <see cref="ConfigurationChangePreflightService"/>: what JIM tells an administrator about a configuration
/// change *before* it is saved.
///
/// The behaviour that matters is symmetry with the classifier's promise. A rename must save in silence (a dialog that
/// fires on harmless edits is one administrators learn to dismiss, which is how the dangerous dropdown beside it gets
/// missed), and a destructive toggle must state, specifically, what it is about to do. Where JIM cannot tell what
/// changed, it must say nothing rather than guess in either direction.
/// </summary>
[TestFixture]
public class ConfigurationChangePreflightServiceTests
{
    private Mock<IRepository> _repo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<IServiceSettingsRepository> _serviceSettingsRepo = null!;
    private JimApplication _jim = null!;
    private ConfigurationSnapshotService _snapshots = null!;

    private const int RuleId = 42;
    private static readonly byte[] HashKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();
        _repo = new Mock<IRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _serviceSettingsRepo = new Mock<IServiceSettingsRepository>();
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _repo.Setup(r => r.ServiceSettings).Returns(_serviceSettingsRepo.Object);

        SetChangeTrackingEnabled(true);
        SetHashKey();

        _jim = new JimApplication(_repo.Object);
        _snapshots = _jim.ConfigurationSnapshots;
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    #region Silence for changes that cannot hurt

    [Test]
    public async Task EvaluateSyncRuleAsync_RenameOnly_NeedsNoAcknowledgementAsync()
    {
        SetStoredBaseline(Rule("HR Inbound"));
        var proposed = Rule("HR Inbound (EMEA)");

        var result = await _jim.ConfigurationChangePreflight.EvaluateSyncRuleAsync(proposed);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.RequiresAcknowledgement, Is.False, "renaming a rule cannot change a synchronisation outcome");
            Assert.That(result.HighestClass, Is.EqualTo(ConfigurationChangeClass.Cosmetic));
        }
    }

    [Test]
    public async Task EvaluateSyncRuleAsync_NothingChanged_NeedsNoAcknowledgementAsync()
    {
        SetStoredBaseline(Rule("HR Inbound"));

        var result = await _jim.ConfigurationChangePreflight.EvaluateSyncRuleAsync(Rule("HR Inbound"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.RequiresAcknowledgement, Is.False);
            Assert.That(result.Items, Is.Empty);
            Assert.That(result.HighestClass, Is.EqualTo(ConfigurationChangeClass.NotClassified));
        }
    }

    [Test]
    public async Task EvaluateSyncRuleAsync_NewRule_NeedsNoAcknowledgementAsync()
    {
        // A create has no prior state, so nothing existing is at risk. This mirrors the classifier, which records no
        // class for a create.
        var proposed = Rule("Brand New");
        proposed.Id = 0;
        proposed.OutboundDeprovisionAction = OutboundDeprovisionAction.Delete;

        var result = await _jim.ConfigurationChangePreflight.EvaluateSyncRuleAsync(proposed);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.RequiresAcknowledgement, Is.False);
            Assert.That(result.BaselineUnavailable, Is.False, "a create is knowably safe, not unknowable");
        }
        _activityRepo.Verify(r => r.GetLatestConfigurationChangeSnapshotAsync(It.IsAny<ActivityTargetType>(), It.IsAny<int>()),
            Times.Never, "a create needs no baseline lookup");
    }

    #endregion

    #region Sync-affecting changes

    [Test]
    public async Task EvaluateSyncRuleAsync_DisablingTheRule_AsksForAcknowledgementWithoutClaimingDestructionAsync()
    {
        SetStoredBaseline(Rule("HR Inbound"));
        var proposed = Rule("HR Inbound");
        proposed.Enabled = false;

        var result = await _jim.ConfigurationChangePreflight.EvaluateSyncRuleAsync(proposed);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.RequiresAcknowledgement, Is.True);
            Assert.That(result.IsDestructive, Is.False);
            Assert.That(result.Items.Select(i => i.Key), Does.Contain("enabled"));
            Assert.That(result.DestructiveItems, Is.Empty);
        }
    }

    [Test]
    public async Task EvaluateSyncRuleAsync_NestedChange_LabelsThePropertyByItsPathAsync()
    {
        // "Order" on its own means nothing on a page full of orderable things; the path is what makes it legible.
        var baseline = Rule("HR Inbound");
        baseline.AttributeFlowRules = [MappingWithSourceOrder(1)];
        SetStoredBaseline(baseline);

        var proposed = Rule("HR Inbound");
        proposed.AttributeFlowRules = [MappingWithSourceOrder(2)];

        var result = await _jim.ConfigurationChangePreflight.EvaluateSyncRuleAsync(proposed);

        var item = result.Items.SingleOrDefault(i => i.Key == "order");
        Assert.That(item, Is.Not.Null, "the changed nested property should be reported");
        Assert.That(item!.Label, Does.StartWith("Attribute Flow"),
            "a nested property must carry its parent sections so it can be identified");
    }

    #endregion

    #region Destructive changes

    [Test]
    public async Task EvaluateSyncRuleAsync_DeprovisionActionToDelete_StatesTheConsequenceAsync()
    {
        SetStoredBaseline(Rule("HR Inbound"));
        var proposed = Rule("HR Inbound");
        proposed.OutboundDeprovisionAction = OutboundDeprovisionAction.Delete;

        var result = await _jim.ConfigurationChangePreflight.EvaluateSyncRuleAsync(proposed);

        var item = result.DestructiveItems.SingleOrDefault();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsDestructive, Is.True);
            Assert.That(item, Is.Not.Null);
            Assert.That(item!.Consequence, Does.Contain("deleted"),
                "the administrator is consenting to deletion, so the copy must say so");
            Assert.That(item!.NewDisplayValue, Is.EqualTo("Delete"));
        }
    }

    [Test]
    public async Task EvaluateSyncRuleAsync_DeprovisionActionBackToDisconnect_DoesNotWarnOfDeletionAsync()
    {
        // Removing the risk is still a destructive-class property, so it is still confirmed; but telling an
        // administrator they are about to delete objects when they have just stopped that happening is a lie, and
        // lies are how a dialog earns the reflex dismissal that makes it useless.
        var baseline = Rule("HR Inbound");
        baseline.OutboundDeprovisionAction = OutboundDeprovisionAction.Delete;
        SetStoredBaseline(baseline);

        var proposed = Rule("HR Inbound");
        proposed.OutboundDeprovisionAction = OutboundDeprovisionAction.Disconnect;

        var result = await _jim.ConfigurationChangePreflight.EvaluateSyncRuleAsync(proposed);

        var item = result.DestructiveItems.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsDestructive, Is.True, "the class must match what the change history will record");
            Assert.That(item.Consequence, Does.Not.Contain("will be deleted"));
            Assert.That(item.Consequence, Does.Contain("disconnected"));
        }
    }

    [Test]
    public async Task EvaluateSyncRuleAsync_RenameAlongsideDestructiveToggle_TakesTheHighestClassAsync()
    {
        SetStoredBaseline(Rule("HR Inbound"));
        var proposed = Rule("HR Inbound (EMEA)");
        proposed.OutboundDeprovisionAction = OutboundDeprovisionAction.Delete;

        var result = await _jim.ConfigurationChangePreflight.EvaluateSyncRuleAsync(proposed);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsDestructive, Is.True, "a cosmetic edit must never mask a destructive one in the same save");
            Assert.That(result.Items.Select(i => i.Key), Does.Contain("name"), "the administrator should see everything they are saving");
            Assert.That(result.Items[0].Class, Is.EqualTo(ConfigurationChangeClass.Destructive),
                "the most consequential change must lead");
        }
    }

    #endregion

    #region When JIM cannot tell

    [Test]
    public async Task EvaluateSyncRuleAsync_NoStoredBaseline_ReportsUnknownRatherThanSafeAsync()
    {
        _activityRepo.Setup(r => r.GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType.SynchronisationRule, RuleId))
            .ReturnsAsync((string?)null);
        var proposed = Rule("HR Inbound");
        proposed.OutboundDeprovisionAction = OutboundDeprovisionAction.Delete;

        var result = await _jim.ConfigurationChangePreflight.EvaluateSyncRuleAsync(proposed);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.BaselineUnavailable, Is.True);
            Assert.That(result.RequiresAcknowledgement, Is.False,
                "with no baseline JIM cannot say what changed, and inventing an acknowledgement would be guesswork");
        }
    }

    [Test]
    public async Task EvaluateSyncRuleAsync_ChangeTrackingDisabled_ReportsUnknownAsync()
    {
        // With tracking off no new baselines are written, so whatever is stored has gone stale and diffing against it
        // would report changes made days ago as though they were happening now.
        SetChangeTrackingEnabled(false);
        SetStoredBaseline(Rule("HR Inbound"));
        var proposed = Rule("HR Inbound");
        proposed.OutboundDeprovisionAction = OutboundDeprovisionAction.Delete;

        var result = await _jim.ConfigurationChangePreflight.EvaluateSyncRuleAsync(proposed);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.BaselineUnavailable, Is.True);
            Assert.That(result.RequiresAcknowledgement, Is.False);
        }
    }

    #endregion

    #region Helpers

    private void SetStoredBaseline(SyncRule rule) =>
        _activityRepo.Setup(r => r.GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType.SynchronisationRule, RuleId))
            .ReturnsAsync(ConfigurationSnapshotService.Serialise(_snapshots.CreateSnapshot(rule, HashKey)));

    private void SetChangeTrackingEnabled(bool enabled) =>
        _serviceSettingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled))
            .ReturnsAsync(new ServiceSetting
            {
                Key = Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled,
                ValueType = ServiceSettingValueType.Boolean,
                Value = enabled ? "true" : "false"
            });

    private void SetHashKey() =>
        _serviceSettingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ConfigurationChangeHashKey))
            .ReturnsAsync(new ServiceSetting
            {
                Key = Constants.SettingKeys.ConfigurationChangeHashKey,
                ValueType = ServiceSettingValueType.StringEncrypted,
                Value = Convert.ToBase64String(HashKey)
            });

    private static SyncRule Rule(string name) => new()
    {
        Id = RuleId,
        Name = name,
        Direction = SyncRuleDirection.Import,
        Enabled = true,
        ConnectedSystemId = 3,
        ConnectedSystemObjectTypeId = 7,
        MetaverseObjectTypeId = 1
    };

    private static SyncRuleMapping MappingWithSourceOrder(int order)
    {
        var mapping = new SyncRuleMapping { Id = 500, TargetMetaverseAttributeId = 11 };
        mapping.Sources.Add(new SyncRuleMappingSource { Id = 600, Order = order, MetaverseAttributeId = 12 });
        return mapping;
    }

    #endregion
}
