// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities;

/// <summary>
/// Maps every <see cref="ActivityTargetType"/> to its <see cref="ActivityTargetCategory"/> so the Activities list
/// quick-filter can expand a category into a target-type filter. The map must partition the full enum: a unit test
/// fails the build of any new target type that is not categorised here.
/// </summary>
public static class ActivityTargetTypeCategories
{
    private static readonly Dictionary<ActivityTargetType, ActivityTargetCategory> Map = new()
    {
        { ActivityTargetType.ConnectedSystem, ActivityTargetCategory.Configuration },
        { ActivityTargetType.SynchronisationRule, ActivityTargetCategory.Configuration },
        { ActivityTargetType.ObjectMatchingRule, ActivityTargetCategory.Configuration },
        { ActivityTargetType.MetaverseAttribute, ActivityTargetCategory.Configuration },
        { ActivityTargetType.MetaverseObjectType, ActivityTargetCategory.Configuration },
        { ActivityTargetType.ServiceSetting, ActivityTargetCategory.Configuration },
        { ActivityTargetType.Schedule, ActivityTargetCategory.Configuration },
        { ActivityTargetType.TrustedCertificate, ActivityTargetCategory.Configuration },
        { ActivityTargetType.ApiKey, ActivityTargetCategory.Configuration },
        { ActivityTargetType.Role, ActivityTargetCategory.Configuration },
        { ActivityTargetType.PredefinedSearch, ActivityTargetCategory.Configuration },
        { ActivityTargetType.ConnectorDefinition, ActivityTargetCategory.Configuration },
        // Example Data Sets are admin-managed configuration. Their sibling ExampleDataTemplate stays under System
        // for now because its existing activities are template-generation runs, not configuration changes; the
        // Example Data change-history increment (PRD_CONFIGURATION_CHANGE_HISTORY_COVERAGE.md) revisits that.
        { ActivityTargetType.ExampleDataSet, ActivityTargetCategory.Configuration },
        { ActivityTargetType.MetaverseObject, ActivityTargetCategory.IdentityData },
        { ActivityTargetType.ConnectedSystemRunProfile, ActivityTargetCategory.SyncRuns },
        { ActivityTargetType.TemporalScopeReconciliation, ActivityTargetCategory.SyncRuns },
        { ActivityTargetType.HistoryRetentionCleanup, ActivityTargetCategory.System },
        { ActivityTargetType.System, ActivityTargetCategory.System },
        { ActivityTargetType.SystemInitialisation, ActivityTargetCategory.System },
        { ActivityTargetType.ExampleDataTemplate, ActivityTargetCategory.System },
        { ActivityTargetType.NotSet, ActivityTargetCategory.System }
    };

    /// <summary>
    /// Gets the category a target type belongs to. Throws for a target type missing from the map, so an
    /// uncategorised new enum member fails fast (and is caught by the exhaustiveness unit test).
    /// </summary>
    public static ActivityTargetCategory GetCategory(ActivityTargetType targetType)
    {
        if (Map.TryGetValue(targetType, out var category))
            return category;
        throw new ArgumentOutOfRangeException(nameof(targetType), targetType,
            $"ActivityTargetType '{targetType}' has no category; add it to {nameof(ActivityTargetTypeCategories)}.");
    }

    /// <summary>
    /// Gets all target types belonging to a category, for expanding a category quick-filter into a target-type filter.
    /// </summary>
    public static IReadOnlyCollection<ActivityTargetType> GetTargetTypes(ActivityTargetCategory category)
    {
        return Map.Where(pair => pair.Value == category).Select(pair => pair.Key).ToList();
    }
}
