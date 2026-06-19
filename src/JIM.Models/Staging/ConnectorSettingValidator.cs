// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;

namespace JIM.Models.Staging;

/// <summary>
/// Generic, connector-agnostic validation of Connected System setting values, driven entirely by the metadata each
/// connector declares on its settings. It enforces three declarative constraints:
/// <list type="bullet">
/// <item>Required: a setting marked <see cref="ConnectorSetting.Required"/> (or made required by a satisfied
/// <see cref="ConnectorSetting.RequiredWhenSetting"/>) must have a value.</item>
/// <item>RequiredGroup: at least one (or, for ExactlyOne cardinality, exactly one) member of a named group must have a value.</item>
/// <item>RequiredWhen: a setting is only relevant (shown and required) while its controlling setting holds a given value;
/// otherwise it is hidden and ignored.</item>
/// </list>
/// Used by the application layer when validating Connected System settings, and by the UI for live form feedback
/// (field visibility, the required indicator, group captions, and the save gate).
/// </summary>
public static class ConnectorSettingValidator
{
    /// <summary>
    /// Validates all setting values against their declarative constraints, returning one failure per problem found.
    /// Settings hidden by an unsatisfied RequiredWhen condition are skipped entirely.
    /// </summary>
    public static List<ConnectorSettingValueValidationResult> Validate(List<ConnectedSystemSettingValue> settingValues)
    {
        var results = new List<ConnectorSettingValueValidationResult>();

        // required-value validation: every relevant required setting must have a value of the appropriate type
        foreach (var settingValue in settingValues.Where(sv => IsSettingRequired(settingValues, sv.Setting) && !IsRequiredValueSupplied(sv)))
        {
            results.Add(new ConnectorSettingValueValidationResult
            {
                IsValid = false,
                ErrorMessage = $"Please supply a value for {settingValue.Setting.Name}",
                SettingValue = settingValue
            });
        }

        // required-group (either/or, optionally mutually exclusive) validation
        var groups = settingValues
            .Where(sv => !string.IsNullOrEmpty(sv.Setting.RequiredGroup))
            .GroupBy(sv => sv.Setting.RequiredGroup!)
            .Select(group => group.ToList());

        foreach (var members in groups)
        {
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
    /// Determines whether a setting is currently relevant, i.e. its RequiredWhen condition is satisfied (or it declares
    /// no RequiredWhen). Irrelevant settings are hidden in the UI and ignored by validation.
    /// </summary>
    public static bool IsConditionMet(IEnumerable<ConnectedSystemSettingValue> settingValues, ConnectorSetting setting)
    {
        if (string.IsNullOrEmpty(setting.RequiredWhenSetting))
            return true;

        var controller = settingValues.FirstOrDefault(sv => sv.Setting.Name == setting.RequiredWhenSetting);
        if (controller == null)
            return false;

        return string.Equals(GetCurrentValueAsString(controller), setting.RequiredWhenValue, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Determines whether a setting must currently have a value: it is relevant and either declared Required, or made
    /// required by a satisfied RequiredWhen condition. Drives both server-side validation and the UI required indicator.
    /// </summary>
    public static bool IsSettingRequired(IEnumerable<ConnectedSystemSettingValue> settingValues, ConnectorSetting setting)
    {
        if (!IsConditionMet(settingValues, setting))
            return false;

        return setting.Required || !string.IsNullOrEmpty(setting.RequiredWhenSetting);
    }

    /// <summary>
    /// Reads a setting value's current value as a string, for comparison against a RequiredWhen trigger value.
    /// Checkbox values become "true"/"false"; integers use the invariant decimal string.
    /// </summary>
    private static string? GetCurrentValueAsString(ConnectedSystemSettingValue settingValue)
    {
        return settingValue.Setting.Type switch
        {
            ConnectedSystemSettingType.CheckBox => settingValue.CheckboxValue ? "true" : "false",
            ConnectedSystemSettingType.Integer => settingValue.IntValue?.ToString(CultureInfo.InvariantCulture),
            ConnectedSystemSettingType.StringEncrypted => settingValue.StringEncryptedValue,
            _ => settingValue.StringValue
        };
    }

    /// <summary>
    /// Determines whether a required setting has a value of the appropriate type for its setting type.
    /// Checkbox and non-input setting types (headings, labels, dividers) always count as supplied.
    /// </summary>
    private static bool IsRequiredValueSupplied(ConnectedSystemSettingValue settingValue)
    {
        return settingValue.Setting.Type switch
        {
            ConnectedSystemSettingType.Integer => settingValue.IntValue.HasValue,
            ConnectedSystemSettingType.StringEncrypted => !string.IsNullOrEmpty(settingValue.StringEncryptedValue),
            ConnectedSystemSettingType.CheckBox => true,
            ConnectedSystemSettingType.Heading => true,
            ConnectedSystemSettingType.Label => true,
            ConnectedSystemSettingType.Divider => true,
            ConnectedSystemSettingType.Text => true,
            _ => !string.IsNullOrEmpty(settingValue.StringValue)
        };
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
