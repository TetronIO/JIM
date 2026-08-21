// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging.DTOs;

namespace JIM.Web.Models.Api;

/// <summary>
/// API response describing what a schema refresh found (or, for the preview endpoint, would find) at a Connected
/// System. Removals are reported but never applied by a refresh; entries the Connected System no longer offers
/// are retained in JIM.
/// </summary>
public class SchemaRefreshResultDto
{
    /// <summary>
    /// Whether the schema retrieval completed successfully.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Error message if the retrieval failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Total number of object types found in the schema.
    /// </summary>
    public int TotalObjectTypes { get; set; }

    /// <summary>
    /// Total number of attributes found across all object types.
    /// </summary>
    public int TotalAttributes { get; set; }

    /// <summary>
    /// Object types that are new to the schema.
    /// </summary>
    public List<string> AddedObjectTypes { get; set; } = new();

    /// <summary>
    /// Object types the Connected System no longer reports. Retained in JIM, not deleted.
    /// </summary>
    public List<string> RemovedObjectTypes { get; set; } = new();

    /// <summary>
    /// Object types that already existed and were updated.
    /// </summary>
    public List<string> UpdatedObjectTypes { get; set; } = new();

    /// <summary>
    /// Attributes that are new, grouped by object type name.
    /// </summary>
    public Dictionary<string, List<string>> AddedAttributes { get; set; } = new();

    /// <summary>
    /// Attributes the Connected System no longer reports, grouped by object type name. Retained in JIM, not
    /// deleted; their values stop refreshing.
    /// </summary>
    public Dictionary<string, List<string>> RemovedAttributes { get; set; } = new();

    /// <summary>
    /// Attributes whose definition (data type or plurality) the Connector restated, grouped by object type name.
    /// </summary>
    public Dictionary<string, List<SchemaAttributeDefinitionChangeDto>> ChangedAttributes { get; set; } = new();

    /// <summary>
    /// Attributes no longer reported that are still referenced by Synchronisation Rules. Key is the attribute
    /// name, value is the list of Synchronisation Rule names that reference it.
    /// </summary>
    public Dictionary<string, List<string>> AttributesInUse { get; set; } = new();

    /// <summary>
    /// Credential attributes found in the schema and blocked from management, grouped by object type name.
    /// </summary>
    public Dictionary<string, List<string>> BlockedCredentialAttributes { get; set; } = new();

    /// <summary>
    /// Discovery shortfalls the Connector worked around rather than failed on. A partial read can make entries
    /// appear removed when they are not; check these before committing a refresh.
    /// </summary>
    public List<string> DiscoveryWarnings { get; set; } = new();

    /// <summary>
    /// Whether a password policy was read from the Connected System during the retrieval.
    /// </summary>
    public bool PasswordPolicyDiscovered { get; set; }

    /// <summary>
    /// Whether there were any changes to the schema.
    /// </summary>
    public bool HasChanges { get; set; }

    /// <summary>
    /// Whether the refresh found changes that can invalidate existing configuration or leave stale data behind:
    /// removals and attribute definition changes. Additions never set this.
    /// </summary>
    public bool HasRemovalsOrDefinitionChanges { get; set; }

    /// <summary>
    /// What the destructive changes invalidate (#1485): the Synchronisation Rules and Attribute Flow mappings
    /// that would be disabled by committing with <c>disableDependents</c>, each with its reason, plus Object
    /// Matching Rules needing attention. Null when the refresh carries no destructive changes.
    /// </summary>
    public SchemaRefreshDependents? Dependents { get; set; }

    /// <summary>
    /// Maps a schema refresh result to its API representation.
    /// </summary>
    public static SchemaRefreshResultDto FromModel(SchemaRefreshResult result)
    {
        return new SchemaRefreshResultDto
        {
            Success = result.Success,
            ErrorMessage = result.ErrorMessage,
            TotalObjectTypes = result.TotalObjectTypes,
            TotalAttributes = result.TotalAttributes,
            AddedObjectTypes = result.AddedObjectTypes,
            RemovedObjectTypes = result.RemovedObjectTypes,
            UpdatedObjectTypes = result.UpdatedObjectTypes,
            AddedAttributes = result.AddedAttributes,
            RemovedAttributes = result.RemovedAttributes,
            ChangedAttributes = result.ChangedAttributes.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.Select(SchemaAttributeDefinitionChangeDto.FromModel).ToList()),
            AttributesInUse = result.AttributesInUse,
            BlockedCredentialAttributes = result.BlockedCredentialAttributes,
            DiscoveryWarnings = result.DiscoveryWarnings,
            PasswordPolicyDiscovered = result.PasswordPolicyDiscovered,
            HasChanges = result.HasChanges,
            HasRemovalsOrDefinitionChanges = result.HasRemovalsOrDefinitionChanges
        };
    }
}

/// <summary>
/// A change a schema refresh applies to the definition of an attribute JIM already holds.
/// </summary>
public class SchemaAttributeDefinitionChangeDto
{
    /// <summary>
    /// The name of the attribute whose definition changed.
    /// </summary>
    public string AttributeName { get; set; } = null!;

    /// <summary>
    /// Which aspect changed: "DataType" or "Plurality".
    /// </summary>
    public string Aspect { get; set; } = null!;

    /// <summary>
    /// The value JIM held before the refresh (e.g. "Text", "MultiValued").
    /// </summary>
    public string OldValue { get; set; } = null!;

    /// <summary>
    /// The value the Connector reported.
    /// </summary>
    public string NewValue { get; set; } = null!;

    /// <summary>
    /// Maps a definition change to its API representation.
    /// </summary>
    public static SchemaAttributeDefinitionChangeDto FromModel(SchemaAttributeDefinitionChange change)
    {
        return new SchemaAttributeDefinitionChangeDto
        {
            AttributeName = change.AttributeName,
            Aspect = change.Aspect.ToString(),
            OldValue = change.OldValue,
            NewValue = change.NewValue
        };
    }
}
