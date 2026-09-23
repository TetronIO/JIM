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
