// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Application.Services;

/// <summary>
/// The words for refusing to deselect a Connected System Object Type an enabled Synchronisation Rule still manages
/// (#1474). The save refuses it and the schema preview reports it as Blocking; both say it in the same words, so an
/// administrator who previews first is told exactly what the save would have told them.
/// </summary>
public static class ObjectTypeDeselectionMessages
{
    /// <summary>
    /// Why the Object Type cannot be deselected, naming the rules to disable, because that is the fix.
    /// </summary>
    /// <param name="objectTypeName">The Object Type being deselected.</param>
    /// <param name="syncRuleNames">The enabled Synchronisation Rules bound to it, in the order to list them.</param>
    public static string StillManaged(string objectTypeName, IReadOnlyList<string> syncRuleNames)
    {
        var rules = string.Join(", ", syncRuleNames);
        return syncRuleNames.Count == 1
            ? $"Object Type '{objectTypeName}' cannot be deselected while an enabled Synchronisation Rule still " +
              $"manages it: {rules}. Disable that Synchronisation Rule first, or keep the Object Type selected."
            : $"Object Type '{objectTypeName}' cannot be deselected while {syncRuleNames.Count} enabled " +
              $"Synchronisation Rules still manage it: {rules}. Disable those Synchronisation Rules first, or keep " +
              "the Object Type selected.";
    }
}
