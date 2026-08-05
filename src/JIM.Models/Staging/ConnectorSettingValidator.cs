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
/// The three compose: a group only constrains the members that currently apply, so a group whose members are all
/// hidden asks for nothing, and a group's members are never required individually because the group's cardinality
/// is what decides how many of them need a value.
/// Used by the application layer when validating Connected System settings, and by the UI for live form feedback
/// (field visibility, the required indicator, group captions, and the save gate).
/// </summary>
public static class ConnectorSettingValidator
{
    /// <summary>
    /// Validates all setting values against their declarative constraints, returning one failure per problem found.
    /// Settings hidden by an unsatisfied RequiredWhen condition are skipped entirely, as are the groups they belong to
    /// when no member of the group applies.
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

        // required-group (either/or, optionally mutually exclusive) validation. Only members that currently apply are
        // considered, so a group scoped to a subset of configurations asks for nothing outside that subset, and a
        // value left behind by a member that no longer applies neither satisfies a group nor breaches an exclusion.
        var groups = settingValues
            .Where(sv => !string.IsNullOrEmpty(sv.Setting.RequiredGroup))
            .Where(sv => IsConditionMet(settingValues, sv.Setting))
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
    /// <remarks>
    /// Conditions are evaluated through the whole chain, not just one link of it: a setting whose controlling setting
    /// is itself hidden is hidden too, because the administrator can neither see nor change the value that would be
    /// steering it, so any value that setting holds is left over from an earlier configuration.
    /// </remarks>
    public static bool IsConditionMet(IEnumerable<ConnectedSystemSettingValue> settingValues, ConnectorSetting setting)
    {
        // materialise once: the chain is walked repeatedly, and the caller may hand over a deferred query
        var allSettingValues = settingValues as IList<ConnectedSystemSettingValue> ?? settingValues.ToList();
        return IsConditionMet(allSettingValues, setting, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Evaluates a setting's RequiredWhen condition, and its controlling setting's condition in turn.
    /// <paramref name="settingsBeingEvaluated"/> carries the names already visited on this walk, so a connector that
    /// declares a circular chain of conditions is reported as not met rather than recursing until the stack runs out.
    /// </summary>
    private static bool IsConditionMet(IList<ConnectedSystemSettingValue> settingValues, ConnectorSetting setting, HashSet<string> settingsBeingEvaluated)
    {
        if (string.IsNullOrEmpty(setting.RequiredWhenSetting))
            return true;

        if (!string.IsNullOrEmpty(setting.Name) && !settingsBeingEvaluated.Add(setting.Name))
            return false;

        var controller = settingValues.FirstOrDefault(sv => sv.Setting.Name == setting.RequiredWhenSetting);
        if (controller == null)
            return false;

        if (!IsConditionMet(settingValues, controller.Setting, settingsBeingEvaluated))
            return false;

        return string.Equals(GetCurrentValueAsString(controller), setting.RequiredWhenValue, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Determines whether a setting must currently have a value: it is relevant and either declared Required, or made
    /// required by a satisfied RequiredWhen condition. Drives both server-side validation and the UI required indicator.
    /// A setting belonging to a RequiredGroup is never required on its own; its group's cardinality decides how many of
    /// the group's members need a value, which is the whole point of declaring one.
    /// </summary>
    public static bool IsSettingRequired(IEnumerable<ConnectedSystemSettingValue> settingValues, ConnectorSetting setting)
    {
        if (!string.IsNullOrEmpty(setting.RequiredGroup))
            return false;

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
    /// The members of the named group that currently apply, i.e. whose RequiredWhen condition is met. This is the set
    /// every other group calculation works from, so the save gate, the group caption and the error message all
    /// describe the same settings the administrator can actually see.
    /// </summary>
    public static List<ConnectedSystemSettingValue> GetApplicableGroupMembers(IEnumerable<ConnectedSystemSettingValue> settingValues, string requiredGroup)
    {
        var allSettingValues = settingValues as IList<ConnectedSystemSettingValue> ?? settingValues.ToList();
        return allSettingValues.Where(sv => sv.Setting.RequiredGroup == requiredGroup && IsConditionMet(allSettingValues, sv.Setting)).ToList();
    }

    /// <summary>
    /// Determines whether the named group's requirement is met by the supplied values.
    /// For AtLeastOne groups, at least one applicable member must have a value. For ExactlyOne groups, exactly one must.
    /// Returns true if no applicable settings belong to the group, as there is nothing to choose between.
    /// </summary>
    public static bool IsGroupSatisfied(IEnumerable<ConnectedSystemSettingValue> settingValues, string requiredGroup)
    {
        var members = GetApplicableGroupMembers(settingValues, requiredGroup);
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
    /// The quantifier reflects the group's cardinality ("at least one" or "exactly one"). Pass the applicable members
    /// from <see cref="GetApplicableGroupMembers"/>, so the message never names a setting that is not on screen.
    /// </summary>
    public static string BuildGroupErrorMessage(IEnumerable<ConnectedSystemSettingValue> groupMembers, ConnectorSettingRequiredGroupCardinality cardinality = ConnectorSettingRequiredGroupCardinality.AtLeastOne)
    {
        var settingNames = string.Join(", ", groupMembers.Select(sv => $"'{sv.Setting.Name}'"));
        var quantifier = cardinality == ConnectorSettingRequiredGroupCardinality.ExactlyOne ? "exactly one" : "at least one";
        return $"Provide a value for {quantifier} of these settings: {settingNames}.";
    }

    /// <summary>
    /// Builds the administrator-facing error message for a mutually exclusive group where more than one value was supplied.
    /// Pass the applicable members from <see cref="GetApplicableGroupMembers"/>, so the message never names a setting
    /// that is not on screen.
    /// </summary>
    public static string BuildExclusiveGroupErrorMessage(IEnumerable<ConnectedSystemSettingValue> groupMembers)
    {
        var settingNames = string.Join(", ", groupMembers.Select(sv => $"'{sv.Setting.Name}'"));
        return $"Provide a value for only one of these settings, not more than one: {settingNames}.";
    }
}
