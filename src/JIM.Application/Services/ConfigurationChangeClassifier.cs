// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;

namespace JIM.Application.Services;

/// <summary>
/// Decides how consequential a configuration change is, so JIM knows whether to demand confirmation,
/// offer a preview, or say nothing. Classification is a pure function over the diff that
/// <see cref="ConfigurationDiffService"/> already computes on every configuration save; nothing extra
/// is diffed or intercepted.
///
/// **The classification tables here and the tables in engineering/CONFIGURATION_CHANGE_CLASSIFICATION.md
/// must agree.** That document is the reviewable source of truth and carries the reason for every
/// decision; this file is its executable mirror. When you add a configuration property, classify it in
/// both. There is deliberately no default class: an unclassified key throws, and
/// ConfigurationChangeClassificationCompletenessTests turns that into a build failure naming the key,
/// rather than letting the map rot into a framework that warns about the wrong things.
///
/// Keys are matched per object type without path qualification, because no key within a single object
/// type currently carries two different classes. Where a key does repeat (a `name` on a Run Profile and
/// on a Partition), both occurrences share a class, so the flat lookup is unambiguous. If a future
/// property introduces a genuine collision, split it into a path-qualified entry rather than picking
/// one class for both.
/// </summary>
public static class ConfigurationChangeClassifier
{
    private const ConfigurationChangeClass A = ConfigurationChangeClass.Destructive;
    private const ConfigurationChangeClass B = ConfigurationChangeClass.SyncAffecting;
    private const ConfigurationChangeClass C = ConfigurationChangeClass.Cosmetic;

    /// <summary>
    /// Object types that take no part in synchronisation, classified wholly Class C. The reason for
    /// each is recorded in engineering/CONFIGURATION_CHANGE_CLASSIFICATION.md; no property within them
    /// could be anything other than cosmetic or operational, so they need no per-key table.
    /// </summary>
    private static readonly HashSet<string> WhollyCosmeticObjectTypes = new(StringComparer.Ordinal)
    {
        ConfigurationSnapshotService.ScheduleObjectType,
        ConfigurationSnapshotService.TrustedCertificateObjectType,
        ConfigurationSnapshotService.ApiKeyObjectType,
        ConfigurationSnapshotService.RoleObjectType,
        ConfigurationSnapshotService.PredefinedSearchObjectType,
        ConfigurationSnapshotService.ConnectorDefinitionObjectType,
        ConfigurationSnapshotService.ExampleDataSetObjectType,
        ConfigurationSnapshotService.ExampleDataTemplateObjectType
    };

    private static readonly Dictionary<string, ConfigurationChangeClass> SyncRuleKeys = new(StringComparer.Ordinal)
    {
        // The rule itself.
        ["synchronisationRule"] = C,
        ["name"] = C,
        ["description"] = C,
        ["direction"] = B,
        ["enabled"] = B,
        ["provisionToConnectedSystem"] = B,
        ["projectToMetaverse"] = B,
        ["outboundDeprovisionAction"] = A,
        ["inboundOutOfScopeAction"] = A,
        ["enforceState"] = B,
        ["connectedSystemId"] = B,
        ["connectedSystemObjectTypeId"] = B,
        ["metaverseObjectTypeId"] = B,

        // Initial Password: changes whether JIM sets a password on the accounts this rule provisions, and what
        // that password looks like. Sync-affecting rather than destructive: it alters what JIM writes to newly
        // created accounts, and destroys nothing that existed before.
        ["initialPassword"] = B,
        ["expiryBehaviour"] = B,
        ["enableAccount"] = B,
        ["style"] = B,
        ["length"] = B,
        ["minimumUppercase"] = B,
        ["minimumLowercase"] = B,
        ["minimumDigits"] = B,
        ["minimumSymbols"] = B,
        ["permittedSymbols"] = B,
        ["wordCount"] = B,
        ["wordSeparator"] = B,
        ["wordCapitalisation"] = B,
        ["appendedDigitCount"] = B,
        ["appendSymbol"] = B,
        ["excludeAmbiguousCharacters"] = B,

        // Attribute Flow: changes what values flow.
        ["attributeFlowRules"] = B,
        ["attributeFlowRule"] = B,
        ["targetMetaverseAttributeId"] = B,
        ["targetConnectedSystemAttributeId"] = B,
        ["inboundValueProcessing"] = B,
        ["caseNormalisation"] = B,
        ["priority"] = B,
        ["nullIsValue"] = B,
        ["initialExportOnly"] = B,

        // Mapping and matching sources: change the computed value.
        ["sources"] = B,
        ["source"] = B,
        ["order"] = B,
        ["metaverseAttributeId"] = B,
        ["connectedSystemAttributeId"] = B,
        ["expression"] = B,

        // Object Matching: changes which objects join.
        ["objectMatchingRules"] = B,
        ["objectMatchingRule"] = B,
        ["caseSensitive"] = B,

        // Scoping: changes which objects the rule applies to. What happens to those that leave scope
        // is governed by inboundOutOfScopeAction, which is Class A.
        ["objectScopingCriteriaGroups"] = B,
        ["group"] = B,
        ["childGroups"] = B,
        ["type"] = B,
        ["position"] = B,
        ["criteria"] = B,
        ["criterion"] = B,
        ["comparisonType"] = B,
        ["stringValue"] = B,
        ["intValue"] = B,
        ["longValue"] = B,
        ["decimalValue"] = B,
        ["dateTimeValue"] = B,
        ["boolValue"] = B,
        ["guidValue"] = B
    };

    private static readonly Dictionary<string, ConfigurationChangeClass> ConnectedSystemKeys = new(StringComparer.Ordinal)
    {
        // The system itself.
        ["connectedSystem"] = C,
        ["name"] = C,
        ["description"] = C,
        ["connectorDefinitionId"] = B,
        ["objectMatchingRuleMode"] = B,
        ["unresolvedReferenceHandling"] = B,
        ["maxExportParallelism"] = C,
        ["settingValues"] = B,

        // Run Profiles.
        ["runProfiles"] = C,
        ["runProfile"] = C,
        ["runType"] = B,
        ["pageSize"] = C,
        ["filePath"] = B,
        ["partitionId"] = B,

        // Connected System schema. Deselecting an Object Type removes its Connected System Objects.
        ["objectTypes"] = B,
        ["objectType"] = B,
        ["selected"] = A,
        ["removeContributedAttributesOnObsoletion"] = B,
        ["attributes"] = B,
        ["attribute"] = B,
        ["type"] = B,
        ["attributePlurality"] = B,
        ["isExternalId"] = B,
        ["isSecondaryExternalId"] = B,
        ["writability"] = B,

        // Partitions and containers. Deselecting a partition removes the objects imported from it;
        // `selected` above covers both, since both carry Class A.
        ["objectMatchingRules"] = B,
        ["objectMatchingRule"] = B,
        ["partitions"] = B,
        ["partition"] = B,
        ["externalId"] = C,
        ["containers"] = C,
        ["container"] = C,
        ["hidden"] = C
    };

    private static readonly Dictionary<string, ConfigurationChangeClass> MetaverseObjectTypeKeys = new(StringComparer.Ordinal)
    {
        ["metaverseObjectType"] = C,
        ["name"] = C,
        ["pluralName"] = C,
        ["builtIn"] = C,
        ["icon"] = C,
        ["deletionRule"] = A,
        ["deletionGracePeriod"] = A,
        ["deletionTriggerConnectedSystemIds"] = A,
        ["attributes"] = B,
        ["attribute"] = B,
        ["attributeId"] = B
    };

    private static readonly Dictionary<string, ConfigurationChangeClass> MetaverseAttributeKeys = new(StringComparer.Ordinal)
    {
        ["metaverseAttribute"] = C,
        ["name"] = C,
        ["type"] = B,
        ["attributePlurality"] = B,
        ["builtIn"] = C,
        ["renderingHint"] = C,
        ["metaverseObjectTypes"] = B,
        ["metaverseObjectType"] = B,
        ["metaverseObjectTypeId"] = B,
        ["standardMappings"] = C,
        ["standardMapping"] = C,
        ["standard"] = C,
        ["counterpartName"] = C,
        ["notes"] = C
    };

    /// <summary>
    /// A Service Setting snapshot's structural nodes are metadata; only the value node carries meaning,
    /// and its significance depends on which setting it is. Classified by setting key in
    /// <see cref="ServiceSettingKeys"/>.
    /// </summary>
    private static readonly Dictionary<string, ConfigurationChangeClass> ServiceSettingNodeKeys = new(StringComparer.Ordinal)
    {
        ["serviceSetting"] = C,
        ["key"] = C,
        ["displayName"] = C,
        ["category"] = C,
        ["valueType"] = C,
        ["defaultValue"] = C,
        ["overridden"] = C
        // "value" is deliberately absent: it is classified by setting key, not by node key.
    };

    /// <summary>
    /// Classification of a Service Setting's value, by setting key. Nearly every setting is
    /// operational; PartitionValidationMode is the exception, because relaxing it lets a Run Profile
    /// whose partition is missing import zero objects, which a full import then reads as everything
    /// having disappeared.
    /// </summary>
    private static readonly Dictionary<string, ConfigurationChangeClass> ServiceSettingKeys = new(StringComparer.Ordinal)
    {
        [Constants.SettingKeys.PartitionValidationMode] = B,
        [Constants.SettingKeys.SyncPageSize] = C,
        [Constants.SettingKeys.VerboseNoChangeRecording] = C,
        [Constants.SettingKeys.MaintenanceMode] = C,
        [Constants.SettingKeys.HistoryRetentionPeriod] = C,
        [Constants.SettingKeys.ConfigurationChangeRetentionPeriod] = C,
        [Constants.SettingKeys.SecurityEventRetentionPeriod] = C,
        [Constants.SettingKeys.HistoryCleanupBatchSize] = C,
        [Constants.SettingKeys.ChangeTrackingCsoChangesEnabled] = C,
        [Constants.SettingKeys.ChangeTrackingMvoChangesEnabled] = C,
        [Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled] = C,
        [Constants.SettingKeys.ChangeTrackingSyncOutcomesLevel] = C,
        [Constants.SettingKeys.SsoAuthority] = C,
        [Constants.SettingKeys.SsoClientId] = C,
        [Constants.SettingKeys.SsoSecret] = C,
        [Constants.SettingKeys.SsoApiScope] = C,
        [Constants.SettingKeys.SsoClaimType] = C,
        [Constants.SettingKeys.SsoMvAttribute] = C,
        [Constants.SettingKeys.SsoUniqueIdentifierClaimType] = C,
        [Constants.SettingKeys.SsoEnableLogOut] = C,
        [Constants.SettingKeys.CredentialEncryptionEnabled] = C,
        [Constants.SettingKeys.EncryptionKeyPath] = C,
        [Constants.SettingKeys.RateLimitingEnabled] = C,
        [Constants.SettingKeys.RateLimitingAuthenticatedRequestsPerMinute] = C,
        [Constants.SettingKeys.RateLimitingUnauthenticatedRequestsPerMinute] = C,
        [Constants.SettingKeys.ProgressUpdateInterval] = C,
        [Constants.SettingKeys.ServiceName] = C,

        // Internal settings, not surfaced for administrators to edit, but classified so the
        // completeness test covers every key Constants.SettingKeys declares.
        [Constants.SettingKeys.ConfigurationChangeHashKey] = C,
        [Constants.SettingKeys.StaleTaskTimeout] = C,
        [Constants.SettingKeys.ServiceId] = C
    };

    /// <summary>
    /// Classifies a configuration change by the highest class among the properties that actually
    /// changed. Returns <see cref="ConfigurationChangeClass.NotClassified"/> when nothing changed.
    /// </summary>
    /// <param name="diff">The diff between the previous and proposed snapshots.</param>
    /// <param name="objectKey">
    /// The Service Setting's key, required only when classifying a Service Setting change; ignored for
    /// every other object type.
    /// </param>
    public static ConfigurationChangeClass Classify(ConfigurationDiff diff, string? objectKey = null)
    {
        ArgumentNullException.ThrowIfNull(diff);

        if (!diff.HasChanges || diff.Root == null)
            return ConfigurationChangeClass.NotClassified;

        // Projected rather than mapped inside the loop, and lazily, so the break below still stops the
        // enumeration at the first Destructive key.
        var highest = ConfigurationChangeClass.NotClassified;
        foreach (var nodeClass in CollectChangedKeys(diff.Root).Select(key => ClassifyKey(diff.ObjectType, key, objectKey)))
        {
            if (nodeClass > highest)
                highest = nodeClass;

            // Destructive is the ceiling; no later key can raise it further.
            if (highest == ConfigurationChangeClass.Destructive)
                break;
        }

        return highest;
    }

    /// <summary>
    /// Classifies a single snapshot node key within an object type. Throws when the key has no explicit
    /// classification, which is what turns an unclassified new property into a test failure rather than
    /// a silent wrong answer.
    /// </summary>
    public static ConfigurationChangeClass ClassifyKey(string objectType, string nodeKey, string? objectKey = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(objectType);
        ArgumentException.ThrowIfNullOrEmpty(nodeKey);

        if (WhollyCosmeticObjectTypes.Contains(objectType))
            return ConfigurationChangeClass.Cosmetic;

        if (objectType == ConfigurationSnapshotService.ServiceSettingObjectType)
            return ClassifyServiceSettingNode(nodeKey, objectKey);

        var table = TableFor(objectType);
        if (table.TryGetValue(nodeKey, out var result))
            return result;

        throw new InvalidOperationException(
            $"Configuration property '{nodeKey}' on '{objectType}' has no classification. Add it to " +
            $"{nameof(ConfigurationChangeClassifier)} and to the matching table in " +
            "engineering/CONFIGURATION_CHANGE_CLASSIFICATION.md. There is no default class by design.");
    }

    /// <summary>
    /// True when the object type is classified wholly Class C, so its individual properties need no
    /// entry. Exposed for the completeness tests.
    /// </summary>
    public static bool IsWhollyCosmetic(string objectType) => WhollyCosmeticObjectTypes.Contains(objectType);

    private static ConfigurationChangeClass ClassifyServiceSettingNode(string nodeKey, string? objectKey)
    {
        if (ServiceSettingNodeKeys.TryGetValue(nodeKey, out var structural))
            return structural;

        if (nodeKey != "value")
        {
            throw new InvalidOperationException(
                $"Service Setting snapshot node '{nodeKey}' has no classification. Add it to " +
                $"{nameof(ConfigurationChangeClassifier)} and to engineering/CONFIGURATION_CHANGE_CLASSIFICATION.md.");
        }

        // The value node's significance is the setting's, not the node's.
        if (string.IsNullOrEmpty(objectKey))
        {
            throw new InvalidOperationException(
                "Classifying a Service Setting value requires the setting key; none was supplied.");
        }

        if (ServiceSettingKeys.TryGetValue(objectKey, out var settingClass))
            return settingClass;

        throw new InvalidOperationException(
            $"Service Setting '{objectKey}' has no classification. Add it to " +
            $"{nameof(ConfigurationChangeClassifier)} and to engineering/CONFIGURATION_CHANGE_CLASSIFICATION.md. " +
            "There is no default class by design.");
    }

    private static Dictionary<string, ConfigurationChangeClass> TableFor(string objectType) => objectType switch
    {
        var t when t == ConfigurationSnapshotService.SyncRuleObjectType => SyncRuleKeys,
        var t when t == ConfigurationSnapshotService.ConnectedSystemObjectType => ConnectedSystemKeys,
        var t when t == ConfigurationSnapshotService.MetaverseObjectTypeObjectType => MetaverseObjectTypeKeys,
        var t when t == ConfigurationSnapshotService.MetaverseAttributeObjectType => MetaverseAttributeKeys,
        _ => throw new InvalidOperationException(
            $"Object type '{objectType}' has no classification table and is not declared wholly cosmetic. " +
            $"Add it to {nameof(ConfigurationChangeClassifier)} and to " +
            "engineering/CONFIGURATION_CHANGE_CLASSIFICATION.md.")
    };

    /// <summary>
    /// Walks the diff tree yielding the key of every node that actually changed. Unchanged branches are
    /// skipped entirely, so a save that touched one field does not drag its siblings into the
    /// classification.
    /// </summary>
    private static IEnumerable<string> CollectChangedKeys(ConfigurationDiffNode node)
    {
        if (node.ChangeType != ConfigurationDiffChangeType.Unchanged)
            yield return node.Key;

        if (node.Children == null)
            yield break;

        foreach (var key in node.Children.SelectMany(CollectChangedKeys))
            yield return key;
    }
}
