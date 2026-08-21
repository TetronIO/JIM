// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// Contains the results of a schema refresh operation, including details about
/// what changed, what was added, what was removed, and any issues detected.
/// </summary>
public class SchemaRefreshResult
{
    /// <summary>
    /// Whether the schema refresh completed successfully.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Error message if the refresh failed.
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
    /// Object types that were added (new to the schema).
    /// </summary>
    public List<string> AddedObjectTypes { get; set; } = new();

    /// <summary>
    /// Object types that were removed from the schema.
    /// </summary>
    public List<string> RemovedObjectTypes { get; set; } = new();

    /// <summary>
    /// Object types that already existed and were updated.
    /// </summary>
    public List<string> UpdatedObjectTypes { get; set; } = new();

    /// <summary>
    /// Attributes that were added, grouped by object type name.
    /// </summary>
    public Dictionary<string, List<string>> AddedAttributes { get; set; } = new();

    /// <summary>
    /// Attributes that were removed, grouped by object type name.
    /// </summary>
    public Dictionary<string, List<string>> RemovedAttributes { get; set; } = new();

    /// <summary>
    /// Attributes whose definition (data type or plurality) the Connector restated, grouped by object type name.
    /// These changes are applied by the merge; they are reported because a definition change can invalidate an
    /// Attribute Flow mapping validated against the old definition. A data type an administrator overrode is
    /// never overwritten and therefore never appears here.
    /// </summary>
    public Dictionary<string, List<SchemaAttributeDefinitionChange>> ChangedAttributes { get; set; } = new();

    /// <summary>
    /// Attributes that could not be removed because they are referenced by Synchronisation Rules.
    /// Key is the attribute name, value is the list of Synchronisation Rule names that reference it.
    /// </summary>
    public Dictionary<string, List<string>> AttributesInUse { get; set; } = new();

    /// <summary>
    /// Credential attributes found in the Connected System's schema and blocked, grouped by object type name.
    /// Blocked attributes are never imported as managed attributes and can never be used in an Attribute Flow;
    /// passwords are handled by JIM's dedicated write-only password channel instead. They are reported here so
    /// the outcome is visible to the administrator rather than silent, and they are deliberately absent from
    /// <see cref="AddedAttributes"/> and <see cref="RemovedAttributes"/> because they are neither.
    /// </summary>
    public Dictionary<string, List<string>> BlockedCredentialAttributes { get; set; } = new();

    /// <summary>
    /// The schema as JIM held it before the merge ran: the merge rebuilds the in-memory graph from what the
    /// Connector reported, so this snapshot is the only place a removed entry's id survives, and it is what
    /// dependent detection (#1485) resolves removal names against.
    /// </summary>
    public List<SchemaRefreshPreRefreshType> PreRefreshSchema { get; set; } = new();

    /// <summary>
    /// Discovery shortfalls the Connector worked around rather than failed on, copied from
    /// <see cref="ConnectorSchema.Warnings"/> so the portal can show them alongside what changed. The schema
    /// import's Activity carries the same warnings, which is how they reach the REST API and PowerShell.
    /// </summary>
    public List<string> DiscoveryWarnings { get; set; } = new();

    /// <summary>
    /// The total number of credential attributes blocked across all object types.
    /// </summary>
    /// <summary>
    /// Whether a password policy was read from the Connected System during this refresh. False when the Connector
    /// cannot discover policies, when the system exposes none, or when the read failed; a failed read never fails
    /// the schema import, so this is how the outcome stays visible rather than silent.
    /// </summary>
    public bool PasswordPolicyDiscovered { get; set; }

    public int BlockedCredentialAttributeCount => BlockedCredentialAttributes.Values.Sum(v => v.Count);

    /// <summary>
    /// Whether any action is required from the user (e.g., attributes in use that need attention).
    /// </summary>
    public bool ActionRequired => AttributesInUse.Count > 0;

    /// <summary>
    /// Whether there were any changes to the schema.
    /// </summary>
    public bool HasChanges => AddedObjectTypes.Count > 0 ||
                              RemovedObjectTypes.Count > 0 ||
                              AddedAttributes.Values.Sum(v => v.Count) > 0 ||
                              RemovedAttributes.Values.Sum(v => v.Count) > 0 ||
                              ChangedAttributes.Values.Sum(v => v.Count) > 0;

    /// <summary>
    /// Whether the refresh found changes that can invalidate existing configuration or leave stale data behind:
    /// removals (which JIM reports but deliberately retains; see issue #782) and attribute definition changes.
    /// Additions never set this; they cannot break anything that already works. Surfaces use this to decide
    /// whether discarding or applying the refresh warrants a warning.
    /// </summary>
    public bool HasRemovalsOrDefinitionChanges => RemovedObjectTypes.Count > 0 ||
                                                  RemovedAttributes.Values.Sum(v => v.Count) > 0 ||
                                                  ChangedAttributes.Values.Sum(v => v.Count) > 0;

    /// <summary>
    /// Records a definition change the merge applied to an existing attribute, grouped under its object type.
    /// </summary>
    public void AddChangedAttribute(string objectTypeName, SchemaAttributeDefinitionChange change)
    {
        if (!ChangedAttributes.TryGetValue(objectTypeName, out var changes))
        {
            changes = new List<SchemaAttributeDefinitionChange>();
            ChangedAttributes[objectTypeName] = changes;
        }
        changes.Add(change);
    }

    /// <summary>
    /// Creates a successful result with no changes.
    /// </summary>
    public static SchemaRefreshResult NoChanges(int objectTypeCount, int attributeCount)
    {
        return new SchemaRefreshResult
        {
            Success = true,
            TotalObjectTypes = objectTypeCount,
            TotalAttributes = attributeCount
        };
    }

    /// <summary>
    /// Creates a failed result with an error message.
    /// </summary>
    public static SchemaRefreshResult Failed(string errorMessage)
    {
        return new SchemaRefreshResult
        {
            Success = false,
            ErrorMessage = errorMessage
        };
    }
}
