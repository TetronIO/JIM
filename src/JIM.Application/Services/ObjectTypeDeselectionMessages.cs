// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Localisation;
using JIM.Application.Resources;
using JIM.Models.Localisation;

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
    public static string StillManaged(string objectTypeName, IReadOnlyList<string> syncRuleNames) =>
        StillManagedMessage(objectTypeName, syncRuleNames).ToEnglish();

    /// <summary>
    /// <see cref="StillManaged"/> as a message the portal can show in the viewer's language (#3030).
    /// </summary>
    /// <param name="objectTypeName">The Object Type being deselected.</param>
    /// <param name="syncRuleNames">The enabled Synchronisation Rules bound to it, in the order to list them.</param>
    public static JimMessage StillManagedMessage(string objectTypeName, IReadOnlyList<string> syncRuleNames) =>
        ConnectedSystemMessage.Counted(nameof(ConnectedSystemMessages.ObjectTypeStillManagedOne), syncRuleNames.Count,
            objectTypeName, syncRuleNames.Count, string.Join(", ", syncRuleNames));
}
