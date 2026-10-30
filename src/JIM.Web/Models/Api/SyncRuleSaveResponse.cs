// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core.DTOs;
using JIM.Models.Logic;
using JIM.Models.Logic.DTOs;
using JIM.Models.Staging.DTOs;

namespace JIM.Web.Models.Api;

/// <summary>
/// A Synchronisation Rule as saved, with what the save raised that did not stop it.
/// </summary>
public class SyncRuleSaveResponse : SyncRuleHeader
{
    /// <summary>
    /// Non-blocking warnings the save raised about the rule's Attribute Flows and Scoping Criteria, for example that an
    /// Attribute Flow deriving a Metaverse attribute calls a function returning a different value each time it is
    /// evaluated, or that a Scoping Criterion the save left as it was is no longer valid. The save went ahead
    /// regardless. Always present; empty when there are none.
    /// </summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>
    /// The Metaverse-Derived Attribute Flows the save left with a missing input (for example by disabling the rule
    /// holding the last enabled contributor of an attribute they read). The save went ahead regardless; each flow's
    /// Missing Input Behaviour now decides what it contributes. Always present.
    /// </summary>
    public List<DependentDerivedFlow> DependentDerivedFlows { get; set; } = new();

    /// <summary>
    /// Set when the save took the rule into projecting into a Metaverse Object Type deleted When Authoritative
    /// Source Disconnected, from a Connected System that is not one of its authoritative sources: objects only this
    /// system holds will never be deleted, and those it shares with an authoritative source are deleted when that
    /// source disconnects. Null when there is nothing to warn about, including for a rule that already projected.
    /// </summary>
    public string? DeletionSourceWarning { get; set; }

    /// <summary>
    /// The Connected System Object Types this save recorded as awaiting synchronisation. For each, the Connected System's
    /// next synchronisation, Delta or Full, processes every object of the type, not only the changed ones; the entry gives
    /// the Connected System and Object Type, how many objects that is, and about how long it takes at the speed of the
    /// Connected System's last Full Synchronisation (empty when it has never completed one). Always present; empty when
    /// the save recorded nothing: a change that needs no synchronisation, a rule that is switched off, and an export rule,
    /// whose change reaches existing objects through the review it flags instead.
    /// </summary>
    public List<AwaitingSynchronisationEstimate> AwaitingSynchronisation { get; set; } = new();

    /// <summary>
    /// Builds the response from the rule as saved (reloaded) and the instance the save was performed on, which carries
    /// the save's transient warnings and dependants.
    /// </summary>
    /// <param name="saved">The rule as stored after the save.</param>
    /// <param name="savedInstance">The instance handed to the save.</param>
    /// <param name="deletionSourceWarning">The deletion source warning for the save, if any (#1256).</param>
    /// <param name="awaitingSynchronisation">What the save recorded as awaiting synchronisation, with its cost (#2615).</param>
    public static SyncRuleSaveResponse FromSave(SyncRule saved, SyncRule savedInstance,
        SyncRuleDeletionSourceWarning? deletionSourceWarning = null,
        IEnumerable<AwaitingSynchronisationEstimate>? awaitingSynchronisation = null)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(savedInstance);

        var header = FromEntity(saved);
        var response = new SyncRuleSaveResponse
        {
            Warnings = savedInstance.SaveWarnings.ToList(),
            DependentDerivedFlows = savedInstance.SaveDependentDerivedFlows.ToList(),
            DeletionSourceWarning = deletionSourceWarning?.Message,
            AwaitingSynchronisation = awaitingSynchronisation?.ToList() ?? []
        };

        // Copy every header property, so a field added to the header later reaches this response without anyone
        // having to remember it here.
        foreach (var property in typeof(SyncRuleHeader).GetProperties().Where(property => property.CanRead && property.CanWrite))
            property.SetValue(response, property.GetValue(header));

        return response;
    }
}
