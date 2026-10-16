// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// What switching a Connected System's Object Matching Rule mode did.
/// </summary>
public class ObjectMatchingModeSwitchResult
{
    /// <summary>
    /// Whether the switch was made.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// The mode the Connected System is now in: ConnectedSystem (simple mode, rules on Object Types) or SyncRule
    /// (advanced mode, rules on Synchronisation Rules).
    /// </summary>
    public ObjectMatchingRuleMode NewMode { get; set; }

    /// <summary>
    /// How many import Synchronisation Rules received a copy of their Object Type's rules, when switching to
    /// advanced mode; 0 otherwise. One that already had rules of its own keeps them.
    /// </summary>
    public int SyncRulesUpdated { get; set; }

    /// <summary>
    /// How many Object Types received rules, when switching to simple mode; 0 otherwise.
    /// </summary>
    public int ObjectTypesUpdated { get; set; }

    /// <summary>
    /// What the switch left behind or discarded, one sentence each: rules kept but no longer consulted, export
    /// Synchronisation Rules left with none, rules that differed between Synchronisation Rules, and rules discarded
    /// because an Object Type already had its own.
    /// </summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>
    /// When switching to simple mode, what happened on each Object Type; empty otherwise.
    /// </summary>
    public List<ObjectTypeMatchingRuleMigration> ObjectTypeMigrations { get; set; } = new();

    /// <summary>
    /// Why the switch failed; null when it succeeded.
    /// </summary>
    public string? ErrorMessage { get; set; }

    public static ObjectMatchingModeSwitchResult ToAdvancedMode(int syncRulesUpdated, List<string>? warnings = null)
    {
        return new ObjectMatchingModeSwitchResult
        {
            Success = true,
            NewMode = ObjectMatchingRuleMode.SyncRule,
            SyncRulesUpdated = syncRulesUpdated,
            Warnings = warnings ?? new List<string>()
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
                $"different matching rule configurations across {migration.SyncRuleCount} Synchronisation Rules. " +
                $"The most common configuration was used.");
        }

        // Add warnings for any object types whose existing rules took precedence: the Synchronisation Rules' own
        // rules were cleared without being migrated, and that loss was previously silent (#1569).
        foreach (var migration in migrations.Where(m => m.ObjectTypeRulesTookPrecedence))
        {
            result.Warnings.Add($"Object type '{migration.ObjectTypeName}' already had Object Matching Rules, so they remain " +
                $"in effect and the rules on {migration.SyncRulesWithMatchingRules} Synchronisation Rule(s) were discarded " +
                $"rather than migrated.");
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
/// What switching to simple mode did on one Object Type.
/// </summary>
public class ObjectTypeMatchingRuleMigration
{
    /// <summary>
    /// The ID of the Connected System Object Type.
    /// </summary>
    public int ObjectTypeId { get; set; }

    /// <summary>
    /// The name of the Connected System Object Type.
    /// </summary>
    public string ObjectTypeName { get; set; } = string.Empty;

    /// <summary>
    /// How many import Synchronisation Rules the Object Type has.
    /// </summary>
    public int SyncRuleCount { get; set; }

    /// <summary>
    /// How many of them had Object Matching Rules of their own.
    /// </summary>
    public int SyncRulesWithMatchingRules { get; set; }

    /// <summary>
    /// How many different sets of rules those Synchronisation Rules had between them.
    /// </summary>
    public int UniqueSyncRuleConfigurations { get; set; }

    /// <summary>
    /// Whether the Synchronisation Rules had different rules, in which case the most common set was used.
    /// </summary>
    public bool RulesDiverged => UniqueSyncRuleConfigurations > 1;

    /// <summary>
    /// Whether the Object Type already had rules, which stay, so the Synchronisation Rules' rules were discarded
    /// rather than moved.
    /// </summary>
    public bool ObjectTypeRulesTookPrecedence { get; set; }

    /// <summary>
    /// How many rules were put on the Object Type.
    /// </summary>
    public int MatchingRulesSet { get; set; }

    /// <summary>
    /// How many Synchronisation Rules had their rules removed.
    /// </summary>
    public int SyncRulesCleared { get; set; }
}
