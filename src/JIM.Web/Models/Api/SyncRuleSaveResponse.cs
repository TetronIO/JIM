// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core.DTOs;
using JIM.Models.Logic;
using JIM.Models.Logic.DTOs;

namespace JIM.Web.Models.Api;

/// <summary>
/// The response to a Synchronisation Rule update: the rule's header, plus what the save raised that the caller should
/// know about without being refused for it.
/// </summary>
public class SyncRuleSaveResponse : SyncRuleHeader
{
    /// <summary>
    /// Non-blocking warnings the save raised about the rule's Attribute Flows, for example that an Attribute Flow
    /// deriving a Metaverse attribute calls a function returning a different value each time it is evaluated. The
    /// save went ahead regardless. Always present; empty when there are none.
    /// </summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>
    /// The Metaverse-Derived Attribute Flows the save left with a missing input (for example by disabling the rule
    /// holding the last enabled contributor of an attribute they read). The save went ahead regardless; each flow's
    /// Missing Input Behaviour now decides what it contributes. Always present; empty when the Metaverse-Derived
    /// Attribute Flows feature is off.
    /// </summary>
    public List<DependentDerivedFlow> DependentDerivedFlows { get; set; } = new();

    /// <summary>
    /// Set when the save took the rule into projecting into a Metaverse Object Type deleted When Authoritative Source
    /// Disconnected, from a Connected System that is not one of the type's authoritative sources (#1256): objects it
    /// projects that no selected source also holds will never be deleted, and those it shares with a selected source are
    /// deleted when that source disconnects. The save went ahead regardless. Null when there is nothing to warn about,
    /// including for a rule that already projected before the save.
    /// </summary>
    public string? DeletionSourceWarning { get; set; }

    /// <summary>
    /// Builds the response from the rule as saved (reloaded) and the instance the save was performed on, which carries
    /// the save's transient warnings and dependants.
    /// </summary>
    /// <param name="saved">The rule as stored after the save.</param>
    /// <param name="savedInstance">The instance handed to the save.</param>
    /// <param name="deletionSourceWarning">The deletion source warning for the save, if any (#1256).</param>
    public static SyncRuleSaveResponse FromSave(SyncRule saved, SyncRule savedInstance,
        SyncRuleDeletionSourceWarning? deletionSourceWarning = null)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(savedInstance);

        var header = FromEntity(saved);
        var response = new SyncRuleSaveResponse
        {
            Warnings = savedInstance.AttributeFlowRules.SelectMany(mapping => mapping.SaveWarnings).ToList(),
            DependentDerivedFlows = savedInstance.SaveDependentDerivedFlows.ToList(),
            DeletionSourceWarning = deletionSourceWarning?.Message
        };

        // Copy every header property, so a field added to the header later reaches this response without anyone
        // having to remember it here.
        foreach (var property in typeof(SyncRuleHeader).GetProperties().Where(property => property.CanRead && property.CanWrite))
            property.SetValue(response, property.GetValue(header));

        return response;
    }
}
