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
using JIM.TestSupport;
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
    public void Classify_RuleDisabledWithReason_IsSyncAffecting()
    {
        // The schema refresh "Apply and Disable Dependents" path (#1485) disables a rule and records why in the same
        // save. The reason must classify rather than throw, and must not lift the change above the toggle's class.
        var before = SyncRule("HR Inbound");
        var after = SyncRule("HR Inbound");
        after.Enabled = false;
        after.DisabledReason = "Attribute 'department' was removed from the Connected System schema.";

        Assert.That(ClassifyChange(before, after), Is.EqualTo(ConfigurationChangeClass.SyncAffecting));
    }

    [Test]
    public void Classify_DisabledReasonOnly_IsCosmetic()
    {
        // The reason records why; it changes nothing about what synchronises.
        var before = SyncRule("HR Inbound");
        before.Enabled = false;
        before.DisabledReason = "Attribute 'department' was removed from the Connected System schema.";
        var after = SyncRule("HR Inbound");
        after.Enabled = false;
        after.DisabledReason = "Attribute 'department' was removed; re-map before enabling.";

        Assert.That(ClassifyChange(before, after), Is.EqualTo(ConfigurationChangeClass.Cosmetic));
    }

    [Test]
    public void Classify_AttributeFlowDisabledWithReason_IsSyncAffecting()
    {
        var before = SyncRule("HR Inbound");
        before.AttributeFlowRules.Add(new SyncRuleMapping { Id = 100, TargetMetaverseAttributeId = 5, Enabled = true });
        var after = SyncRule("HR Inbound");
        after.AttributeFlowRules.Add(new SyncRuleMapping
        {
            Id = 100,
            TargetMetaverseAttributeId = 5,
            Enabled = false,
            DisabledReason = "Source attribute 'department' was removed from the Connected System schema."
        });

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

    [Test]
    public void Classify_GeneratedMappingTokenKindChange_IsSyncAffecting()
    {
        // A token change alters what a future object receives; it never touches a value already assigned
        // (plan decision 10), so this is Class B, never A.
        var before = SyncRuleWithGeneratedMapping(GeneratedValueTokenKind.OnlyIfTaken);
        var after = SyncRuleWithGeneratedMapping(GeneratedValueTokenKind.Sequence);

        Assert.That(ClassifyChange(before, after), Is.EqualTo(ConfigurationChangeClass.SyncAffecting));
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

    [Test]
    public void ClassifyKey_FeatureFlagValue_IsCosmeticWithoutAPerKeyEntry()
    {
        // Feature flags (#1781) classify as a category, not one hand-added row per catalogue entry.
        using var catalogue = FeatureFlagCatalogueScope.Use(FeatureFlagCatalogueScope.InDevelopmentFlag);
        var result = ConfigurationChangeClassifier.ClassifyKey(
            ConfigurationSnapshotService.ServiceSettingObjectType, "value", FeatureFlagCatalogueScope.InDevelopmentFlag.Key);

        Assert.That(result, Is.EqualTo(ConfigurationChangeClass.Cosmetic));
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

    #region Unique Value Generation (#242)

    [Test]
    public void ClassifyKey_UniqueValueGenerationKeys_AreSyncAffecting()
    {
        string[] keys =
        [
            "generation", "tokenKind", "suffixStyle", "suffixStart", "sequenceStart", "sequenceIncrement",
            "fixedWidth", "onWidthExceeded", "randomFormat", "randomLength", "separator", "attemptLimit",
            "neverReuse", "collisionRemediation", "exclusions", "exclusion"
        ];

        using (Assert.EnterMultipleScope())
        {
            foreach (var key in keys)
            {
                Assert.That(ConfigurationChangeClassifier.ClassifyKey(ConfigurationSnapshotService.SyncRuleObjectType, key),
                    Is.EqualTo(ConfigurationChangeClass.SyncAffecting), $"'{key}' must classify as sync-affecting");
            }
        }
    }

    #endregion

    #region Run Profile settings (#1618, #2049, SPEC-1082 D10)

    [TestCase(false, true)]
    [TestCase(true, false)]
    public void Classify_AllowObjectTypeChangesSwitched_IsSyncAffecting(bool before, bool after)
    {
        // Switched on, the Full Import retires the held object and imports a new one of the new Object Type; switched
        // off, it refuses the change again. Either way the run's outcome changes, but nothing is deleted by saving it:
        // the retired object is disconnected by a later synchronisation, under Deletion Rules classified on their own.
        var beforeProfile = FullImportRunProfile();
        beforeProfile.AllowObjectTypeChanges = before;
        var afterProfile = FullImportRunProfile();
        afterProfile.AllowObjectTypeChanges = after;

        Assert.That(ClassifyChange(SystemWith(beforeProfile), SystemWith(afterProfile)),
            Is.EqualTo(ConfigurationChangeClass.SyncAffecting));
    }

    [TestCase(false, true)]
    [TestCase(true, false)]
    public void Classify_VerificationModeSwitched_IsCosmetic(bool before, bool after)
    {
        // Verification Mode changes how a Full Import reads (no content-hash skips) and what it reports, not what it
        // imports, so it must not raise the "Full Synchronisation needed" indicator.
        var beforeProfile = FullImportRunProfile();
        beforeProfile.VerifyImportContentHashes = before;
        var afterProfile = FullImportRunProfile();
        afterProfile.VerifyImportContentHashes = after;

        Assert.That(ClassifyChange(SystemWith(beforeProfile), SystemWith(afterProfile)),
            Is.EqualTo(ConfigurationChangeClass.Cosmetic));
    }

    [TestCase(500, null)]
    [TestCase(null, 500)]
    [TestCase(500, 5000)]
    public void Classify_DetectedDeletionLimitChanged_IsSyncAffecting(int? before, int? after)
    {
        // Relaxing the limit lets a Full Import mark objects deleted that it would otherwise have left alone, the
        // same shape as relaxing Partition Validation Mode; tightening it can stop deletion detection altogether.
        var beforeProfile = FullImportRunProfile();
        beforeProfile.MaxDetectedDeletions = before;
        var afterProfile = FullImportRunProfile();
        afterProfile.MaxDetectedDeletions = after;

        Assert.That(ClassifyChange(SystemWith(beforeProfile), SystemWith(afterProfile)),
            Is.EqualTo(ConfigurationChangeClass.SyncAffecting));
    }

    [Test]
    public void Classify_ExportLimitsChanged_IsSyncAffecting()
    {
        var beforeProfile = ExportRunProfile();
        var afterProfile = ExportRunProfile();
        afterProfile.MaxCreates = 5;
        afterProfile.MaxUpdates = 0;
        afterProfile.MaxDeletes = 100;

        Assert.That(ClassifyChange(SystemWith(beforeProfile), SystemWith(afterProfile)),
            Is.EqualTo(ConfigurationChangeClass.SyncAffecting));
    }

    [Test]
    public void Classify_DetectedDeletionPercentLimitRemoved_IsSyncAffecting()
    {
        var beforeProfile = FullImportRunProfile();
        beforeProfile.MaxDetectedDeletionsPercent = 10;
        var afterProfile = FullImportRunProfile();

        Assert.That(ClassifyChange(SystemWith(beforeProfile), SystemWith(afterProfile)),
            Is.EqualTo(ConfigurationChangeClass.SyncAffecting));
    }

    #endregion

    #region Helpers

    private ConfigurationChangeClass ClassifyChange(SyncRule before, SyncRule after)
    {
        var diff = _diffs.Diff(_snapshots.CreateSnapshot(before, HashKey), _snapshots.CreateSnapshot(after, HashKey));
        return ConfigurationChangeClassifier.Classify(diff);
    }

    private ConfigurationChangeClass ClassifyChange(ConnectedSystem before, ConnectedSystem after)
    {
        var diff = _diffs.Diff(_snapshots.CreateSnapshot(before, HashKey), _snapshots.CreateSnapshot(after, HashKey));
        return ConfigurationChangeClassifier.Classify(diff);
    }

    private static ConnectedSystemRunProfile FullImportRunProfile() => new()
    {
        Id = 50, Name = "Full Import", RunType = ConnectedSystemRunType.FullImport, PageSize = 100
    };

    private static ConnectedSystemRunProfile ExportRunProfile() => new()
    {
        Id = 51, Name = "Export", RunType = ConnectedSystemRunType.Export, PageSize = 100
    };

    private static ConnectedSystem SystemWith(ConnectedSystemRunProfile runProfile) => new()
    {
        Id = 3, Name = "Contoso AD", ConnectorDefinitionId = 4, RunProfiles = [runProfile]
    };

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

    private static SyncRule SyncRuleWithGeneratedMapping(GeneratedValueTokenKind tokenKind)
    {
        var rule = SyncRule("HR Inbound");
        rule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            Id = 100,
            TargetMetaverseAttributeId = 5,
            Generation = new SyncRuleMappingGeneration { Id = 900, TokenKind = tokenKind }
        });
        return rule;
    }

    #endregion
}
