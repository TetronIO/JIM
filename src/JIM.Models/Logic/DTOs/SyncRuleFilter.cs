// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic.DTOs;

/// <summary>
/// The facets an administrator can narrow a list of Synchronisation Rules by, plus an optional
/// free-text search term.
/// <para>
/// Facets combine with AND, values within a facet combine with OR, and an unset or empty facet
/// matches everything. The search term narrows whatever the facets left, so clearing it returns the
/// facet results rather than the full list.
/// </para>
/// <para>
/// This is the single definition of what "matching" means for a Synchronisation Rule list; the
/// portal, the REST API and PowerShell all filter through <see cref="Matches"/> so the three
/// surfaces cannot drift apart.
/// </para>
/// </summary>
public class SyncRuleFilter
{
    /// <summary>
    /// Match only Synchronisation Rules belonging to these Connected Systems.
    /// </summary>
    public IReadOnlyCollection<int>? ConnectedSystemIds { get; set; }

    /// <summary>
    /// Match only Synchronisation Rules flowing in these directions.
    /// </summary>
    public IReadOnlyCollection<SyncRuleDirection>? Directions { get; set; }

    /// <summary>
    /// Match only Synchronisation Rules performing these actions.
    /// </summary>
    public IReadOnlyCollection<SyncRuleActionType>? ActionTypes { get; set; }

    /// <summary>
    /// Match only Synchronisation Rules in these states.
    /// </summary>
    public IReadOnlyCollection<SyncRuleStatus>? Statuses { get; set; }

    /// <summary>
    /// Free-text term matched case-insensitively against the Synchronisation Rule name. Null,
    /// empty or whitespace-only terms are ignored.
    /// </summary>
    public string? Search { get; set; }

    /// <summary>
    /// True when no facet and no search term has been supplied, so every Synchronisation Rule
    /// matches. Callers can use this to skip filtering work entirely.
    /// </summary>
    public bool IsEmpty =>
        ConnectedSystemIds is not { Count: > 0 } &&
        Directions is not { Count: > 0 } &&
        ActionTypes is not { Count: > 0 } &&
        Statuses is not { Count: > 0 } &&
        string.IsNullOrWhiteSpace(Search);

    /// <summary>
    /// Determines the action a Synchronisation Rule performs from its direction and its
    /// projection/provisioning settings. Projection only applies to Import rules and provisioning
    /// only to Export rules, so a value set on the wrong direction is ignored.
    /// </summary>
    public static SyncRuleActionType GetActionType(SyncRuleHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);

        if (header.Direction == SyncRuleDirection.Import && header.ProjectToMetaverse == true)
            return SyncRuleActionType.Projects;

        if (header.Direction == SyncRuleDirection.Export && header.ProvisionToConnectedSystem == true)
            return SyncRuleActionType.Provisions;

        return SyncRuleActionType.FlowOnly;
    }

    /// <summary>
    /// Expresses a Synchronisation Rule's enabled flag as a <see cref="SyncRuleStatus"/>.
    /// </summary>
    public static SyncRuleStatus GetStatus(SyncRuleHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        return header.Enabled ? SyncRuleStatus.Enabled : SyncRuleStatus.Disabled;
    }

    /// <summary>
    /// Determines whether a Synchronisation Rule satisfies every facet and the search term.
    /// </summary>
    public bool Matches(SyncRuleHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);

        if (ConnectedSystemIds is { Count: > 0 } connectedSystemIds && !connectedSystemIds.Contains(header.ConnectedSystemId))
            return false;

        if (Directions is { Count: > 0 } directions && !directions.Contains(header.Direction))
            return false;

        if (ActionTypes is { Count: > 0 } actionTypes && !actionTypes.Contains(GetActionType(header)))
            return false;

        if (Statuses is { Count: > 0 } statuses && !statuses.Contains(GetStatus(header)))
            return false;

        if (!string.IsNullOrWhiteSpace(Search) && !header.Name.Contains(Search, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }
}
