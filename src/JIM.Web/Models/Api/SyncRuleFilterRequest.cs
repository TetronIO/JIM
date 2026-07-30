// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;
using JIM.Models.Logic.DTOs;

namespace JIM.Web.Models.Api;

/// <summary>
/// Query-string filters for the Synchronisation Rules list endpoint.
/// <para>
/// Each facet is repeatable (for example <c>?directions=Import&amp;directions=Export</c>). Facets
/// combine with AND, values within a facet combine with OR, and an omitted facet matches
/// everything. <see cref="Search"/> narrows whatever the facets left.
/// </para>
/// </summary>
public class SyncRuleFilterRequest
{
    /// <summary>
    /// Return only Synchronisation Rules belonging to these Connected Systems.
    /// </summary>
    public List<int>? ConnectedSystemIds { get; set; }

    /// <summary>
    /// Return only Synchronisation Rules flowing in these directions (<c>Import</c> for inbound,
    /// <c>Export</c> for outbound).
    /// </summary>
    public List<SyncRuleDirection>? Directions { get; set; }

    /// <summary>
    /// Return only Synchronisation Rules performing these actions (<c>Projects</c>,
    /// <c>Provisions</c>, or <c>FlowOnly</c> for rules that create no objects).
    /// </summary>
    public List<SyncRuleActionType>? ActionTypes { get; set; }

    /// <summary>
    /// Return only Synchronisation Rules in these states (<c>Enabled</c> or <c>Disabled</c>).
    /// </summary>
    public List<SyncRuleStatus>? Statuses { get; set; }

    /// <summary>
    /// Free-text term matched case-insensitively against the Synchronisation Rule name.
    /// </summary>
    public string? Search { get; set; }

    /// <summary>
    /// Converts the request into the shared filter that every JIM surface evaluates rules against.
    /// </summary>
    public SyncRuleFilter ToFilter()
    {
        return new SyncRuleFilter
        {
            ConnectedSystemIds = ConnectedSystemIds,
            Directions = Directions,
            ActionTypes = ActionTypes,
            Statuses = Statuses,
            Search = Search
        };
    }
}
