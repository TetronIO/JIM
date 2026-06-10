// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// Validates ConnectorSetting.RequiredGroup constraints: when one or more settings declare the same RequiredGroup,
/// at least one of them must have a value supplied by the administrator.
/// Used by the application layer when validating Connected System settings, and by the UI for live form feedback.
/// </summary>
public static class ConnectorSettingGroupValidator
{
    /// <summary>
    /// Validates all setting groups, returning one validation failure per unsatisfied group.
    /// Groups where at least one member has a value, and settings without a RequiredGroup, produce no results.
    /// </summary>
    public static List<ConnectorSettingValueValidationResult> Validate(List<ConnectedSystemSettingValue> settingValues)
    {
        var results = new List<ConnectorSettingValueValidationResult>();
        var groups = settingValues
            .Where(sv => !string.IsNullOrEmpty(sv.Setting.RequiredGroup))
            .GroupBy(sv => sv.Setting.RequiredGroup!);

        foreach (var group in groups.Where(g => !g.Any(sv => sv.HasUserSuppliedValue())))
        {
            results.Add(new ConnectorSettingValueValidationResult
            {
                IsValid = false,
                ErrorMessage = BuildGroupErrorMessage(group)
            });
        }

        return results;
    }

    /// <summary>
    /// Determines whether at least one setting in the named group has a value supplied by the administrator.
    /// Returns true if no settings belong to the group, as there is nothing to satisfy.
    /// </summary>
    public static bool IsGroupSatisfied(IEnumerable<ConnectedSystemSettingValue> settingValues, string requiredGroup)
    {
        var members = settingValues.Where(sv => sv.Setting.RequiredGroup == requiredGroup).ToList();
        return members.Count == 0 || members.Any(sv => sv.HasUserSuppliedValue());
    }

    /// <summary>
    /// Builds the administrator-facing error message for an unsatisfied group, listing the member setting names.
    /// </summary>
    public static string BuildGroupErrorMessage(IEnumerable<ConnectedSystemSettingValue> groupMembers)
    {
        var settingNames = string.Join(", ", groupMembers.Select(sv => $"'{sv.Setting.Name}'"));
        return $"Provide a value for at least one of these settings: {settingNames}.";
    }
}
