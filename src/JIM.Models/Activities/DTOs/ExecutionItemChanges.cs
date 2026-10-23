// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core.DTOs;
using JIM.Models.Staging.DTOs;

namespace JIM.Models.Activities.DTOs;

/// <summary>
/// The attribute changes recorded with one Run Profile Execution Item, in the shapes object change history uses.
/// </summary>
/// <remarks>
/// Read by item rather than by object (#2033), so a change whose object has since been deleted is still reachable.
/// </remarks>
public class ExecutionItemChanges
{
    /// <summary>
    /// The change a synchronisation made to the Metaverse Object; null when the item recorded none (an import or
    /// export, or a synchronisation that changed nothing on the Metaverse Object).
    /// </summary>
    public MvoChangeHistoryDto? MetaverseObjectChange { get; set; }

    /// <summary>
    /// The change the item recorded on a Connected System Object: what an import or export did to it, or the object a
    /// synchronisation provisioned. Null when the item recorded none.
    /// </summary>
    public CsoChangeHistoryDto? ConnectedSystemObjectChange { get; set; }

    /// <summary>
    /// What each Pending Export a synchronisation staged would write, as recorded when it was staged. Each one's
    /// <c>id</c> is the <c>connectedSystemObjectChangeId</c> of the outcome that staged it. Empty when the item staged
    /// none.
    /// </summary>
    public List<CsoChangeHistoryDto> StagedExportChanges { get; set; } = [];
}
