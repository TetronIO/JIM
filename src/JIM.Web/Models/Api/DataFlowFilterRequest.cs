// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;
using JIM.Models.Logic.DTOs;

namespace JIM.Web.Models.Api;

/// <summary>
/// Query-string filters for the Data Flow endpoint (#1199).
/// <para>
/// Every filter is optional and they combine with AND, so omitting all of them returns every attribute data flow in
/// both directions. <see cref="Search"/> narrows whatever the others left.
/// </para>
/// </summary>
public class DataFlowFilterRequest
{
    /// <summary>
    /// Return only flows in this direction (<c>Import</c> into the Metaverse, <c>Export</c> out of it). Omit for both.
    /// </summary>
    public SyncRuleDirection? Direction { get; set; }

    /// <summary>
    /// Return only flows belonging to this Connected System.
    /// </summary>
    public int? ConnectedSystemId { get; set; }

    /// <summary>
    /// Return only flows for this Connected System Object Type.
    /// </summary>
    public int? ConnectedSystemObjectTypeId { get; set; }

    /// <summary>
    /// Return only flows for this Metaverse Object Type.
    /// </summary>
    public int? MetaverseObjectTypeId { get; set; }

    /// <summary>
    /// Return only flows that read or write this Connected System attribute, whichever side it sits on for the
    /// direction in question. A flow whose Connected System side is an expression cannot match: an expression's
    /// attribute references live in its text and are not modelled. Use <see cref="Search"/> to find those.
    /// </summary>
    public int? ConnectedSystemAttributeId { get; set; }

    /// <summary>
    /// Return only flows that read or write this Metaverse Attribute, whichever side it sits on for the direction in
    /// question. Subject to the same expression limitation as <see cref="ConnectedSystemAttributeId"/>.
    /// </summary>
    public int? MetaverseAttributeId { get; set; }

    /// <summary>
    /// Return only Import flows whose target Metaverse Attribute has more than one contributor, which are the only
    /// flows whose priority order decides anything. Ignored for Export flows, which have no priority.
    /// </summary>
    public bool MultipleContributorsOnly { get; set; }

    /// <summary>
    /// Free-text term matched case-insensitively against the Synchronisation Rule, the Connected System, both object
    /// types, every attribute named on either side, and any expression text.
    /// </summary>
    public string? Search { get; set; }

    /// <summary>
    /// Converts the request into the query the application layer evaluates.
    /// </summary>
    public DataFlowQuery ToQuery()
    {
        return new DataFlowQuery
        {
            Direction = Direction,
            ConnectedSystemId = ConnectedSystemId,
            ConnectedSystemObjectTypeId = ConnectedSystemObjectTypeId,
            MetaverseObjectTypeId = MetaverseObjectTypeId,
            ConnectedSystemAttributeId = ConnectedSystemAttributeId,
            MetaverseAttributeId = MetaverseAttributeId,
            MultipleContributorsOnly = MultipleContributorsOnly,
            Search = Search
        };
    }
}
