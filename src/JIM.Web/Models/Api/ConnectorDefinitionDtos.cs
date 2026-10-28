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
    /// The ID of the Connector.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The name of the Connector, such as "JIM LDAP Connector".
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// What the Connector connects to; null when none is given.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Where to read more about the Connector; null when none is given.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// Whether the Connector ships with JIM, rather than being supplied by someone else.
    /// </summary>
    public bool BuiltIn { get; set; }

    /// <summary>
    /// When the Connector was added (UTC).
    /// </summary>
    public DateTime Created { get; set; }

    /// <summary>
    /// Who added the Connector.
    /// </summary>
    public string? CreatedByName { get; set; }

    /// <summary>
    /// When the Connector was last changed (UTC); null if it never has been.
    /// </summary>
    public DateTime? LastUpdated { get; set; }

    /// <summary>
    /// Who last changed the Connector.
    /// </summary>
    public string? LastUpdatedByName { get; set; }

    /// <summary>
    /// The settings an administrator gives a Connected System that uses the Connector.
    /// </summary>
    public List<ConnectorDefinitionSettingDto> Settings { get; set; } = new();

    /// <summary>
    /// The files that make up the Connector. Their contents are never returned.
    /// </summary>
    public List<ConnectorDefinitionFileDto> Files { get; set; } = new();

    /// <summary>
    /// Whether the Connector can run a Full Import.
    /// </summary>
    public bool SupportsFullImport { get; set; }

    /// <summary>
    /// Whether the Connector can run a Delta Import.
    /// </summary>
    public bool SupportsDeltaImport { get; set; }

    /// <summary>
    /// Whether the Connector can export changes to the Connected System.
    /// </summary>
    public bool SupportsExport { get; set; }

    /// <summary>
    /// Whether the Connector reads its Connected System as partitions.
    /// </summary>
    public bool SupportsPartitions { get; set; }

    /// <summary>
    /// Whether the Connector reads the containers in each partition, so you can choose which ones JIM imports from.
    /// </summary>
    public bool SupportsPartitionContainers { get; set; }

    /// <summary>
    /// Whether the Connector gives each object a second identifier, such as an LDAP Distinguished Name, beside its
    /// unchanging external ID.
    /// </summary>
    public bool SupportsSecondaryExternalId { get; set; }

    /// <summary>
    /// Whether the administrator chooses which attribute is the external ID.
    /// </summary>
    public bool SupportsUserSelectedExternalId { get; set; }

    /// <summary>
    /// Whether the administrator can change the data type the Connector found for an attribute.
    /// </summary>
    public bool SupportsUserSelectedAttributeTypes { get; set; }

    /// <summary>
    /// Whether the administrator can say whether an attribute holds one value or several, where the Connected System's
    /// schema cannot state it (a delimited file).
    /// </summary>
    public bool SupportsUserSelectedAttributePlurality { get; set; }

    /// <summary>
    /// Whether the Connector confirms an export as soon as it is made, rather than at the next import.
    /// </summary>
    public bool SupportsAutoConfirmExport { get; set; }

    /// <summary>
    /// Whether the Connector can export batches in parallel.
    /// </summary>
    public bool SupportsParallelExport { get; set; }

    /// <summary>
    /// Whether the Connector imports and exports a page at a time.
    /// </summary>
    public bool SupportsPaging { get; set; }

    /// <summary>
    /// Whether the Connector imports from or exports to files.
    /// </summary>
    public bool SupportsFilePaths { get; set; }

    /// <summary>
    /// Whether the Connector can set passwords on objects in the Connected System.
    /// </summary>
    public bool SupportsPasswordSet { get; set; }

    /// <summary>
    /// Whether the Connector can read the password policy the Connected System enforces.
    /// </summary>
    public bool SupportsPasswordPolicyDiscovery { get; set; }

    /// <summary>
    /// Whether the Connector can search the Connected System for values already in use, so that generating a unique
    /// value can avoid one held by an account JIM has never imported.
    /// </summary>
    public bool SupportsUniquenessProbe { get; set; }

    /// <summary>
    /// Whether the Connector reports an export refused because a value is already in use as that, rather than as an
    /// ordinary export failure, so that JIM can generate another value.
    /// </summary>
    public bool SupportsUniquenessRejectionClassification { get; set; }

    /// <summary>
    /// Whether an external ID identifies one object across every Object Type of a Connected System using the
    /// Connector. When true, an import refuses an object that has changed Object Type, rather than treating it as a
    /// deletion and a new object, unless the Full Import Run Profile allows Object Type changes. When false, a Run
    /// Profile's Allow Object Type Changes setting has no effect.
    /// </summary>
    /// <remarks>
    /// #2049.
    /// </remarks>
    public bool ExternalIdsUniqueAcrossObjectTypes { get; set; }

    /// <summary>
    /// Which standard's attribute names the Connector's schema follows, such as Scim or Ldap; NotSet when it
    /// follows none. JIM uses it to suggest Standard Mappings.
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
            SupportsUserSelectedAttributePlurality = entity.SupportsUserSelectedAttributePlurality,
            SupportsAutoConfirmExport = entity.SupportsAutoConfirmExport,
            SupportsParallelExport = entity.SupportsParallelExport,
            SupportsPaging = entity.SupportsPaging,
            SupportsFilePaths = entity.SupportsFilePaths,
            SupportsPasswordSet = entity.SupportsPasswordSet,
            SupportsPasswordPolicyDiscovery = entity.SupportsPasswordPolicyDiscovery,
            SupportsUniquenessProbe = entity.SupportsUniquenessProbe,
            SupportsUniquenessRejectionClassification = entity.SupportsUniquenessRejectionClassification,
            ExternalIdsUniqueAcrossObjectTypes = entity.ExternalIdsUniqueAcrossObjectTypes,
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
    /// An example value to start from, such as a complete JSON document, shown verbatim beneath the setting in the
    /// portal. Null when the Connector supplies no example for the setting.
    /// </summary>
    public string? Example { get; set; }

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
            Example = entity.Example,
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
    /// The file name of the Connector file, such as "Example.Connector.dll".
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
