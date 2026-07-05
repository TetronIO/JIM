// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Scheduling;
using JIM.Models.Security;
using JIM.Models.Staging;
using JIM.Utilities;

namespace JIM.Application.Services;

/// <summary>
/// Builds scoped, redaction-aware configuration snapshots for configuration objects (Synchronisation Rules and
/// Connected Systems). Each snapshot is a purpose-built, deliberately-scoped projection of the object's configuration,
/// not a naive serialisation of the EF graph: backlinks, large operational collections (Connected System Objects,
/// Pending Exports), and persisted connector data are never included. Secret values are represented by a keyed hash
/// (HMAC-SHA-256), so a credential change can be detected and shown without disclosing the value.
/// </summary>
public class ConfigurationSnapshotService
{
    /// <summary>The object-type discriminator stored on a Synchronisation Rule snapshot.</summary>
    public const string SyncRuleObjectType = "SynchronisationRule";

    /// <summary>The object-type discriminator stored on a Connected System snapshot.</summary>
    public const string ConnectedSystemObjectType = "ConnectedSystem";

    /// <summary>The object-type discriminator stored on a Schedule snapshot.</summary>
    public const string ScheduleObjectType = "Schedule";

    /// <summary>The object-type discriminator stored on a Service Setting snapshot.</summary>
    public const string ServiceSettingObjectType = "ServiceSetting";

    /// <summary>The object-type discriminator stored on a Metaverse Object Type snapshot.</summary>
    public const string MetaverseObjectTypeObjectType = "MetaverseObjectType";

    /// <summary>The object-type discriminator stored on a Metaverse Attribute snapshot.</summary>
    public const string MetaverseAttributeObjectType = "MetaverseAttribute";

    /// <summary>The object-type discriminator stored on a Trusted Certificate snapshot.</summary>
    public const string TrustedCertificateObjectType = "TrustedCertificate";

    /// <summary>The object-type discriminator stored on an API Key snapshot.</summary>
    public const string ApiKeyObjectType = "ApiKey";

    private JimApplication Application { get; }

    private static readonly JsonSerializerOptions SerialiserOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false
    };

    internal ConfigurationSnapshotService(JimApplication application)
    {
        Application = application;
    }

    /// <summary>Serialises a snapshot to its stored JSON (jsonb) representation.</summary>
    public static string Serialise(ConfigurationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return JsonSerializer.Serialize(snapshot, SerialiserOptions);
    }

    /// <summary>Deserialises a stored snapshot document, or null when the input is null/empty.</summary>
    public static ConfigurationSnapshot? Deserialise(string? json)
    {
        return string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<ConfigurationSnapshot>(json, SerialiserOptions);
    }

    // -- Synchronisation Rule ------------------------------------------------------------------------------------------

    /// <summary>
    /// Builds a scoped snapshot of a Synchronisation Rule. <paramref name="hashKey"/> is the per-instance keyed-hash key;
    /// Synchronisation Rules carry no secrets, but the signature is kept uniform with the Connected System overload.
    /// </summary>
    public ConfigurationSnapshot CreateSnapshot(SyncRule rule, byte[] hashKey)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var children = new List<ConfigurationSnapshotNode>();
        Add(children, "name", rule.Name, "Name");
        AddEnum(children, "direction", rule.Direction, "Direction");
        Add(children, "enabled", Render(rule.Enabled), "Enabled");
        Add(children, "provisionToConnectedSystem", Render(rule.ProvisionToConnectedSystem), "Provision to Connected System");
        Add(children, "projectToMetaverse", Render(rule.ProjectToMetaverse), "Project to Metaverse");
        AddEnum(children, "outboundDeprovisionAction", rule.OutboundDeprovisionAction, "Outbound deprovision action");
        AddEnum(children, "inboundOutOfScopeAction", rule.InboundOutOfScopeAction, "Inbound out-of-scope action");
        Add(children, "enforceState", Render(rule.EnforceState), "Enforce state");
        AddReference(children, "connectedSystemId", rule.ConnectedSystemId, rule.ConnectedSystem?.Name, "Connected System");
        AddReference(children, "connectedSystemObjectTypeId", rule.ConnectedSystemObjectTypeId, rule.ConnectedSystemObjectType?.Name, "Connected System Object Type");
        AddReference(children, "metaverseObjectTypeId", rule.MetaverseObjectTypeId, rule.MetaverseObjectType?.Name, "Metaverse Object Type");
        children.Add(BuildAttributeFlowRules(rule.AttributeFlowRules));
        children.Add(BuildObjectMatchingRules(rule.ObjectMatchingRules));
        children.Add(BuildScopingCriteriaGroups("objectScopingCriteriaGroups", "Scope", rule.ObjectScopingCriteriaGroups));

        return new ConfigurationSnapshot
        {
            ObjectType = SyncRuleObjectType,
            ObjectId = rule.Id,
            ObjectName = rule.Name,
            Root = ConfigurationSnapshotNode.ObjectNode("synchronisationRule", children, "Synchronisation Rule")
        };
    }

    private ConfigurationSnapshotNode BuildAttributeFlowRules(List<SyncRuleMapping> mappings)
    {
        var items = new List<ConfigurationSnapshotNode>();
        foreach (var mapping in mappings.OrderBy(m => m.Id))
        {
            var children = new List<ConfigurationSnapshotNode>();
            AddReference(children, "targetMetaverseAttributeId", mapping.TargetMetaverseAttributeId, mapping.TargetMetaverseAttribute?.Name, "Target Metaverse Attribute");
            AddReference(children, "targetConnectedSystemAttributeId", mapping.TargetConnectedSystemAttributeId, mapping.TargetConnectedSystemAttribute?.Name, "Target Connected System Attribute");
            AddEnum(children, "inboundValueProcessing", mapping.InboundValueProcessing, "Inbound value processing");
            AddEnum(children, "caseNormalisation", mapping.CaseNormalisation, "Case normalisation");

            // Priority and "Null is a value" determine which contributor wins a multi-source Metaverse attribute, so
            // they are configuration. int.MaxValue is the "sole contributor / no explicit priority" sentinel, not a
            // real priority, so it is omitted rather than rendered as a meaningless 2147483647.
            if (mapping.Priority != int.MaxValue)
                Add(children, "priority", Render(mapping.Priority), "Priority");
            Add(children, "nullIsValue", Render(mapping.NullIsValue), "Null is a value");

            children.Add(BuildMappingSources(mapping.Sources));
            items.Add(ConfigurationSnapshotNode.ObjectNode("attributeFlowRule", children, "Attribute Flow", mapping.Id));
        }
        return ConfigurationSnapshotNode.CollectionNode("attributeFlowRules", items, "Attribute Flow");
    }

    private ConfigurationSnapshotNode BuildMappingSources(List<SyncRuleMappingSource> sources)
    {
        var items = new List<ConfigurationSnapshotNode>();
        foreach (var source in sources.OrderBy(s => s.Order).ThenBy(s => s.Id))
        {
            var children = new List<ConfigurationSnapshotNode>();
            Add(children, "order", Render(source.Order), "Order");
            AddReference(children, "metaverseAttributeId", source.MetaverseAttributeId, source.MetaverseAttribute?.Name, "Metaverse Attribute");
            AddReference(children, "connectedSystemAttributeId", source.ConnectedSystemAttributeId, source.ConnectedSystemAttribute?.Name, "Connected System Attribute");
            Add(children, "expression", source.Expression, "Expression");
            items.Add(ConfigurationSnapshotNode.ObjectNode("source", children, "Source", source.Id));
        }
        return ConfigurationSnapshotNode.CollectionNode("sources", items, "Sources");
    }

    private ConfigurationSnapshotNode BuildObjectMatchingRules(List<ObjectMatchingRule> rules)
    {
        var items = new List<ConfigurationSnapshotNode>();
        foreach (var rule in rules.OrderBy(r => r.Order).ThenBy(r => r.Id))
        {
            var children = new List<ConfigurationSnapshotNode>();
            Add(children, "order", Render(rule.Order), "Order");
            Add(children, "caseSensitive", Render(rule.CaseSensitive), "Case sensitive");
            AddReference(children, "metaverseObjectTypeId", rule.MetaverseObjectTypeId, rule.MetaverseObjectType?.Name, "Metaverse Object Type");
            AddReference(children, "targetMetaverseAttributeId", rule.TargetMetaverseAttributeId, rule.TargetMetaverseAttribute?.Name, "Target Metaverse Attribute");
            children.Add(BuildObjectMatchingRuleSources(rule.Sources));
            items.Add(ConfigurationSnapshotNode.ObjectNode("objectMatchingRule", children, "Object Matching Rule", rule.Id));
        }
        return ConfigurationSnapshotNode.CollectionNode("objectMatchingRules", items, "Object Matching Rules");
    }

    private ConfigurationSnapshotNode BuildObjectMatchingRuleSources(List<ObjectMatchingRuleSource> sources)
    {
        var items = new List<ConfigurationSnapshotNode>();
        foreach (var source in sources.OrderBy(s => s.Order).ThenBy(s => s.Id))
        {
            var children = new List<ConfigurationSnapshotNode>();
            Add(children, "order", Render(source.Order), "Order");
            AddReference(children, "connectedSystemAttributeId", source.ConnectedSystemAttributeId, source.ConnectedSystemAttribute?.Name, "Connected System Attribute");
            AddReference(children, "metaverseAttributeId", source.MetaverseAttributeId, source.MetaverseAttribute?.Name, "Metaverse Attribute");
            Add(children, "expression", source.Expression, "Expression");
            items.Add(ConfigurationSnapshotNode.ObjectNode("source", children, "Source", source.Id));
        }
        return ConfigurationSnapshotNode.CollectionNode("sources", items, "Sources");
    }

    private ConfigurationSnapshotNode BuildScopingCriteriaGroups(string key, string label, List<SyncRuleScopingCriteriaGroup> groups)
    {
        var items = new List<ConfigurationSnapshotNode>();
        foreach (var group in groups.OrderBy(g => g.Position).ThenBy(g => g.Id))
        {
            var children = new List<ConfigurationSnapshotNode>();
            AddEnum(children, "type", group.Type, "Match");
            Add(children, "position", Render(group.Position), "Position");
            children.Add(BuildScopingCriteria(group.Criteria));
            children.Add(BuildScopingCriteriaGroups("childGroups", "Nested groups", group.ChildGroups));
            items.Add(ConfigurationSnapshotNode.ObjectNode("group", children, "Group", group.Id));
        }
        return ConfigurationSnapshotNode.CollectionNode(key, items, label);
    }

    private ConfigurationSnapshotNode BuildScopingCriteria(List<SyncRuleScopingCriteria> criteria)
    {
        var items = new List<ConfigurationSnapshotNode>();
        foreach (var criterion in criteria.OrderBy(c => c.Id))
        {
            var children = new List<ConfigurationSnapshotNode>();
            AddReference(children, "metaverseAttributeId", criterion.MetaverseAttributeId, criterion.MetaverseAttribute?.Name, "Metaverse Attribute");
            AddReference(children, "connectedSystemAttributeId", criterion.ConnectedSystemAttributeId, criterion.ConnectedSystemAttribute?.Name, "Connected System Attribute");
            AddEnum(children, "comparisonType", criterion.ComparisonType, "Comparison");
            Add(children, "stringValue", criterion.StringValue, "Value");
            Add(children, "intValue", Render(criterion.IntValue), "Value");
            Add(children, "longValue", Render(criterion.LongValue), "Value");
            Add(children, "dateTimeValue", Render(criterion.DateTimeValue), "Value");
            Add(children, "boolValue", Render(criterion.BoolValue), "Value");
            Add(children, "guidValue", Render(criterion.GuidValue), "Value");
            Add(children, "caseSensitive", Render(criterion.CaseSensitive), "Case sensitive");
            items.Add(ConfigurationSnapshotNode.ObjectNode("criterion", children, "Criterion", criterion.Id));
        }
        return ConfigurationSnapshotNode.CollectionNode("criteria", items, "Criteria");
    }

    // -- Connected System ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Builds a scoped, redacted snapshot of a Connected System. Encrypted setting values are never stored; a keyed hash
    /// (using <paramref name="hashKey"/>) is recorded instead. The high-volume operational collections (Objects,
    /// Pending Exports) and PersistedConnectorData are excluded.
    /// </summary>
    public ConfigurationSnapshot CreateSnapshot(ConnectedSystem connectedSystem, byte[] hashKey)
    {
        ArgumentNullException.ThrowIfNull(connectedSystem);

        var children = new List<ConfigurationSnapshotNode>();
        Add(children, "name", connectedSystem.Name, "Name");
        Add(children, "description", connectedSystem.Description, "Description");
        // Status (Active/Deleting) is deliberately excluded: it is runtime state, not configuration, so it does not
        // belong in a configuration change history (it would record phantom changes around deletion attempts).
        AddReference(children, "connectorDefinitionId", connectedSystem.ConnectorDefinitionId, connectedSystem.ConnectorDefinition?.Name, "Connector");
        AddEnum(children, "objectMatchingRuleMode", connectedSystem.ObjectMatchingRuleMode, "Object matching rule mode");
        // SettingValuesValid is deliberately excluded: it is internal UI-flow state (whether the connector has validated
        // the settings), not configuration, so it does not belong in a configuration change history.
        Add(children, "maxExportParallelism", Render(connectedSystem.MaxExportParallelism), "Max export parallelism");
        children.Add(BuildSettingValues(connectedSystem.SettingValues, hashKey));
        children.Add(BuildRunProfiles(connectedSystem.RunProfiles));
        children.Add(BuildObjectTypes(connectedSystem.ObjectTypes));
        children.Add(BuildPartitions(connectedSystem.Partitions));

        return new ConfigurationSnapshot
        {
            ObjectType = ConnectedSystemObjectType,
            ObjectId = connectedSystem.Id,
            ObjectName = connectedSystem.Name,
            Root = ConfigurationSnapshotNode.ObjectNode("connectedSystem", children, "Connected System")
        };
    }

    private ConfigurationSnapshotNode BuildSettingValues(List<ConnectedSystemSettingValue> settingValues, byte[] hashKey)
    {
        // Unset settings (BuildSettingValueNode returns null) are skipped, so the snapshot records only configured
        // settings and does not litter creation history with empty "+ File Path:" lines.
        var items = settingValues
            .OrderBy(sv => sv.Setting?.Id ?? sv.Id)
            .Select(sv => BuildSettingValueNode(sv, hashKey))
            .Where(node => node != null)
            .Select(node => node!)
            .ToList();
        return ConfigurationSnapshotNode.CollectionNode("settingValues", items, "Settings");
    }

    private ConfigurationSnapshotNode? BuildSettingValueNode(ConnectedSystemSettingValue settingValue, byte[] hashKey)
    {
        var label = settingValue.Setting?.Name ?? $"Setting {settingValue.Id}";
        var nodeKey = !string.IsNullOrEmpty(settingValue.Setting?.Name) ? settingValue.Setting!.Name! : $"setting-{settingValue.Id}";
        var itemId = settingValue.Setting?.Id ?? settingValue.Id;

        // Secret detection is robust: any populated encrypted value, or a setting declared as StringEncrypted, is
        // redacted. StringEncryptedValue is only ever populated for encrypted settings, so a secret is never leaked even
        // when the Setting navigation is not loaded.
        var isSecret = !string.IsNullOrEmpty(settingValue.StringEncryptedValue) ||
                       settingValue.Setting?.Type == ConnectedSystemSettingType.StringEncrypted;
        if (isSecret)
        {
            // An unset secret (no encrypted value) is not configuration; skip it rather than record an empty hash.
            if (string.IsNullOrEmpty(settingValue.StringEncryptedValue))
                return null;

            var node = ConfigurationSnapshotNode.Secret(nodeKey, ComputeSecretHash(settingValue.StringEncryptedValue, hashKey), label);
            node.ItemId = itemId;
            return node;
        }

        string? value = settingValue.StringValue;
        if (string.IsNullOrEmpty(value) && settingValue.IntValue.HasValue)
            value = Render(settingValue.IntValue.Value);
        if (string.IsNullOrEmpty(value) && settingValue.Setting?.Type == ConnectedSystemSettingType.CheckBox)
            value = Render(settingValue.CheckboxValue);

        // An unset setting is not configuration; skip it, matching how the top-level scalar Add() helper skips empties.
        if (string.IsNullOrEmpty(value))
            return null;

        var scalar = ConfigurationSnapshotNode.Scalar(nodeKey, value, label);
        scalar.ItemId = itemId;
        return scalar;
    }

    private string ComputeSecretHash(string? encryptedValue, byte[] hashKey)
    {
        if (string.IsNullOrEmpty(encryptedValue))
            return string.Empty;

        // Decrypt transiently so the keyed hash is deterministic across versions; the plaintext is discarded
        // immediately and never stored. If credential protection is unavailable we cannot produce a meaningful keyed
        // hash, so an empty marker is recorded rather than risk storing ciphertext.
        var plaintext = Application.CredentialProtection?.Unprotect(encryptedValue);
        if (string.IsNullOrEmpty(plaintext))
            return string.Empty;

        return ComputePlaintextHash(plaintext, hashKey);
    }

    // Keyed hash (HMAC-SHA-256) of an already-plaintext secret. Used for values that are stored in plaintext (e.g. a
    // Schedule step's SQL connection string) rather than encrypted; the deterministic keyed hash lets a change be
    // detected across versions without ever storing the value.
    private static string ComputePlaintextHash(string plaintext, byte[] hashKey)
    {
        using var hmac = new HMACSHA256(hashKey);
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(plaintext)));
    }

    private ConfigurationSnapshotNode BuildRunProfiles(List<ConnectedSystemRunProfile>? runProfiles)
    {
        var items = new List<ConfigurationSnapshotNode>();
        foreach (var runProfile in (runProfiles ?? []).OrderBy(rp => rp.Id))
        {
            var children = new List<ConfigurationSnapshotNode>();
            Add(children, "name", runProfile.Name, "Name");
            AddEnum(children, "runType", runProfile.RunType, "Run type");
            Add(children, "pageSize", Render(runProfile.PageSize), "Page size");
            Add(children, "filePath", runProfile.FilePath, "File path");
            AddReference(children, "partitionId", runProfile.Partition?.Id, runProfile.Partition?.Name, "Partition");
            items.Add(ConfigurationSnapshotNode.ObjectNode("runProfile", children, "Run Profile", runProfile.Id));
        }
        return ConfigurationSnapshotNode.CollectionNode("runProfiles", items, "Run Profiles");
    }

    private ConfigurationSnapshotNode BuildObjectTypes(List<ConnectedSystemObjectType>? objectTypes)
    {
        var items = new List<ConfigurationSnapshotNode>();
        foreach (var objectType in (objectTypes ?? []).OrderBy(ot => ot.Id))
        {
            var children = new List<ConfigurationSnapshotNode>();
            Add(children, "name", objectType.Name, "Name");
            Add(children, "selected", Render(objectType.Selected), "Selected");
            Add(children, "removeContributedAttributesOnObsoletion", Render(objectType.RemoveContributedAttributesOnObsoletion), "Remove contributed attributes on obsoletion");
            children.Add(BuildObjectTypeAttributes(objectType.Attributes));
            // Simple Mode Object Matching Rules attach to the object type; they are the system's matching
            // configuration, so they belong in its snapshot (Advanced Mode rules live on the Synchronisation Rule).
            children.Add(BuildObjectMatchingRules(objectType.ObjectMatchingRules));
            items.Add(ConfigurationSnapshotNode.ObjectNode("objectType", children, "Object Type", objectType.Id));
        }
        return ConfigurationSnapshotNode.CollectionNode("objectTypes", items, "Object Types");
    }

    private ConfigurationSnapshotNode BuildObjectTypeAttributes(List<ConnectedSystemObjectTypeAttribute> attributes)
    {
        // Capture only selected attributes (the admin's configuration); unselected attributes are discovered schema.
        // Selecting/deselecting therefore reads naturally as an addition/removal in the diff.
        var items = new List<ConfigurationSnapshotNode>();
        foreach (var attribute in attributes.Where(a => a.Selected).OrderBy(a => a.Id))
        {
            var children = new List<ConfigurationSnapshotNode>();
            Add(children, "name", attribute.Name, "Name");
            AddEnum(children, "type", attribute.Type, "Type");
            AddEnum(children, "attributePlurality", attribute.AttributePlurality, "Plurality");
            Add(children, "isExternalId", Render(attribute.IsExternalId), "External ID");
            Add(children, "isSecondaryExternalId", Render(attribute.IsSecondaryExternalId), "Secondary external ID");
            AddEnum(children, "writability", attribute.Writability, "Writability");
            items.Add(ConfigurationSnapshotNode.ObjectNode("attribute", children, attribute.Name, attribute.Id));
        }
        return ConfigurationSnapshotNode.CollectionNode("attributes", items, "Attributes");
    }

    private ConfigurationSnapshotNode BuildPartitions(List<ConnectedSystemPartition>? partitions)
    {
        var items = new List<ConfigurationSnapshotNode>();
        foreach (var partition in (partitions ?? []).OrderBy(p => p.Id))
        {
            var children = new List<ConfigurationSnapshotNode>();
            Add(children, "name", partition.Name, "Name");
            Add(children, "externalId", partition.ExternalId, "External ID");
            Add(children, "selected", Render(partition.Selected), "Selected");
            children.Add(BuildContainers(partition.Containers));
            items.Add(ConfigurationSnapshotNode.ObjectNode("partition", children, "Partition", partition.Id));
        }
        return ConfigurationSnapshotNode.CollectionNode("partitions", items, "Partitions");
    }

    private ConfigurationSnapshotNode BuildContainers(IEnumerable<ConnectedSystemContainer>? containers)
    {
        // Capture only selected containers (the admin's topology selection); the full discovered tree is operational.
        var items = new List<ConfigurationSnapshotNode>();
        foreach (var container in (containers ?? []).Where(c => c.Selected).OrderBy(c => c.Id))
        {
            var children = new List<ConfigurationSnapshotNode>();
            Add(children, "name", container.Name, "Name");
            Add(children, "externalId", container.ExternalId, "External ID");
            Add(children, "hidden", Render(container.Hidden), "Hidden");
            children.Add(BuildContainers(container.ChildContainers));
            items.Add(ConfigurationSnapshotNode.ObjectNode("container", children, container.Name, container.Id));
        }
        return ConfigurationSnapshotNode.CollectionNode("containers", items, "Containers");
    }

    // -- Schedule ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Builds a scoped, redacted snapshot of a Schedule (a Guid-keyed configuration object). Only configuration is
    /// captured: runtime and audit state (NextRunTime, LastRunTime, Created, LastUpdated and every initiator/CreatedBy/
    /// LastUpdatedBy field) is excluded. A step's SQL connection string can contain a secret, so it is redacted to a
    /// keyed hash (using <paramref name="hashKey"/>) exactly like a Connected System setting value; its value is never
    /// stored. Steps are captured as a collection keyed by their Guid id (their StepIndex is not unique).
    /// </summary>
    public ConfigurationSnapshot CreateSnapshot(Schedule schedule, byte[] hashKey)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        var children = new List<ConfigurationSnapshotNode>();
        Add(children, "name", schedule.Name, "Name");
        Add(children, "description", schedule.Description, "Description");
        Add(children, "enabled", Render(schedule.IsEnabled), "Enabled");
        Add(children, "builtIn", Render(schedule.BuiltIn), "Built-in");
        AddEnum(children, "triggerType", schedule.TriggerType, "Trigger type");
        AddEnum(children, "patternType", schedule.PatternType, "Pattern type");
        Add(children, "intervalValue", Render(schedule.IntervalValue), "Interval value");
        AddEnum(children, "intervalUnit", schedule.IntervalUnit, "Interval unit");
        Add(children, "intervalWindowStart", schedule.IntervalWindowStart, "Interval window start");
        Add(children, "intervalWindowEnd", schedule.IntervalWindowEnd, "Interval window end");
        Add(children, "daysOfWeek", schedule.DaysOfWeek, "Days of week");
        Add(children, "runTimes", schedule.RunTimes, "Run times");
        Add(children, "cronExpression", schedule.CronExpression, "Cron expression");
        children.Add(BuildScheduleSteps(schedule.Steps, hashKey));

        return new ConfigurationSnapshot
        {
            ObjectType = ScheduleObjectType,
            ObjectGuidId = schedule.Id,
            ObjectName = schedule.Name,
            Root = ConfigurationSnapshotNode.ObjectNode("schedule", children, "Schedule")
        };
    }

    private ConfigurationSnapshotNode BuildScheduleSteps(List<ScheduleStep> steps, byte[] hashKey)
    {
        var items = new List<ConfigurationSnapshotNode>();
        foreach (var step in steps.OrderBy(s => s.StepIndex).ThenBy(s => s.Id))
        {
            var children = new List<ConfigurationSnapshotNode>();
            Add(children, "stepIndex", Render(step.StepIndex), "Step index");
            Add(children, "name", step.Name, "Name");
            AddEnum(children, "stepType", step.StepType, "Step type");
            AddEnum(children, "executionMode", step.ExecutionMode, "Execution mode");
            Add(children, "continueOnFailure", Render(step.ContinueOnFailure), "Continue on failure");
            Add(children, "timeout", Render(step.Timeout), "Timeout");
            AddReference(children, "connectedSystemId", step.ConnectedSystemId, null, "Connected System");
            AddReference(children, "runProfileId", step.RunProfileId, null, "Run Profile");
            Add(children, "scriptPath", step.ScriptPath, "Script path");
            Add(children, "arguments", step.Arguments, "Arguments");
            Add(children, "executablePath", step.ExecutablePath, "Executable path");
            Add(children, "workingDirectory", step.WorkingDirectory, "Working directory");
            Add(children, "sqlScriptPath", step.SqlScriptPath, "SQL script path");
            // A SQL connection string can carry a credential; redact it to a keyed hash so a change is detectable but the
            // value is never stored. Unlike a Connected System setting it is persisted as plaintext, so the hash is taken
            // over the plaintext directly rather than after a decrypt. An empty value is not configuration; skip it.
            if (!string.IsNullOrEmpty(step.SqlConnectionString))
                children.Add(ConfigurationSnapshotNode.Secret("sqlConnectionString", ComputePlaintextHash(step.SqlConnectionString, hashKey), "SQL connection string"));

            var label = !string.IsNullOrEmpty(step.Name) ? step.Name : $"Step {step.StepIndex}";
            items.Add(ConfigurationSnapshotNode.ObjectNode("step", children, label, step.Id));
        }
        return ConfigurationSnapshotNode.CollectionNode("steps", items, "Steps");
    }

    // -- Service Setting -----------------------------------------------------------------------------------------------

    /// <summary>
    /// Builds a scoped snapshot of a Service Setting: its identity and typing metadata, the current (override) value,
    /// the default value, and the override-vs-default flag, so a revert diffs as the override being removed. For
    /// <see cref="ServiceSettingValueType.StringEncrypted"/> settings the value and default are never stored in any
    /// recoverable form; each is represented by a keyed hash so a rotation is detectable without disclosure.
    /// </summary>
    public ConfigurationSnapshot CreateSnapshot(ServiceSetting setting, byte[] hashKey)
    {
        ArgumentNullException.ThrowIfNull(setting);

        var children = new List<ConfigurationSnapshotNode>();
        Add(children, "key", setting.Key, "Key");
        Add(children, "displayName", setting.DisplayName, "Name");
        AddEnum(children, "category", setting.Category, "Category");
        AddEnum(children, "valueType", setting.ValueType, "Value type");

        if (setting.ValueType == ServiceSettingValueType.StringEncrypted)
        {
            // An unset secret is not configuration; skip it rather than record an empty hash, matching the
            // Connected System setting-value treatment.
            if (!string.IsNullOrEmpty(setting.Value))
                children.Add(ConfigurationSnapshotNode.Secret("value", ComputeSecretHash(setting.Value, hashKey), "Value"));
            if (!string.IsNullOrEmpty(setting.DefaultValue))
                children.Add(ConfigurationSnapshotNode.Secret("defaultValue", ComputeSecretHash(setting.DefaultValue, hashKey), "Default value"));
        }
        else
        {
            Add(children, "value", setting.Value, "Value");
            Add(children, "defaultValue", setting.DefaultValue, "Default value");
        }

        Add(children, "overridden", Render(setting.IsOverridden), "Overridden");

        return new ConfigurationSnapshot
        {
            ObjectType = ServiceSettingObjectType,
            ObjectKey = setting.Key,
            ObjectName = setting.DisplayName,
            Root = ConfigurationSnapshotNode.ObjectNode("serviceSetting", children, "Service Setting")
        };
    }

    // -- Metaverse Object Type -----------------------------------------------------------------------------------------

    /// <summary>
    /// Builds a scoped snapshot of a Metaverse Object Type: its identity, its deletion-rule configuration (rule, grace
    /// period and trigger Connected Systems) and its attribute associations. Metaverse Object Types carry no secrets;
    /// <paramref name="hashKey"/> keeps the signature uniform with the other builders. Load the object type with its
    /// attributes so the association list reflects persisted truth.
    /// </summary>
    public ConfigurationSnapshot CreateSnapshot(MetaverseObjectType objectType, byte[] hashKey)
    {
        ArgumentNullException.ThrowIfNull(objectType);

        var children = new List<ConfigurationSnapshotNode>();
        Add(children, "name", objectType.Name, "Name");
        Add(children, "pluralName", objectType.PluralName, "Plural name");
        Add(children, "builtIn", Render(objectType.BuiltIn), "Built-in");
        Add(children, "icon", objectType.Icon, "Icon");
        AddEnum(children, "deletionRule", objectType.DeletionRule, "Deletion rule");
        Add(children, "deletionGracePeriod", Render(objectType.DeletionGracePeriod), "Deletion grace period");
        children.Add(BuildDeletionTriggerSystems(objectType.DeletionTriggerConnectedSystemIds));
        children.Add(BuildAttributeAssociations(objectType.Attributes));

        return new ConfigurationSnapshot
        {
            ObjectType = MetaverseObjectTypeObjectType,
            ObjectId = objectType.Id,
            ObjectName = objectType.Name,
            Root = ConfigurationSnapshotNode.ObjectNode("metaverseObjectType", children, "Metaverse Object Type")
        };
    }

    private static ConfigurationSnapshotNode BuildDeletionTriggerSystems(List<int>? connectedSystemIds)
    {
        // Only the ids are held on the entity; they are recorded as the stable diffable value (matching AddReference's
        // id-first treatment) so a re-point is always detected.
        var items = new List<ConfigurationSnapshotNode>();
        foreach (var id in (connectedSystemIds ?? []).OrderBy(id => id))
        {
            var node = ConfigurationSnapshotNode.Scalar("connectedSystemId", Render(id), "Connected System");
            node.ItemId = id;
            items.Add(node);
        }
        return ConfigurationSnapshotNode.CollectionNode("deletionTriggerConnectedSystemIds", items, "Deletion trigger Connected Systems");
    }

    private static ConfigurationSnapshotNode BuildAttributeAssociations(List<MetaverseAttribute>? attributes)
    {
        // Associations are captured as references (stable id value, name as the display form): binding or unbinding an
        // attribute is this object type's configuration change; the attribute's own definition has its own history.
        var items = new List<ConfigurationSnapshotNode>();
        foreach (var attribute in (attributes ?? []).OrderBy(a => a.Id))
        {
            var node = ConfigurationSnapshotNode.Scalar("attributeId", Render(attribute.Id), "Attribute", attribute.Name);
            node.ItemId = attribute.Id;
            items.Add(node);
        }
        return ConfigurationSnapshotNode.CollectionNode("attributes", items, "Attributes");
    }

    // -- Metaverse Attribute -------------------------------------------------------------------------------------------

    /// <summary>
    /// Builds a scoped snapshot of a Metaverse Attribute: its definition (data type, plurality, rendering hint) and its
    /// Metaverse Object Type associations. Metaverse Attributes carry no secrets; <paramref name="hashKey"/> keeps the
    /// signature uniform with the other builders. Load the attribute with its object types so the association list
    /// reflects persisted truth.
    /// </summary>
    public ConfigurationSnapshot CreateSnapshot(MetaverseAttribute attribute, byte[] hashKey)
    {
        ArgumentNullException.ThrowIfNull(attribute);

        var children = new List<ConfigurationSnapshotNode>();
        Add(children, "name", attribute.Name, "Name");
        AddEnum(children, "type", attribute.Type, "Type");
        AddEnum(children, "attributePlurality", attribute.AttributePlurality, "Plurality");
        Add(children, "builtIn", Render(attribute.BuiltIn), "Built-in");
        AddEnum(children, "renderingHint", attribute.RenderingHint, "Rendering hint");
        children.Add(BuildObjectTypeAssociations(attribute.MetaverseObjectTypes));

        return new ConfigurationSnapshot
        {
            ObjectType = MetaverseAttributeObjectType,
            ObjectId = attribute.Id,
            ObjectName = attribute.Name,
            Root = ConfigurationSnapshotNode.ObjectNode("metaverseAttribute", children, "Metaverse Attribute")
        };
    }

    private static ConfigurationSnapshotNode BuildObjectTypeAssociations(List<MetaverseObjectType>? objectTypes)
    {
        var items = new List<ConfigurationSnapshotNode>();
        foreach (var objectType in (objectTypes ?? []).OrderBy(ot => ot.Id))
        {
            var node = ConfigurationSnapshotNode.Scalar("metaverseObjectTypeId", Render(objectType.Id), "Metaverse Object Type", objectType.Name);
            node.ItemId = objectType.Id;
            items.Add(node);
        }
        return ConfigurationSnapshotNode.CollectionNode("metaverseObjectTypes", items, "Metaverse Object Types");
    }

    // -- Trusted Certificate -------------------------------------------------------------------------------------------

    /// <summary>
    /// Builds a metadata-only snapshot of a Trusted Certificate (a Guid-keyed configuration object): its name, the
    /// public X.509 identity fields (thumbprint, subject, issuer, serial number, validity window), its source, enabled
    /// state and notes. The raw certificate material (DER/PEM bytes) is never captured in any form; the thumbprint
    /// already identifies the exact certificate, so a swap is always detectable from metadata alone. Trusted
    /// Certificates carry no secrets; <paramref name="hashKey"/> keeps the signature uniform with the other builders.
    /// </summary>
    public ConfigurationSnapshot CreateSnapshot(TrustedCertificate certificate, byte[] hashKey)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        var children = new List<ConfigurationSnapshotNode>();
        Add(children, "name", certificate.Name, "Name");
        Add(children, "thumbprint", certificate.Thumbprint, "Thumbprint");
        Add(children, "subject", certificate.Subject, "Subject");
        Add(children, "issuer", certificate.Issuer, "Issuer");
        Add(children, "serialNumber", certificate.SerialNumber, "Serial number");
        Add(children, "validFrom", Render((DateTime?)certificate.ValidFrom), "Valid from");
        Add(children, "validTo", Render((DateTime?)certificate.ValidTo), "Valid to");
        AddEnum(children, "sourceType", certificate.SourceType, "Source");
        Add(children, "filePath", certificate.FilePath, "File path");
        Add(children, "enabled", Render(certificate.IsEnabled), "Enabled");
        Add(children, "notes", certificate.Notes, "Notes");

        return new ConfigurationSnapshot
        {
            ObjectType = TrustedCertificateObjectType,
            ObjectGuidId = certificate.Id,
            ObjectName = certificate.Name,
            Root = ConfigurationSnapshotNode.ObjectNode("trustedCertificate", children, "Trusted Certificate")
        };
    }

    // -- API Key ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Builds a metadata-only snapshot of an API Key (a Guid-keyed configuration object): its name, description, key
    /// prefix, expiry, enabled state, whether it is an infrastructure key, and its Role assignments. The key's secret
    /// material (<see cref="ApiKey.KeyHash"/>) is never captured in any form, not even a keyed hash: unlike a
    /// Connected System credential there is no legitimate "did it change" question for it, since a key is deleted and
    /// replaced rather than edited in place, so recording even a hash would serve no purpose beyond risk.
    /// <see cref="ApiKey.LastUsedAt"/> and <see cref="ApiKey.LastUsedFromIp"/> are operational state, not
    /// configuration, and are excluded so authentication traffic does not churn the semantic dedupe. API Keys carry
    /// no other secrets; <paramref name="hashKey"/> keeps the signature uniform with the other builders.
    /// </summary>
    public ConfigurationSnapshot CreateSnapshot(ApiKey apiKey, byte[] hashKey)
    {
        ArgumentNullException.ThrowIfNull(apiKey);

        var children = new List<ConfigurationSnapshotNode>();
        Add(children, "name", apiKey.Name, "Name");
        Add(children, "description", apiKey.Description, "Description");
        Add(children, "keyPrefix", apiKey.KeyPrefix, "Key prefix");
        Add(children, "expiresAt", Render(apiKey.ExpiresAt), "Expires");
        Add(children, "enabled", Render(apiKey.IsEnabled), "Enabled");
        Add(children, "infrastructureKey", Render(apiKey.IsInfrastructureKey), "Infrastructure key");
        children.Add(BuildRoleAssociations(apiKey.Roles));

        return new ConfigurationSnapshot
        {
            ObjectType = ApiKeyObjectType,
            ObjectGuidId = apiKey.Id,
            ObjectName = apiKey.Name,
            Root = ConfigurationSnapshotNode.ObjectNode("apiKey", children, "API Key")
        };
    }

    private static ConfigurationSnapshotNode BuildRoleAssociations(List<Role>? roles)
    {
        // Associations are captured as references (stable id value, name as the display form), mirroring
        // BuildAttributeAssociations: binding or unbinding a Role to this key is this key's configuration change;
        // renaming the Role itself is that Role's own configuration history, so it must not register as a change
        // here.
        var items = new List<ConfigurationSnapshotNode>();
        foreach (var role in (roles ?? []).OrderBy(r => r.Id))
        {
            var node = ConfigurationSnapshotNode.Scalar("roleId", Render(role.Id), "Role", role.Name);
            node.ItemId = role.Id;
            items.Add(node);
        }
        return ConfigurationSnapshotNode.CollectionNode("roles", items, "Roles");
    }

    // -- value rendering -----------------------------------------------------------------------------------------------

    private static void Add(List<ConfigurationSnapshotNode> nodes, string key, string? value, string label)
    {
        if (!string.IsNullOrEmpty(value))
            nodes.Add(ConfigurationSnapshotNode.Scalar(key, value, label));
    }

    // Records an enum: the raw enum name is stored for stable diffing, with a spaced, human-friendly display form
    // (e.g. "TreatWhitespaceAsNoValue" -> "Treat Whitespace As No Value").
    private static void AddEnum<TEnum>(List<ConfigurationSnapshotNode> nodes, string key, TEnum value, string label) where TEnum : struct, Enum
    {
        var raw = value.ToString();
        nodes.Add(ConfigurationSnapshotNode.Scalar(key, raw, label, raw.SplitOnCapitalLetters()));
    }

    // Records a nullable enum, skipping when unset (matching Add()'s skip-empty behaviour).
    private static void AddEnum<TEnum>(List<ConfigurationSnapshotNode> nodes, string key, TEnum? value, string label) where TEnum : struct, Enum
    {
        if (value.HasValue)
            AddEnum(nodes, key, value.Value, label);
    }

    // Records a foreign-key reference: the raw id is stored for stable diffing (so a re-point to a different entity is
    // detected even when the two share a name), with the resolved name as the human-friendly display value when the
    // referenced entity is available on the loaded graph (otherwise the id is shown). A null id records nothing,
    // matching Add()'s skip-empty behaviour.
    private static void AddReference(List<ConfigurationSnapshotNode> nodes, string key, int? id, string? name, string label)
    {
        if (!id.HasValue)
            return;

        var raw = id.Value.ToString(CultureInfo.InvariantCulture);
        var display = string.IsNullOrEmpty(name) ? null : name;
        nodes.Add(ConfigurationSnapshotNode.Scalar(key, raw, label, display));
    }

    private static string Render(bool value) => value ? "true" : "false";

    private static string? Render(bool? value) => value.HasValue ? Render(value.Value) : null;

    private static string Render(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string? Render(int? value) => value?.ToString(CultureInfo.InvariantCulture);

    private static string? Render(long? value) => value?.ToString(CultureInfo.InvariantCulture);

    private static string? Render(DateTime? value) => value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string? Render(Guid? value) => value?.ToString("D");

    private static string? Render(TimeSpan? value) => value?.ToString("c", CultureInfo.InvariantCulture);

    private static string Render<TEnum>(TEnum value) where TEnum : struct, Enum => value.ToString();
}
