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
    /// Attributes that could not be removed because they are referenced by sync rules.
    /// Key is the attribute name, value is the list of sync rule names that reference it.
    /// </summary>
    public Dictionary<string, List<string>> AttributesInUse { get; set; } = new();

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
