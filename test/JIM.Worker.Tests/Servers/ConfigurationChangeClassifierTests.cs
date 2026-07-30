// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Services;
using JIM.Data;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Behaviour of <see cref="ConfigurationChangeClassifier"/>: that a change takes the highest class among
/// the properties that actually changed, that cosmetic-only edits stay silent, and that an unclassified
/// property fails loudly rather than defaulting. See engineering/CONFIGURATION_CHANGE_CLASSIFICATION.md.
/// </summary>
[TestFixture]
public class ConfigurationChangeClassifierTests
{
    private JimApplication _jim = null!;
    private ConfigurationSnapshotService _snapshots = null!;
    private ConfigurationDiffService _diffs = null!;
    private static readonly byte[] HashKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();
        _jim = new JimApplication(new Mock<IRepository>().Object);
        _snapshots = _jim.ConfigurationSnapshots;
        _diffs = new ConfigurationDiffService();
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    #region Highest class wins

    [Test]
    public void Classify_RenameOnly_IsCosmetic()
    {
        // The question an administrator actually asks: renaming a rule must not raise a preview.
        var before = SyncRule("HR Inbound");
        var after = SyncRule("HR Inbound (EMEA)");

        Assert.That(ClassifyChange(before, after), Is.EqualTo(ConfigurationChangeClass.Cosmetic));
    }

    [Test]
    public void Classify_DescriptionOnly_IsCosmetic()
    {
        var before = SyncRule("HR Inbound");
        var after = SyncRule("HR Inbound");
        after.Description = "Now with a description.";

        Assert.That(ClassifyChange(before, after), Is.EqualTo(ConfigurationChangeClass.Cosmetic));
    }

    [Test]
    public void Classify_ScopeChange_IsSyncAffecting()
    {
        var before = SyncRule("HR Inbound");
        var after = SyncRule("HR Inbound");
        after.Enabled = false;

        Assert.That(ClassifyChange(before, after), Is.EqualTo(ConfigurationChangeClass.SyncAffecting));
    }

    [Test]
    public void Classify_DeprovisionActionChange_IsDestructive()
    {
        var before = SyncRule("HR Inbound");
        var after = SyncRule("HR Inbound");
        after.OutboundDeprovisionAction = OutboundDeprovisionAction.Delete;

        Assert.That(ClassifyChange(before, after), Is.EqualTo(ConfigurationChangeClass.Destructive));
    }

    [Test]
    public void Classify_RenameAlongsideDeprovisionActionChange_TakesTheHighestClass()
    {
        // A cosmetic edit must never mask a destructive one sharing the same save.
        var before = SyncRule("HR Inbound");
        var after = SyncRule("HR Inbound (EMEA)");
        after.OutboundDeprovisionAction = OutboundDeprovisionAction.Delete;

        Assert.That(ClassifyChange(before, after), Is.EqualTo(ConfigurationChangeClass.Destructive));
    }

    [Test]
    public void Classify_NoChange_IsNotClassified()
    {
        var before = SyncRule("HR Inbound");
        var after = SyncRule("HR Inbound");

        Assert.That(ClassifyChange(before, after), Is.EqualTo(ConfigurationChangeClass.NotClassified));
    }

    #endregion

    #region Object types and settings

    [Test]
    public void ClassifyKey_WhollyCosmeticObjectType_IsCosmeticWithoutAKeyTable()
    {
        // Schedules never affect a synchronisation outcome, so any of their properties is Class C.
        var result = ConfigurationChangeClassifier.ClassifyKey(
            ConfigurationSnapshotService.ScheduleObjectType, "anyPropertyAtAll");

        Assert.That(result, Is.EqualTo(ConfigurationChangeClass.Cosmetic));
    }

    [Test]
    public void ClassifyKey_ServiceSettingValue_IsClassifiedBySettingKeyNotNodeKey()
    {
        var partitionValidation = ConfigurationChangeClassifier.ClassifyKey(
            ConfigurationSnapshotService.ServiceSettingObjectType, "value", Constants.SettingKeys.PartitionValidationMode);
        var pageSize = ConfigurationChangeClassifier.ClassifyKey(
            ConfigurationSnapshotService.ServiceSettingObjectType, "value", Constants.SettingKeys.SyncPageSize);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(partitionValidation, Is.EqualTo(ConfigurationChangeClass.SyncAffecting));
            Assert.That(pageSize, Is.EqualTo(ConfigurationChangeClass.Cosmetic));
        }
    }

    [Test]
    public void ClassifyKey_ServiceSettingValueWithoutASettingKey_Throws()
    {
        // Silently classifying an unidentified setting would be a guess; the framework must not guess.
        Assert.Throws<InvalidOperationException>(() => ConfigurationChangeClassifier.ClassifyKey(
            ConfigurationSnapshotService.ServiceSettingObjectType, "value"));
    }

    #endregion

    #region No default class

    [Test]
    public void ClassifyKey_UnknownProperty_ThrowsNamingTheKey()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ConfigurationChangeClassifier.ClassifyKey(
            ConfigurationSnapshotService.SyncRuleObjectType, "somePropertyNobodyClassified"));

        Assert.That(ex!.Message, Does.Contain("somePropertyNobodyClassified"),
            "the failure must name the offending key so the developer knows what to classify");
    }

    [Test]
    public void ClassifyKey_UnknownObjectType_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => ConfigurationChangeClassifier.ClassifyKey(
            "SomeNewSnapshotType", "name"));
    }

    #endregion

    #region Helpers

    private ConfigurationChangeClass ClassifyChange(SyncRule before, SyncRule after)
    {
        var diff = _diffs.Diff(_snapshots.CreateSnapshot(before, HashKey), _snapshots.CreateSnapshot(after, HashKey));
        return ConfigurationChangeClassifier.Classify(diff);
    }

    private static SyncRule SyncRule(string name) => new()
    {
        Id = 42,
        Name = name,
        Direction = SyncRuleDirection.Import,
        Enabled = true,
        ConnectedSystemId = 3,
        ConnectedSystemObjectTypeId = 7,
        MetaverseObjectTypeId = 1
    };

    #endregion
}
