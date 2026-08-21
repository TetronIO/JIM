// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Web.Models.Api;

/// <summary>
/// API representation of a Connector Definition: the metadata, capabilities and settings a client needs
/// to configure a Connected System. Replaces the raw entity response (#1447), which dragged each connector
/// binary onto the wire as base64 and exposed whatever navigation graph EF happened to have loaded.
/// </summary>
public class ConnectorDefinitionDto
{
    /// <summary>
    /// The unique identifier of the Connector Definition.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The name of the Connector Definition, e.g. "JIM LDAP Connector".
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// A description of what the connector synchronises with.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// An optional URL with more information about the connector.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// Whether the connector ships with JIM (as opposed to being third-party supplied).
    /// </summary>
    public bool BuiltIn { get; set; }

    /// <summary>
    /// When the Connector Definition was created (UTC).
    /// </summary>
    public DateTime Created { get; set; }

    /// <summary>
    /// The display name of the principal that created the Connector Definition.
    /// </summary>
    public string? CreatedByName { get; set; }

    /// <summary>
    /// When the Connector Definition was last modified (UTC). Null if never modified after creation.
    /// </summary>
    public DateTime? LastUpdated { get; set; }

    /// <summary>
    /// The display name of the principal that last modified the Connector Definition.
    /// </summary>
    public string? LastUpdatedByName { get; set; }

    /// <summary>
    /// The settings the connector asks an administrator to supply when configuring a Connected System.
    /// </summary>
    public List<ConnectorDefinitionSettingDto> Settings { get; set; } = new();

    /// <summary>
    /// Metadata about the files that make up the connector. The file contents are never returned by the API.
    /// </summary>
    public List<ConnectorDefinitionFileDto> Files { get; set; } = new();

    /// <summary>
    /// Whether the connector supports Full Imports.
    /// </summary>
    public bool SupportsFullImport { get; set; }

    /// <summary>
    /// Whether the connector supports Delta Imports.
    /// </summary>
    public bool SupportsDeltaImport { get; set; }

    /// <summary>
    /// Whether the connector supports exporting changes to the Connected System.
    /// </summary>
    public bool SupportsExport { get; set; }

    /// <summary>
    /// Whether the connector supports partitions.
    /// </summary>
    public bool SupportsPartitions { get; set; }

    /// <summary>
    /// Whether the connector supports partition containers.
    /// </summary>
    public bool SupportsPartitionContainers { get; set; }

    /// <summary>
    /// Whether the connector uses a secondary identifier (i.e. an LDAP DN) alongside the immutable external id.
    /// </summary>
    public bool SupportsSecondaryExternalId { get; set; }

    /// <summary>
    /// Whether the administrator selects which attribute is the external id.
    /// </summary>
    public bool SupportsUserSelectedExternalId { get; set; }

    /// <summary>
    /// Whether the administrator can override discovered attribute data types.
    /// </summary>
    public bool SupportsUserSelectedAttributeTypes { get; set; }

    /// <summary>
    /// Whether the connector can confirm exports immediately rather than via the next import cycle.
    /// </summary>
    public bool SupportsAutoConfirmExport { get; set; }

    /// <summary>
    /// Whether the connector supports parallel export batch processing.
    /// </summary>
    public bool SupportsParallelExport { get; set; }

    /// <summary>
    /// Whether the connector supports paged imports/exports.
    /// </summary>
    public bool SupportsPaging { get; set; }

    /// <summary>
    /// Whether the connector uses file paths for import and/or export operations.
    /// </summary>
    public bool SupportsFilePaths { get; set; }

    /// <summary>
    /// Whether the connector can set passwords on objects in the Connected System.
    /// </summary>
    public bool SupportsPasswordSet { get; set; }

    /// <summary>
    /// Whether the connector can read the password policy the Connected System enforces.
    /// </summary>
    public bool SupportsPasswordPolicyDiscovery { get; set; }

    /// <summary>
    /// Which wire standard's vocabulary the connector's schema follows (e.g. "Scim", "Ldap", "NotSet").
    /// Advisory metadata used for Standard Mapping hints.
    /// </summary>
    public string SchemaStandard { get; set; } = null!;

    /// <summary>
    /// Creates a DTO from an entity. The entity's Settings and Files collections should be populated.
    /// </summary>
    public static ConnectorDefinitionDto FromEntity(ConnectorDefinition entity)
    {
        return new ConnectorDefinitionDto
        {
            Id = entity.Id,
            Name = entity.Name,
            Description = entity.Description,
            Url = entity.Url,
            BuiltIn = entity.BuiltIn,
            Created = entity.Created,
            CreatedByName = entity.CreatedByName,
            LastUpdated = entity.LastUpdated,
            LastUpdatedByName = entity.LastUpdatedByName,
            Settings = entity.Settings.Select(ConnectorDefinitionSettingDto.FromEntity).ToList(),
            Files = entity.Files.Select(ConnectorDefinitionFileDto.FromEntity).ToList(),
            SupportsFullImport = entity.SupportsFullImport,
            SupportsDeltaImport = entity.SupportsDeltaImport,
            SupportsExport = entity.SupportsExport,
            SupportsPartitions = entity.SupportsPartitions,
            SupportsPartitionContainers = entity.SupportsPartitionContainers,
            SupportsSecondaryExternalId = entity.SupportsSecondaryExternalId,
            SupportsUserSelectedExternalId = entity.SupportsUserSelectedExternalId,
            SupportsUserSelectedAttributeTypes = entity.SupportsUserSelectedAttributeTypes,
            SupportsAutoConfirmExport = entity.SupportsAutoConfirmExport,
            SupportsParallelExport = entity.SupportsParallelExport,
            SupportsPaging = entity.SupportsPaging,
            SupportsFilePaths = entity.SupportsFilePaths,
            SupportsPasswordSet = entity.SupportsPasswordSet,
            SupportsPasswordPolicyDiscovery = entity.SupportsPasswordPolicyDiscovery,
            SchemaStandard = entity.SchemaStandard.ToString()
        };
    }
}

/// <summary>
/// API representation of a single connector setting definition.
/// </summary>
public class ConnectorDefinitionSettingDto
{
    /// <summary>
    /// The unique identifier of the setting definition.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The name of the setting, e.g. "Connection String".
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// A description of what the setting controls.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// The settings page section the setting appears under: Connectivity, General, Capabilities, Schema, Import or Export.
    /// </summary>
    public string Category { get; set; } = null!;

    /// <summary>
    /// The value type of the setting, e.g. "String", "StringEncrypted", "Integer", "CheckBox", "DropDown".
    /// </summary>
    public string Type { get; set; } = null!;

    /// <summary>
    /// The default value for a checkbox setting.
    /// </summary>
    public bool? DefaultCheckboxValue { get; set; }

    /// <summary>
    /// The default value for a string setting.
    /// </summary>
    public string? DefaultStringValue { get; set; }

    /// <summary>
    /// The default value for an integer setting.
    /// </summary>
    public int? DefaultIntValue { get; set; }

    /// <summary>
    /// The permitted values for a drop-down setting.
    /// </summary>
    public List<string>? DropDownValues { get; set; }

    /// <summary>
    /// Whether the administrator must supply a value.
    /// </summary>
    public bool Required { get; set; }

    /// <summary>
    /// The name of the requirement group the setting belongs to, when a value is required
    /// for one-of/all-of a group of settings rather than the setting individually.
    /// </summary>
    public string? RequiredGroup { get; set; }

    /// <summary>
    /// How many settings in the requirement group need values: "AtLeastOne" or "All".
    /// </summary>
    public string RequiredGroupCardinality { get; set; } = null!;

    /// <summary>
    /// The name of another setting that makes this one required when it holds <see cref="RequiredWhenValue"/>.
    /// </summary>
    public string? RequiredWhenSetting { get; set; }

    /// <summary>
    /// The value of <see cref="RequiredWhenSetting"/> that makes this setting required.
    /// </summary>
    public string? RequiredWhenValue { get; set; }

    /// <summary>
    /// Creates a DTO from an entity.
    /// </summary>
    public static ConnectorDefinitionSettingDto FromEntity(ConnectorDefinitionSetting entity)
    {
        return new ConnectorDefinitionSettingDto
        {
            Id = entity.Id,
            Name = entity.Name,
            Description = entity.Description,
            Category = entity.Category.ToString(),
            Type = entity.Type.ToString(),
            DefaultCheckboxValue = entity.DefaultCheckboxValue,
            DefaultStringValue = entity.DefaultStringValue,
            DefaultIntValue = entity.DefaultIntValue,
            DropDownValues = entity.DropDownValues,
            Required = entity.Required,
            RequiredGroup = entity.RequiredGroup,
            RequiredGroupCardinality = entity.RequiredGroupCardinality.ToString(),
            RequiredWhenSetting = entity.RequiredWhenSetting,
            RequiredWhenValue = entity.RequiredWhenValue
        };
    }
}

/// <summary>
/// API representation of one file that makes up a connector: metadata only, never the binary payload.
/// </summary>
public class ConnectorDefinitionFileDto
{
    /// <summary>
    /// The unique identifier of the connector file.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The filename of the connector file, e.g. "JIM.Connectors.dll".
    /// </summary>
    public string Filename { get; set; } = null!;

    /// <summary>
    /// The version of the connector file.
    /// </summary>
    public string Version { get; set; } = null!;

    /// <summary>
    /// The size of the connector file in bytes.
    /// </summary>
    public int FileSizeBytes { get; set; }

    /// <summary>
    /// Whether the file implements the core connector interface.
    /// </summary>
    public bool ImplementsIConnector { get; set; }

    /// <summary>
    /// Whether the file declares connector capabilities.
    /// </summary>
    public bool ImplementsICapabilities { get; set; }

    /// <summary>
    /// Whether the file implements schema discovery.
    /// </summary>
    public bool ImplementsISchema { get; set; }

    /// <summary>
    /// Whether the file declares connector settings.
    /// </summary>
    public bool ImplementsISettings { get; set; }

    /// <summary>
    /// Whether the file implements call-based export.
    /// </summary>
    public bool ImplementsIExportUsingCalls { get; set; }

    /// <summary>
    /// Whether the file implements file-based export.
    /// </summary>
    public bool ImplementsIExportUsingFiles { get; set; }

    /// <summary>
    /// Whether the file implements call-based import.
    /// </summary>
    public bool ImplementsIImportUsingCalls { get; set; }

    /// <summary>
    /// Whether the file implements file-based import.
    /// </summary>
    public bool ImplementsIImportUsingFiles { get; set; }

    /// <summary>
    /// Creates a DTO from an entity. The binary payload is deliberately not carried.
    /// </summary>
    public static ConnectorDefinitionFileDto FromEntity(ConnectorDefinitionFile entity)
    {
        return new ConnectorDefinitionFileDto
        {
            Id = entity.Id,
            Filename = entity.Filename,
            Version = entity.Version,
            FileSizeBytes = entity.FileSizeBytes,
            ImplementsIConnector = entity.ImplementsIConnector,
            ImplementsICapabilities = entity.ImplementsICapabilities,
            ImplementsISchema = entity.ImplementsISchema,
            ImplementsISettings = entity.ImplementsISettings,
            ImplementsIExportUsingCalls = entity.ImplementsIExportUsingCalls,
            ImplementsIExportUsingFiles = entity.ImplementsIExportUsingFiles,
            ImplementsIImportUsingCalls = entity.ImplementsIImportUsingCalls,
            ImplementsIImportUsingFiles = entity.ImplementsIImportUsingFiles
        };
    }
}
