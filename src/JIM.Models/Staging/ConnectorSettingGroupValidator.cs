// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// Validates ConnectorSetting.RequiredGroup constraints: when one or more settings declare the same RequiredGroup,
/// at least one of them must have a value supplied by the administrator. When the group's cardinality is
/// ExactlyOne, supplying more than one value is also rejected (mutually exclusive settings).
/// Used by the application layer when validating Connected System settings, and by the UI for live form feedback.
/// </summary>
public static class ConnectorSettingGroupValidator
{
    /// <summary>
    /// Validates all setting groups, returning one validation failure per unsatisfied group.
    /// A group fails when no member has a value, or (for ExactlyOne groups) when more than one member has a value.
    /// Groups that are satisfied, and settings without a RequiredGroup, produce no results.
    /// </summary>
    public static List<ConnectorSettingValueValidationResult> Validate(List<ConnectedSystemSettingValue> settingValues)
    {
        var results = new List<ConnectorSettingValueValidationResult>();
        var groups = settingValues
            .Where(sv => !string.IsNullOrEmpty(sv.Setting.RequiredGroup))
            .GroupBy(sv => sv.Setting.RequiredGroup!);

        foreach (var group in groups)
        {
            var members = group.ToList();
            var suppliedCount = members.Count(sv => sv.HasUserSuppliedValue());
            var cardinality = GetGroupCardinality(members);

            if (suppliedCount == 0)
            {
                results.Add(new ConnectorSettingValueValidationResult
                {
                    IsValid = false,
                    ErrorMessage = BuildGroupErrorMessage(members, cardinality)
                });
            }
            else if (cardinality == ConnectorSettingRequiredGroupCardinality.ExactlyOne && suppliedCount > 1)
            {
                results.Add(new ConnectorSettingValueValidationResult
                {
                    IsValid = false,
                    ErrorMessage = BuildExclusiveGroupErrorMessage(members)
                });
            }
        }

        return results;
    }

    /// <summary>
    /// Determines whether the named group's requirement is met by the supplied values.
    /// For AtLeastOne groups, at least one member must have a value. For ExactlyOne groups, exactly one must.
    /// Returns true if no settings belong to the group, as there is nothing to satisfy.
    /// </summary>
    public static bool IsGroupSatisfied(IEnumerable<ConnectedSystemSettingValue> settingValues, string requiredGroup)
    {
        var members = settingValues.Where(sv => sv.Setting.RequiredGroup == requiredGroup).ToList();
        if (members.Count == 0)
            return true;

        var suppliedCount = members.Count(sv => sv.HasUserSuppliedValue());
        return GetGroupCardinality(members) == ConnectorSettingRequiredGroupCardinality.ExactlyOne
            ? suppliedCount == 1
            : suppliedCount >= 1;
    }

    /// <summary>
    /// Resolves the cardinality for a group. Members should all declare the same cardinality; if any member
    /// declares ExactlyOne, the group is treated as ExactlyOne (the stricter constraint).
    /// </summary>
    public static ConnectorSettingRequiredGroupCardinality GetGroupCardinality(IEnumerable<ConnectedSystemSettingValue> groupMembers)
    {
        return groupMembers.Any(sv => sv.Setting.RequiredGroupCardinality == ConnectorSettingRequiredGroupCardinality.ExactlyOne)
            ? ConnectorSettingRequiredGroupCardinality.ExactlyOne
            : ConnectorSettingRequiredGroupCardinality.AtLeastOne;
    }

    /// <summary>
    /// Builds the administrator-facing error message for a group with no value supplied, listing the member setting names.
    /// The quantifier reflects the group's cardinality ("at least one" or "exactly one").
    /// </summary>
    public static string BuildGroupErrorMessage(IEnumerable<ConnectedSystemSettingValue> groupMembers, ConnectorSettingRequiredGroupCardinality cardinality = ConnectorSettingRequiredGroupCardinality.AtLeastOne)
    {
        var settingNames = string.Join(", ", groupMembers.Select(sv => $"'{sv.Setting.Name}'"));
        var quantifier = cardinality == ConnectorSettingRequiredGroupCardinality.ExactlyOne ? "exactly one" : "at least one";
        return $"Provide a value for {quantifier} of these settings: {settingNames}.";
    }

    /// <summary>
    /// Builds the administrator-facing error message for a mutually exclusive group where more than one value was supplied.
    /// </summary>
    public static string BuildExclusiveGroupErrorMessage(IEnumerable<ConnectedSystemSettingValue> groupMembers)
    {
        var settingNames = string.Join(", ", groupMembers.Select(sv => $"'{sv.Setting.Name}'"));
        return $"Provide a value for only one of these settings, not more than one: {settingNames}.";
    }
}
