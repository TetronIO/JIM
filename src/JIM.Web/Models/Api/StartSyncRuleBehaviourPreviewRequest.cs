// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;
using JIM.Models.Preview;
using JIM.Models.Staging;

namespace JIM.Web.Models.Api;

/// <summary>
/// The Synchronisation Rule behaviour toggles to preview (#1462): whether the rule runs at all, which way it runs,
/// and what it is allowed to create or correct.
/// </summary>
public class StartSyncRuleBehaviourPreviewRequest
{
    /// <summary>Whether the rule would be evaluated at all. Omitted leaves the stored value.</summary>
    public bool? Enabled { get; set; }

    /// <summary>
    /// Which way the rule would flow. Accepted so a caller can ask, and refused by the preview with a blocking
    /// finding: a saved rule's mappings and Object Matching Rules are written for the direction it has.
    /// </summary>
    public SyncRuleDirection? Direction { get; set; }

    /// <summary>Whether an in-scope object matching nothing would have a Metaverse Object created for it.</summary>
    public bool? ProjectToMetaverse { get; set; }

    /// <summary>Whether an in-scope Metaverse Object with no object in the target system would have one created.</summary>
    public bool? ProvisionToConnectedSystem { get; set; }

    /// <summary>Whether drift in the target system would still be corrected. Export rules only.</summary>
    public bool? EnforceState { get; set; }

    /// <summary>
    /// Whether drill-down rows are kept in full or capped per summary group. Counts are exact either way.
    /// </summary>
    public ConfigurationChangePreviewDeltaPersistence DeltaPersistence { get; set; } =
        ConfigurationChangePreviewDeltaPersistence.Capped;

    /// <summary>
    /// The proposal this request describes, with every omitted toggle taken from the stored rule.
    /// </summary>
    /// <remarks>
    /// An omitted toggle means "as the rule stands", so a caller proposing one change is never silently proposing
    /// a second, and the adapter is never handed an absence to interpret. Two of these are nullable on the rule
    /// itself, where null means off at synchronisation time, so they resolve rather than propagate.
    /// </remarks>
    public SyncRuleBehaviourToggleProposal ToProposal(SyncRule syncRule)
    {
        ArgumentNullException.ThrowIfNull(syncRule);

        var stored = SyncRuleBehaviourToggleProposal.FromCurrentSettings(syncRule);

        return new SyncRuleBehaviourToggleProposal(
            Enabled ?? stored.Enabled,
            Direction ?? stored.Direction,
            ProjectToMetaverse ?? stored.ProjectToMetaverse,
            ProvisionToConnectedSystem ?? stored.ProvisionToConnectedSystem,
            EnforceState ?? stored.EnforceState);
    }
}
