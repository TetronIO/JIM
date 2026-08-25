// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Reflection;
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
/// The guard that keeps the classification map honest. It drives <see cref="ConfigurationSnapshotService"/>
/// with fully populated objects, walks every node key the snapshot actually emits, and asserts each one
/// has an explicit classification. Adding a configuration property without classifying it therefore fails
/// the build naming the key, instead of silently defaulting to a class nobody chose.
///
/// This is the same enforcement idea as BulkInsertColumnCompletenessTests, and exists for the same reason:
/// a hand-maintained map that nothing checks will drift, and the drift stays invisible until it produces a
/// wrong answer in front of a customer. See engineering/CONFIGURATION_CHANGE_CLASSIFICATION.md.
/// </summary>
[TestFixture]
public class ConfigurationChangeClassificationCompletenessTests
{
    private JimApplication _jim = null!;
    private ConfigurationSnapshotService _service = null!;
    private static readonly byte[] HashKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();
        _jim = new JimApplication(new Mock<IRepository>().Object);
        _service = _jim.ConfigurationSnapshots;
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    #region Snapshot key completeness

    [Test]
    public void Classification_SyncRuleSnapshot_ClassifiesEveryEmittedKey()
    {
        AssertEveryKeyClassified(_service.CreateSnapshot(BuildFullSyncRule(), HashKey));
    }

    [Test]
    public void Classification_ConnectedSystemSnapshot_ClassifiesEveryEmittedKey()
    {
        AssertEveryKeyClassified(_service.CreateSnapshot(BuildFullConnectedSystem(), HashKey));
    }

    [Test]
    public void Classification_MetaverseObjectTypeSnapshot_ClassifiesEveryEmittedKey()
    {
        AssertEveryKeyClassified(_service.CreateSnapshot(BuildFullMetaverseObjectType(), HashKey));
    }

    [Test]
    public void Classification_MetaverseAttributeSnapshot_ClassifiesEveryEmittedKey()
    {
        AssertEveryKeyClassified(_service.CreateSnapshot(BuildFullMetaverseAttribute(), HashKey));
    }

    #endregion

    #region Service Settings

    [Test]
    public void Classification_EveryDeclaredServiceSettingKey_IsClassified()
    {
        // Reflects over Constants.SettingKeys so a newly declared setting fails here until it is
        // classified, rather than throwing the first time an administrator edits it.
        var settingKeys = typeof(Constants.SettingKeys)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.That(settingKeys, Is.Not.Empty, "expected Constants.SettingKeys to declare setting keys");

        var unclassified = settingKeys
            .Where(key => !TryClassifyServiceSetting(key))
            .ToList();

        Assert.That(unclassified, Is.Empty,
            "Service Setting(s) with no classification: " + string.Join(", ", unclassified) +
            ". Classify them in ConfigurationChangeClassifier and in " +
            "engineering/CONFIGURATION_CHANGE_CLASSIFICATION.md.");
    }

    #endregion

    #region Destructive consequence copy

    [Test]
    public void Consequences_EveryDestructiveKey_HasCuratedCopy()
    {
        // A destructive property that reaches an administrator with no stated consequence gives them a dialog
        // demanding consent to something unnamed, which is worse than not asking: they cannot weigh it, so they
        // click through. Every Class A key must say what it will do.
        var snapshots = new[]
        {
            _service.CreateSnapshot(BuildFullSyncRule(), HashKey),
            _service.CreateSnapshot(BuildFullConnectedSystem(), HashKey),
            _service.CreateSnapshot(BuildFullMetaverseObjectType(), HashKey),
            _service.CreateSnapshot(BuildFullMetaverseAttribute(), HashKey)
        };

        // Both change types, because a key can be destructive on removal alone (a container's selection is its
        // presence in the snapshot, so deselecting one is a removal rather than a property edit). A key classified
        // that way reaches the same dialog and owes the same explanation.
        var changeTypes = new[] { ConfigurationDiffChangeType.Modified, ConfigurationDiffChangeType.Removed };

        var missing = snapshots
            .SelectMany(s => CollectKeys(s.Root).Distinct()
                .SelectMany(key => changeTypes.Select(changeType => (Snapshot: s, Key: key, ChangeType: changeType))))
            .Where(x => ConfigurationChangeClassifier.ClassifyKey(x.Snapshot.ObjectType, x.Key, x.Snapshot.ObjectKey, x.ChangeType)
                        == ConfigurationChangeClass.Destructive)
            .Where(x => !ConfigurationChangeConsequences.HasCopyFor(x.Snapshot.ObjectType, x.Key))
            .Select(x => $"{x.Snapshot.ObjectType}.{x.Key}")
            .Distinct()
            .ToList();

        Assert.That(missing, Is.Empty,
            "Destructive propert(ies) with no stated consequence: " + string.Join(", ", missing) +
            ". Add copy to ConfigurationChangeConsequences saying what the change will do.");
    }

    #endregion

    #region Object type coverage

    [Test]
    public void Classification_EverySnapshotObjectType_IsEitherWhollyCosmeticOrHasAKeyTable()
    {
        // Every object type the snapshot service can produce must be accounted for: either declared
        // wholly cosmetic, or backed by a per-key table. A new snapshot type fails here.
        var objectTypes = typeof(ConfigurationSnapshotService)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string)
                        && f.Name.EndsWith("ObjectType", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.That(objectTypes, Is.Not.Empty, "expected snapshot object type discriminators to be declared");

        var unaccounted = objectTypes.Where(t => !IsAccountedFor(t)).ToList();

        Assert.That(unaccounted, Is.Empty,
            "Snapshot object type(s) with no classification: " + string.Join(", ", unaccounted) +
            ". Either declare them wholly cosmetic or give them a key table in " +
            "ConfigurationChangeClassifier, and record the decision in " +
            "engineering/CONFIGURATION_CHANGE_CLASSIFICATION.md.");
    }

    #endregion

    #region Helpers

    private static void AssertEveryKeyClassified(ConfigurationSnapshot snapshot)
    {
        var failures = new List<string>();
        foreach (var key in CollectKeys(snapshot.Root).Distinct())
        {
            try
            {
                ConfigurationChangeClassifier.ClassifyKey(snapshot.ObjectType, key, snapshot.ObjectKey);
            }
            catch (InvalidOperationException ex)
            {
                failures.Add($"{key}: {ex.Message}");
            }
        }

        Assert.That(failures, Is.Empty,
            $"Unclassified key(s) on '{snapshot.ObjectType}':{Environment.NewLine}" +
            string.Join(Environment.NewLine, failures));
    }

    private static bool TryClassifyServiceSetting(string settingKey)
    {
        try
        {
            ConfigurationChangeClassifier.ClassifyKey(
                ConfigurationSnapshotService.ServiceSettingObjectType, "value", settingKey);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsAccountedFor(string objectType)
    {
        if (ConfigurationChangeClassifier.IsWhollyCosmetic(objectType))
            return true;

        try
        {
            // Service Settings are keyed differently; probe a structural node instead.
            var probeKey = objectType == ConfigurationSnapshotService.ServiceSettingObjectType ? "key" : "name";
            ConfigurationChangeClassifier.ClassifyKey(objectType, probeKey);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static IEnumerable<string> CollectKeys(ConfigurationSnapshotNode node)
    {
        yield return node.Key;

        if (node.Children == null)
            yield break;

        foreach (var key in node.Children.SelectMany(CollectKeys))
            yield return key;
    }

    #endregion

    #region Fully populated objects

    // These deliberately populate every collection, because an empty collection emits no child nodes and
    // would let an unclassified nested property slip past the guard.

    private static SyncRule BuildFullSyncRule()
    {
        var mapping = new SyncRuleMapping
        {
            Id = 100,
            TargetMetaverseAttributeId = 5,
            TargetConnectedSystemAttributeId = 6,
            Priority = 2,
            NullIsValue = true,
            InitialExportOnly = true
        };
        mapping.Sources.Add(new SyncRuleMappingSource
        {
            Id = 200, Order = 0, ConnectedSystemAttributeId = 9, MetaverseAttributeId = 10, Expression = "x"
        });

        var matchingRule = new ObjectMatchingRule { Id = 300, Order = 0, TargetMetaverseAttributeId = 11 };
        matchingRule.Sources.Add(new ObjectMatchingRuleSource
        {
            Id = 400, Order = 0, ConnectedSystemAttributeId = 12, Expression = "y"
        });

        var group = new SyncRuleScopingCriteriaGroup { Id = 500, Position = 0 };
        group.Criteria.Add(new SyncRuleScopingCriteria
        {
            Id = 600,
            MetaverseAttributeId = 13,
            ConnectedSystemAttributeId = 14,
            StringValue = "s",
            IntValue = 1,
            LongValue = 2L,
            DecimalValue = 3.5m,
            DateTimeValue = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            BoolValue = true,
            GuidValue = Guid.Empty,
            CaseSensitive = true
        });

        var rule = new SyncRule
        {
            Id = 42,
            Name = "HR Inbound",
            Description = "Everything populated.",
            Direction = SyncRuleDirection.Import,
            Enabled = true,
            ProvisionToConnectedSystem = true,
            ProjectToMetaverse = true,
            EnforceState = true,
            ConnectedSystemId = 3,
            ConnectedSystemObjectTypeId = 7,
            MetaverseObjectTypeId = 1
        };
        rule.AttributeFlowRules.Add(mapping);
        rule.ObjectMatchingRules.Add(matchingRule);
        rule.ObjectScopingCriteriaGroups.Add(group);
        return rule;
    }

    private static ConnectedSystem BuildFullConnectedSystem()
    {
        var objectType = new ConnectedSystemObjectType { Id = 10, Name = "user", Selected = true };
        objectType.Attributes.Add(new ConnectedSystemObjectTypeAttribute
        {
            Id = 20, Name = "sAMAccountName", IsExternalId = true, IsSecondaryExternalId = false
        });
        // Simple Mode Object Matching Rules hang off the object type, and are the only place several matching keys
        // appear; without one the snapshot never emits them and the guard silently covers less than it claims.
        var simpleModeMatchingRule = new ObjectMatchingRule
        {
            Id = 25, Order = 0, CaseSensitive = true, MetaverseObjectTypeId = 1, TargetMetaverseAttributeId = 13
        };
        simpleModeMatchingRule.Sources.Add(new ObjectMatchingRuleSource
        {
            Id = 26, Order = 0, ConnectedSystemAttributeId = 14, Expression = "x"
        });
        objectType.ObjectMatchingRules.Add(simpleModeMatchingRule);

        var partition = new ConnectedSystemPartition
        {
            Id = 30, Name = "DC=example", ExternalId = "abc", Selected = true,
            // Selected matters: BuildContainers captures only selected containers, so an unselected one emits no
            // keys at all and this guard silently covers nothing beneath the partition.
            Containers = [new ConnectedSystemContainer { Id = 40, Name = "OU=Users", ExternalId = "def", Hidden = false, Selected = true }]
        };

        var system = new ConnectedSystem
        {
            Id = 3,
            Name = "Payroll",
            Description = "Everything populated.",
            ConnectorDefinitionId = 1,
            MaxExportParallelism = 4,
            RequireSecureTransport = true,
            // Without a configuration here the snapshot emits no Password Synchronisation keys at all, and this
            // guard silently covers less than it claims: that is exactly how every one of them went unclassified,
            // which would have thrown the first time an administrator saved those settings (#1119).
            PasswordSynchronisation = new ConnectedSystemPasswordSynchronisation
            {
                Id = 70,
                ConnectedSystemId = 3,
                Enabled = true,
                TargetObjectTypeId = 10,
                MaxRetries = 5,
                RetryBackoffBase = TimeSpan.FromMinutes(5)
            }
        };
        // Setting values, plain and encrypted. Their absence here is what let the connector-named-key gap through:
        // every Connected System settings save failed to classify and was recorded unclassified.
        system.SettingValues.Add(new ConnectedSystemSettingValue
        {
            Id = 60,
            Setting = new ConnectorDefinitionSetting { Id = 60, Name = "File Path", Type = ConnectedSystemSettingType.String },
            StringValue = "/mnt/import/hr.csv"
        });
        system.SettingValues.Add(new ConnectedSystemSettingValue
        {
            Id = 61,
            Setting = new ConnectorDefinitionSetting { Id = 61, Name = "Password", Type = ConnectedSystemSettingType.StringEncrypted },
            StringEncryptedValue = "ciphertext"
        });
        system.ObjectTypes!.Add(objectType);
        system.Partitions = [partition];
        system.RunProfiles!.Add(new ConnectedSystemRunProfile
        {
            Id = 50, Name = "Full Import", RunType = ConnectedSystemRunType.FullImport, PageSize = 100
        });
        return system;
    }

    private static MetaverseObjectType BuildFullMetaverseObjectType()
    {
        var type = new MetaverseObjectType
        {
            Id = 1,
            Name = "User",
            PluralName = "Users",
            BuiltIn = false,
            DeletionGracePeriod = TimeSpan.FromDays(7),
            // An empty trigger list emits no items, so the per-entry key never appeared and went unclassified.
            DeletionTriggerConnectedSystemIds = [3]
        };
        type.Attributes.Add(new MetaverseAttribute { Id = 2, Name = "displayName" });
        return type;
    }

    private static MetaverseAttribute BuildFullMetaverseAttribute()
    {
        var attribute = new MetaverseAttribute
        {
            Id = 2,
            Name = "displayName",
            BuiltIn = false
        };
        attribute.MetaverseObjectTypes = [new MetaverseObjectType { Id = 1, Name = "User", PluralName = "Users" }];
        return attribute;
    }

    #endregion
}
