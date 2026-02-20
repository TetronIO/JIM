namespace JIM.Models.Staging.DTOs;

/// <summary>
/// Result of switching the object matching rule mode for a Connected System.
/// </summary>
public class ObjectMatchingModeSwitchResult
{
    /// <summary>
    /// Whether the mode switch was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// The new mode after the switch.
    /// </summary>
    public ObjectMatchingRuleMode NewMode { get; set; }

    /// <summary>
    /// Number of sync rules that were updated during the switch.
    /// </summary>
    public int SyncRulesUpdated { get; set; }

    /// <summary>
    /// Number of object types that had matching rules set (when switching to Simple Mode).
    /// </summary>
    public int ObjectTypesUpdated { get; set; }

    /// <summary>
    /// Warning messages about the switch (e.g., diverging rules).
    /// </summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>
    /// Detailed information about what happened per object type.
    /// </summary>
    public List<ObjectTypeMatchingRuleMigration> ObjectTypeMigrations { get; set; } = new();

    /// <summary>
    /// Error message if the switch failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    public static ObjectMatchingModeSwitchResult ToAdvancedMode(int syncRulesUpdated)
    {
        return new ObjectMatchingModeSwitchResult
        {
            Success = true,
            NewMode = ObjectMatchingRuleMode.SyncRule,
            SyncRulesUpdated = syncRulesUpdated
        };
    }

    public static ObjectMatchingModeSwitchResult ToSimpleMode(int objectTypesUpdated, List<ObjectTypeMatchingRuleMigration> migrations)
    {
        var result = new ObjectMatchingModeSwitchResult
        {
            Success = true,
            NewMode = ObjectMatchingRuleMode.ConnectedSystem,
            ObjectTypesUpdated = objectTypesUpdated,
            ObjectTypeMigrations = migrations
        };

        // Add warnings for any object types where rules diverged
        foreach (var migration in migrations.Where(m => m.RulesDiverged))
        {
            result.Warnings.Add($"Object type '{migration.ObjectTypeName}' had {migration.UniqueSyncRuleConfigurations} " +
                $"different matching rule configurations across {migration.SyncRuleCount} sync rules. " +
                $"The most common configuration was used.");
        }

        return result;
    }

    public static ObjectMatchingModeSwitchResult NoChange(ObjectMatchingRuleMode currentMode)
    {
        return new ObjectMatchingModeSwitchResult
        {
            Success = true,
            NewMode = currentMode
        };
    }

    public static ObjectMatchingModeSwitchResult Failed(string errorMessage)
    {
        return new ObjectMatchingModeSwitchResult
        {
            Success = false,
            ErrorMessage = errorMessage
        };
    }
}

/// <summary>
/// Details about matching rule migration for a single object type when switching to Simple Mode.
/// </summary>
public class ObjectTypeMatchingRuleMigration
{
    /// <summary>
    /// The ID of the object type.
    /// </summary>
    public int ObjectTypeId { get; set; }

    /// <summary>
    /// The name of the object type.
    /// </summary>
    public string ObjectTypeName { get; set; } = string.Empty;

    /// <summary>
    /// Number of import sync rules for this object type.
    /// </summary>
    public int SyncRuleCount { get; set; }

    /// <summary>
    /// Number of sync rules that had matching rules defined.
    /// </summary>
    public int SyncRulesWithMatchingRules { get; set; }

    /// <summary>
    /// Number of unique matching rule configurations found across sync rules.
    /// </summary>
    public int UniqueSyncRuleConfigurations { get; set; }

    /// <summary>
    /// Whether the matching rules diverged across sync rules (required best-guess selection).
    /// </summary>
    public bool RulesDiverged => UniqueSyncRuleConfigurations > 1;

    /// <summary>
    /// Number of matching rules that were set on the object type.
    /// </summary>
    public int MatchingRulesSet { get; set; }

    /// <summary>
    /// Number of sync rules that had their matching rules cleared.
    /// </summary>
    public int SyncRulesCleared { get; set; }
}
