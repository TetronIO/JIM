// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Application.Services;

/// <summary>
/// Run Profile Safeguards (#1618): validates a Run Profile's Max creates/updates/deletes and detected-deletion
/// limits, and Allow Object Type Changes (#2049).
/// Mirrors the existing Verification Mode rule (<c>VerifyImportContentHashes</c>): a value that only
/// makes sense on one Run Type is rejected on every surface with a message naming the field.
/// </summary>
public static class RunProfileSafeguardsValidator
{
    /// <summary>
    /// Validates the Run-Type-specific limits and settings on <paramref name="runProfile"/>.
    /// </summary>
    /// <returns>
    /// Null when the Run Profile's limits are valid; otherwise a message naming the first offending
    /// field, suitable for an <see cref="ArgumentException"/> or a 400 response.
    /// </returns>
    public static string? Validate(ConnectedSystemRunProfile runProfile)
    {
        ArgumentNullException.ThrowIfNull(runProfile);

        return ValidateField(nameof(runProfile.MaxCreates), runProfile.MaxCreates, runProfile.RunType, ConnectedSystemRunType.Export, "an Export")
            ?? ValidateField(nameof(runProfile.MaxUpdates), runProfile.MaxUpdates, runProfile.RunType, ConnectedSystemRunType.Export, "an Export")
            ?? ValidateField(nameof(runProfile.MaxDeletes), runProfile.MaxDeletes, runProfile.RunType, ConnectedSystemRunType.Export, "an Export")
            ?? ValidateField(nameof(runProfile.MaxDetectedDeletions), runProfile.MaxDetectedDeletions, runProfile.RunType, ConnectedSystemRunType.FullImport, "a Full Import")
            ?? ValidatePercentField(runProfile.MaxDetectedDeletionsPercent, runProfile.RunType)
            ?? ValidateAllowObjectTypeChanges(runProfile.AllowObjectTypeChanges, runProfile.RunType);
    }

    /// <summary>
    /// Allow Object Type Changes (#2049) is a Full Import setting: its effect, retiring an object of the old Object
    /// Type, comes from the Full Import's deletion detection, which no other run performs.
    /// </summary>
    private static string? ValidateAllowObjectTypeChanges(bool allowObjectTypeChanges, ConnectedSystemRunType runType) =>
        allowObjectTypeChanges && runType != ConnectedSystemRunType.FullImport
            ? $"{nameof(ConnectedSystemRunProfile.AllowObjectTypeChanges)} can only be enabled on a Full Import Run Profile."
            : null;

    private static string? ValidateField(string fieldName, int? value, ConnectedSystemRunType runType, ConnectedSystemRunType requiredRunType, string requiredRunTypeArticleAndName)
    {
        if (value == null)
            return null;

        if (runType != requiredRunType)
            return $"{fieldName} can only be set on {requiredRunTypeArticleAndName} Run Profile.";

        if (value < 0)
            return $"{fieldName} cannot be negative.";

        return null;
    }

    private static string? ValidatePercentField(int? value, ConnectedSystemRunType runType)
    {
        const string fieldName = nameof(ConnectedSystemRunProfile.MaxDetectedDeletionsPercent);

        if (value == null)
            return null;

        if (runType != ConnectedSystemRunType.FullImport)
            return $"{fieldName} can only be set on a Full Import Run Profile.";

        if (value is < 0 or > 100)
            return $"{fieldName} must be between 0 and 100.";

        return null;
    }
}
