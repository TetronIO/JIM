// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Sync;

namespace JIM.Web.Causality;

/// <summary>
/// Everything the causality panel needs for one execution item, as <see cref="CausalityItemLoader"/> reads it (#2100):
/// the item page and the Causality Explorer each render an item's panel from one of these.
/// </summary>
/// <remarks>
/// What the item went on to cause is not here. It is read after the panel first renders, so the panel never waits on
/// it, and read again when someone asks to check; each host owns that read.
/// </remarks>
public sealed class CausalityItemData
{
    private CausalityPageContext? _context;

    public required ActivityRunProfileExecutionItem Item { get; init; }

    public Activity? Activity { get; init; }

    /// <summary>
    /// The Connected System the item's record belongs to; see <see cref="CausalityPageContext.CsoConnectedSystemId"/>.
    /// </summary>
    public ConnectedSystemHeader? RecordConnectedSystem { get; init; }

    /// <summary>
    /// The Connected System the run executed against; see <see cref="CausalityPageContext.ConnectedSystemId"/>.
    /// </summary>
    public ConnectedSystemHeader? RunConnectedSystem { get; init; }

    public ConnectedSystemObjectAttributeValue? ExternalIdAttributeValue { get; init; }

    /// <summary>
    /// The decision-time deletion policy recorded on the item (#119), where one was captured.
    /// </summary>
    public MvoDeletionPolicySnapshot? DeletionPolicySnapshot { get; init; }

    /// <summary>
    /// The Pending Exports this item created that are still queued; see <c>CausalityPanel.LivePendingExportIds</c>.
    /// </summary>
    public IReadOnlySet<Guid> LivePendingExportIds { get; init; } = new HashSet<Guid>();

    /// <summary>
    /// The current label of every Connected System Object this item's tree provisioned; see
    /// <see cref="CausalityPageContext.ConnectedSystemObjectNames"/>.
    /// </summary>
    public IReadOnlyDictionary<Guid, string>? ProvisionedConnectedSystemObjectNames { get; init; }

    /// <summary>
    /// The upward causal walk (#1223), where the item recorded anything to explain.
    /// </summary>
    public CausalChain? Chain { get; init; }

    /// <summary>
    /// The Identity the item acted on, where it is still there (#1495).
    /// </summary>
    public MetaverseObjectHeader? IdentityHeader { get; init; }

    /// <summary>
    /// The Identity's id, where it was looked up and is no longer there (#1495).
    /// </summary>
    public Guid? DeletedIdentityId { get; init; }

    /// <summary>
    /// The Metaverse Object Type the item concerns, where it could be resolved.
    /// </summary>
    public MetaverseObjectType? MetaverseObjectType { get; init; }

    /// <summary>
    /// Where the panel's links go (#2100).
    /// </summary>
    public ICausalityLinks Links { get; init; } = CausalityPageLinks.Instance;

    /// <summary>
    /// When the item's run executed (UTC).
    /// </summary>
    public DateTime? Timestamp => Activity?.Executed;

    /// <summary>
    /// Whether the run was a Full or Delta Synchronisation, the only kind that evaluates a Deletion Rule.
    /// </summary>
    public bool IsSynchronisationRun => Activity?.ConnectedSystemRunType is ConnectedSystemRunType.FullSynchronisation
        or ConnectedSystemRunType.DeltaSynchronisation;

    /// <summary>
    /// The panel's page context, built once from what was read.
    /// </summary>
    public CausalityPageContext Context => _context ??= CausalityItemLoader.BuildContext(this);
}
