// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.ExampleData;
using JIM.Models.Logic;
using JIM.Models.Scheduling;
using JIM.Models.Search;
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

    /// <summary>The object-type discriminator stored on a Role snapshot.</summary>
    public const string RoleObjectType = "Role";

    /// <summary>The object-type discriminator stored on a Predefined Search snapshot.</summary>
    public const string PredefinedSearchObjectType = "PredefinedSearch";

    /// <summary>The object-type discriminator stored on a Connector Definition snapshot.</summary>
    public const string ConnectorDefinitionObjectType = "ConnectorDefinition";

    /// <summary>The object-type discriminator stored on an Example Data Set snapshot.</summary>
    public const string ExampleDataSetObjectType = "ExampleDataSet";

    /// <summary>The object-type discriminator stored on an Example Data Template snapshot.</summary>
    public const string ExampleDataTemplateObjectType = "ExampleDataTemplate";

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
        Add(children, "description", rule.Description, "Description");
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

    // -- Role ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Builds a scoped snapshot of a Role: its identity, built-in flag, and static membership. Roles carry no secrets;
    /// <paramref name="hashKey"/> keeps the signature uniform with the other builders. Load the Role with its static
    /// members (see <c>ISecurityRepository.GetRoleByIdAsync</c>) so the membership list reflects persisted truth.
    /// </summary>
    public ConfigurationSnapshot CreateSnapshot(Role role, byte[] hashKey)
    {
        ArgumentNullException.ThrowIfNull(role);

        var children = new List<ConfigurationSnapshotNode>();
        Add(children, "name", role.Name, "Name");
        Add(children, "builtIn", Render(role.BuiltIn), "Built-in");
        children.Add(BuildRoleMembers(role.StaticMembers));

        return new ConfigurationSnapshot
        {
            ObjectType = RoleObjectType,
            ObjectId = role.Id,
            ObjectName = role.Name,
            Root = ConfigurationSnapshotNode.ObjectNode("role", children, "Role")
        };
    }

    private static ConfigurationSnapshotNode BuildRoleMembers(List<MetaverseObject>? members)
    {
        // A Role's static members are Guid-keyed Metaverse Objects, so each item uses ItemGuidId (not ItemId) as its
        // stable identity, matching the Schedule Step precedent for Guid-keyed collection items. The member's display
        // name is recorded as the human-friendly display value; renaming the member itself is that Metaverse Object's
        // own change, not this Role's, so only membership (who is present) is captured here.
        var items = new List<ConfigurationSnapshotNode>();
        foreach (var member in (members ?? []).OrderBy(m => m.Id))
        {
            var node = ConfigurationSnapshotNode.Scalar("memberId", member.Id.ToString("D"), "Member", member.DisplayName);
            node.ItemGuidId = member.Id;
            items.Add(node);
        }
        return ConfigurationSnapshotNode.CollectionNode("members", items, "Members");
    }

    // -- Predefined Search -----------------------------------------------------------------------------------------------

    /// <summary>
    /// Builds a scoped snapshot of a Predefined Search: its identity, targeting, result-column attribute selections
    /// and its full criteria graph (nested groups and their criteria). Predefined Searches carry no secrets;
    /// <paramref name="hashKey"/> keeps the signature uniform with the other builders. Load the search with its
    /// attributes and full criteria graph (see <c>ISearchRepository.GetPredefinedSearchAsync(int)</c>) so the
    /// snapshot reflects persisted truth.
    /// </summary>
    public ConfigurationSnapshot CreateSnapshot(PredefinedSearch search, byte[] hashKey)
    {
        ArgumentNullException.ThrowIfNull(search);

        var children = new List<ConfigurationSnapshotNode>();
        Add(children, "name", search.Name, "Name");
        Add(children, "uri", search.Uri, "URI");
        Add(children, "builtIn", Render(search.BuiltIn), "Built-in");
        Add(children, "enabled", Render(search.IsEnabled), "Enabled");
        Add(children, "isDefaultForMetaverseObjectType", Render(search.IsDefaultForMetaverseObjectType), "Default for Metaverse Object Type");
        AddReference(children, "metaverseObjectTypeId", search.MetaverseObjectType?.Id, search.MetaverseObjectType?.Name, "Metaverse Object Type");
        children.Add(BuildPredefinedSearchResultAttributes(search.Attributes));
        children.Add(BuildPredefinedSearchCriteriaGroups(search.CriteriaGroups));

        return new ConfigurationSnapshot
        {
            ObjectType = PredefinedSearchObjectType,
            ObjectId = search.Id,
            ObjectName = search.Name,
            Root = ConfigurationSnapshotNode.ObjectNode("predefinedSearch", children, "Predefined Search")
        };
    }

    private static ConfigurationSnapshotNode BuildPredefinedSearchResultAttributes(List<PredefinedSearchAttribute> attributes)
    {
        // Mirrors BuildAttributeAssociations: a reference (stable id, name as display) per selected result column.
        var items = new List<ConfigurationSnapshotNode>();
        foreach (var attribute in attributes.OrderBy(a => a.Position).ThenBy(a => a.Id))
        {
            var node = ConfigurationSnapshotNode.Scalar("attributeId", Render(attribute.MetaverseAttribute.Id), "Attribute", attribute.MetaverseAttribute.Name);
            node.ItemId = attribute.MetaverseAttribute.Id;
            items.Add(node);
        }
        return ConfigurationSnapshotNode.CollectionNode("resultAttributes", items, "Result Attributes");
    }

    private ConfigurationSnapshotNode BuildPredefinedSearchCriteriaGroups(List<PredefinedSearchCriteriaGroup> groups)
    {
        var items = new List<ConfigurationSnapshotNode>();
        foreach (var group in groups.OrderBy(g => g.Position).ThenBy(g => g.Id))
        {
            var children = new List<ConfigurationSnapshotNode>();
            AddEnum(children, "type", group.Type, "Match");
            Add(children, "position", Render(group.Position), "Position");
            children.Add(BuildPredefinedSearchCriteria(group.Criteria));
            children.Add(BuildPredefinedSearchCriteriaGroups(group.ChildGroups));
            items.Add(ConfigurationSnapshotNode.ObjectNode("group", children, "Group", group.Id));
        }
        return ConfigurationSnapshotNode.CollectionNode("criteriaGroups", items, "Criteria");
    }

    private static ConfigurationSnapshotNode BuildPredefinedSearchCriteria(List<PredefinedSearchCriteria> criteria)
    {
        var items = new List<ConfigurationSnapshotNode>();
        foreach (var criterion in criteria.OrderBy(c => c.Id))
        {
            var children = new List<ConfigurationSnapshotNode>();
            AddReference(children, "metaverseAttributeId", criterion.MetaverseAttributeId, criterion.MetaverseAttribute?.Name, "Metaverse Attribute");
            AddEnum(children, "comparisonType", criterion.ComparisonType, "Comparison");
            Add(children, "stringValue", criterion.StringValue, "Value");
            Add(children, "intValue", Render(criterion.IntValue), "Value");
            Add(children, "longValue", Render(criterion.LongValue), "Value");
            Add(children, "dateTimeValue", Render(criterion.DateTimeValue), "Value");
            Add(children, "boolValue", Render(criterion.BoolValue), "Value");
            Add(children, "guidValue", Render(criterion.GuidValue), "Value");
            Add(children, "caseSensitive", Render(criterion.CaseSensitive), "Case sensitive");

            // The relative-date fields are only meaningful (and only ever populated) when ValueMode is Relative;
            // recording them unconditionally would show meaningless nulls against an Absolute criterion.
            if (criterion.ValueMode == DateCriteriaValueMode.Relative)
            {
                AddEnum(children, "valueMode", criterion.ValueMode, "Value mode");
                Add(children, "relativeCount", Render(criterion.RelativeCount), "Relative count");
                AddEnum(children, "relativeUnit", criterion.RelativeUnit, "Relative unit");
                AddEnum(children, "relativeDirection", criterion.RelativeDirection, "Relative direction");
            }

            items.Add(ConfigurationSnapshotNode.ObjectNode("criterion", children, "Criterion", criterion.Id));
        }
        return ConfigurationSnapshotNode.CollectionNode("criteria", items, "Criteria");
    }

    // -- Connector Definition --------------------------------------------------------------------------------------------

    /// <summary>
    /// Builds a scoped snapshot of a Connector Definition: its identity, the connector capability flags, its setting
    /// definitions (the schema an administrator must supply values for, not any supplied values), and its files as
    /// name/size/version/content-hash. The raw file binary (<see cref="ConnectorDefinitionFile.File"/>) is never
    /// captured in any form; the SHA-256 content hash already detects a re-upload of changed bytes without embedding
    /// the assembly. Connector Definitions carry no secrets; <paramref name="hashKey"/> keeps the signature uniform
    /// with the other builders. Load the definition with its files and settings
    /// (see <c>IConnectedSystemRepository.GetConnectorDefinitionAsync(int)</c>) so the snapshot reflects persisted truth.
    /// </summary>
    public ConfigurationSnapshot CreateSnapshot(ConnectorDefinition definition, byte[] hashKey)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var children = new List<ConfigurationSnapshotNode>();
        Add(children, "name", definition.Name, "Name");
        Add(children, "description", definition.Description, "Description");
        Add(children, "url", definition.Url, "URL");
        Add(children, "builtIn", Render(definition.BuiltIn), "Built-in");
        children.Add(BuildConnectorDefinitionCapabilities(definition));
        children.Add(BuildConnectorDefinitionSettings(definition.Settings, hashKey));
        children.Add(BuildConnectorDefinitionFiles(definition.Files));

        return new ConfigurationSnapshot
        {
            ObjectType = ConnectorDefinitionObjectType,
            ObjectId = definition.Id,
            ObjectName = definition.Name,
            Root = ConfigurationSnapshotNode.ObjectNode("connectorDefinition", children, "Connector Definition")
        };
    }

    private static ConfigurationSnapshotNode BuildConnectorDefinitionCapabilities(ConnectorDefinition definition)
    {
        // The capability flags drive Run Profile availability and export behaviour, so a change to any of them is a
        // material configuration change; group them under one object node so a diff reads as "Capabilities > Export: ...".
        var children = new List<ConfigurationSnapshotNode>();
        Add(children, "supportsFullImport", Render(definition.SupportsFullImport), "Full import");
        Add(children, "supportsDeltaImport", Render(definition.SupportsDeltaImport), "Delta import");
        Add(children, "supportsExport", Render(definition.SupportsExport), "Export");
        Add(children, "supportsPartitions", Render(definition.SupportsPartitions), "Partitions");
        Add(children, "supportsPartitionContainers", Render(definition.SupportsPartitionContainers), "Partition containers");
        Add(children, "supportsSecondaryExternalId", Render(definition.SupportsSecondaryExternalId), "Secondary external ID");
        Add(children, "supportsUserSelectedExternalId", Render(definition.SupportsUserSelectedExternalId), "User-selected external ID");
        Add(children, "supportsUserSelectedAttributeTypes", Render(definition.SupportsUserSelectedAttributeTypes), "User-selected attribute types");
        Add(children, "supportsAutoConfirmExport", Render(definition.SupportsAutoConfirmExport), "Auto-confirm export");
        Add(children, "supportsParallelExport", Render(definition.SupportsParallelExport), "Parallel export");
        Add(children, "supportsPaging", Render(definition.SupportsPaging), "Paging");
        Add(children, "supportsFilePaths", Render(definition.SupportsFilePaths), "File paths");
        return ConfigurationSnapshotNode.ObjectNode("capabilities", children, "Capabilities");
    }

    private static ConfigurationSnapshotNode BuildConnectorDefinitionSettings(List<ConnectorDefinitionSetting> settings, byte[] hashKey)
    {
        // Each setting definition is an ItemId-matched object so re-ordering does not diff and an added/removed setting
        // reads cleanly. These are setting *definitions* (the schema JIM asks the administrator to fill in), never
        // supplied values, so they carry no Connected System secrets. Belt-and-braces: an encrypted-typed default value
        // (never populated in practice) would be redacted, not stored plaintext.
        var items = new List<ConfigurationSnapshotNode>();
        foreach (var setting in (settings ?? []).OrderBy(s => s.Id))
        {
            var children = new List<ConfigurationSnapshotNode>();
            Add(children, "name", setting.Name, "Name");
            AddEnum(children, "category", setting.Category, "Category");
            AddEnum(children, "type", setting.Type, "Type");
            Add(children, "description", setting.Description, "Description");
            Add(children, "required", Render(setting.Required), "Required");
            if (setting.Type == ConnectedSystemSettingType.StringEncrypted && !string.IsNullOrEmpty(setting.DefaultStringValue))
                children.Add(ConfigurationSnapshotNode.Secret("defaultStringValue", ComputePlaintextHash(setting.DefaultStringValue, hashKey), "Default value"));
            else
                Add(children, "defaultStringValue", setting.DefaultStringValue, "Default value");
            Add(children, "defaultIntValue", Render(setting.DefaultIntValue), "Default value");
            Add(children, "defaultCheckboxValue", Render(setting.DefaultCheckboxValue), "Default value");
            Add(children, "dropDownValues", setting.DropDownValues is { Count: > 0 } ? string.Join(", ", setting.DropDownValues) : null, "Options");
            Add(children, "requiredGroup", setting.RequiredGroup, "Required group");
            AddEnum(children, "requiredGroupCardinality", setting.RequiredGroupCardinality, "Required group cardinality");
            Add(children, "requiredWhenSetting", setting.RequiredWhenSetting, "Required when setting");
            Add(children, "requiredWhenValue", setting.RequiredWhenValue, "Required when value");
            items.Add(ConfigurationSnapshotNode.ObjectNode("setting", children, "Setting", setting.Id));
        }
        return ConfigurationSnapshotNode.CollectionNode("settings", items, "Settings");
    }

    private static ConfigurationSnapshotNode BuildConnectorDefinitionFiles(List<ConnectorDefinitionFile> files)
    {
        // Files are ItemId-matched by their database id. The raw assembly bytes are never captured; the SHA-256 content
        // hash fingerprints the file so a re-upload of changed bytes diffs without embedding (or disclosing) the binary.
        var items = new List<ConfigurationSnapshotNode>();
        foreach (var file in (files ?? []).OrderBy(f => f.Id))
        {
            var children = new List<ConfigurationSnapshotNode>();
            Add(children, "filename", file.Filename, "Filename");
            Add(children, "fileSizeBytes", Render(file.FileSizeBytes), "File size (bytes)");
            Add(children, "version", file.Version, "Version");
            Add(children, "sha256", file.File is { Length: > 0 } ? Convert.ToHexString(SHA256.HashData(file.File)).ToLowerInvariant() : null, "Content hash (SHA-256)");
            items.Add(ConfigurationSnapshotNode.ObjectNode("file", children, "File", file.Id));
        }
        return ConfigurationSnapshotNode.CollectionNode("files", items, "Files");
    }

    // -- Example Data Set ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Builds a metadata-only snapshot of an Example Data Set: its name, culture, built-in flag, and the number of
    /// values it holds. The individual values are deliberately not captured (a built-in set can hold thousands of
    /// strings; embedding them would bloat every version and add nothing an auditor needs). A change to the value
    /// count is enough to show that the set's contents changed. Example Data Sets carry no secrets;
    /// <paramref name="hashKey"/> keeps the signature uniform with the other builders.
    /// </summary>
    public ConfigurationSnapshot CreateSnapshot(ExampleDataSet dataSet, byte[] hashKey)
    {
        ArgumentNullException.ThrowIfNull(dataSet);

        var children = new List<ConfigurationSnapshotNode>();
        Add(children, "name", dataSet.Name, "Name");
        Add(children, "culture", dataSet.Culture, "Culture");
        Add(children, "builtIn", Render(dataSet.BuiltIn), "Built-in");
        Add(children, "valueCount", Render(dataSet.Values?.Count ?? 0), "Value count");

        return new ConfigurationSnapshot
        {
            ObjectType = ExampleDataSetObjectType,
            ObjectId = dataSet.Id,
            ObjectName = dataSet.Name,
            Root = ConfigurationSnapshotNode.ObjectNode("exampleDataSet", children, "Example Data Set")
        };
    }

    // -- Example Data Template -----------------------------------------------------------------------------------------

    /// <summary>
    /// Builds a scoped snapshot of an Example Data (generation) Template: its identity and its object-type/attribute
    /// configuration, with each object type's generation attributes and their referenced Example Data Sets as nested
    /// children (stable DB ids). Referenced Example Data Sets are captured by id reference only, never their values, so
    /// the (potentially very large) set contents are not duplicated into the template's history. Templates carry no
    /// secrets; <paramref name="hashKey"/> keeps the signature uniform with the other builders. Load the template with
    /// its object types, template attributes and their associations (see
    /// <c>IExampleDataRepository.GetTemplateAsync(int)</c>) so the snapshot reflects persisted truth.
    /// </summary>
    public ConfigurationSnapshot CreateSnapshot(ExampleDataTemplate template, byte[] hashKey)
    {
        ArgumentNullException.ThrowIfNull(template);

        var children = new List<ConfigurationSnapshotNode>();
        Add(children, "name", template.Name, "Name");
        Add(children, "builtIn", Render(template.BuiltIn), "Built-in");
        children.Add(BuildExampleDataObjectTypes(template.ObjectTypes));

        return new ConfigurationSnapshot
        {
            ObjectType = ExampleDataTemplateObjectType,
            ObjectId = template.Id,
            ObjectName = template.Name,
            Root = ConfigurationSnapshotNode.ObjectNode("exampleDataTemplate", children, "Example Data Template")
        };
    }

    private static ConfigurationSnapshotNode BuildExampleDataObjectTypes(List<ExampleDataObjectType> objectTypes)
    {
        var items = new List<ConfigurationSnapshotNode>();
        foreach (var objectType in (objectTypes ?? []).OrderBy(o => o.Id))
        {
            var children = new List<ConfigurationSnapshotNode>();
            AddReference(children, "metaverseObjectTypeId", objectType.MetaverseObjectType?.Id, objectType.MetaverseObjectType?.Name, "Metaverse Object Type");
            Add(children, "objectsToCreate", Render(objectType.ObjectsToCreate), "Objects to create");
            children.Add(BuildExampleDataTemplateAttributes(objectType.TemplateAttributes));
            items.Add(ConfigurationSnapshotNode.ObjectNode("objectType", children, "Object Type", objectType.Id));
        }
        return ConfigurationSnapshotNode.CollectionNode("objectTypes", items, "Object Types");
    }

    private static ConfigurationSnapshotNode BuildExampleDataTemplateAttributes(List<ExampleDataTemplateAttribute> attributes)
    {
        var items = new List<ConfigurationSnapshotNode>();
        foreach (var attribute in (attributes ?? []).OrderBy(a => a.Id))
        {
            var children = new List<ConfigurationSnapshotNode>();
            AddReference(children, "metaverseAttributeId", attribute.MetaverseAttribute?.Id, attribute.MetaverseAttribute?.Name, "Metaverse Attribute");
            AddReference(children, "connectedSystemObjectTypeAttributeId", attribute.ConnectedSystemObjectTypeAttribute?.Id, attribute.ConnectedSystemObjectTypeAttribute?.Name, "Connected System Attribute");
            Add(children, "populatedValuesPercentage", Render(attribute.PopulatedValuesPercentage), "Populated values (%)");
            Add(children, "pattern", attribute.Pattern, "Pattern");
            Add(children, "expression", attribute.Expression, "Expression");
            Add(children, "minNumber", Render(attribute.MinNumber), "Minimum number");
            Add(children, "maxNumber", Render(attribute.MaxNumber), "Maximum number");
            Add(children, "sequentialNumbers", Render(attribute.SequentialNumbers), "Sequential numbers");
            Add(children, "randomNumbers", Render(attribute.RandomNumbers), "Random numbers");
            Add(children, "minDate", Render(attribute.MinDate), "Minimum date");
            Add(children, "maxDate", Render(attribute.MaxDate), "Maximum date");
            Add(children, "boolTrueDistribution", Render(attribute.BoolTrueDistribution), "Boolean true distribution");
            Add(children, "boolShouldBeRandom", Render(attribute.BoolShouldBeRandom), "Boolean random");
            Add(children, "managerDepthPercentage", Render(attribute.ManagerDepthPercentage), "Manager depth (%)");
            Add(children, "mvaRefMinAssignments", Render(attribute.MvaRefMinAssignments), "Min reference assignments");
            Add(children, "mvaRefMaxAssignments", Render(attribute.MvaRefMaxAssignments), "Max reference assignments");
            children.Add(BuildExampleDataReferencedSets(attribute.ExampleDataSetInstances));
            items.Add(ConfigurationSnapshotNode.ObjectNode("attribute", children, "Attribute", attribute.Id));
        }
        return ConfigurationSnapshotNode.CollectionNode("attributes", items, "Attributes");
    }

    private static ConfigurationSnapshotNode BuildExampleDataReferencedSets(List<ExampleDataSetInstance> instances)
    {
        // Each referenced Example Data Set is captured as a reference (the set's stable id as the value, its name as the
        // display form) keyed by the instance id, so re-ordering or swapping a referenced set diffs cleanly. The set's
        // own values are never embedded here; they live in that set's own configuration history.
        var items = new List<ConfigurationSnapshotNode>();
        foreach (var instance in (instances ?? []).OrderBy(i => i.Order).ThenBy(i => i.Id))
        {
            var node = ConfigurationSnapshotNode.Scalar("exampleDataSetId", Render(instance.ExampleDataSet?.Id), "Example Data Set", instance.ExampleDataSet?.Name);
            node.ItemId = instance.Id;
            items.Add(node);
        }
        return ConfigurationSnapshotNode.CollectionNode("referencedDataSets", items, "Referenced Data Sets");
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
