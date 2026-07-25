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
                              RemovedAttributes.Values.Sum(v => v.Count) > 0;

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
