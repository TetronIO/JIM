// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core.DTOs;

/// <summary>
/// The origin of a Metaverse Object attribute value (#399): a Connected System through a Synchronisation Rule,
/// JIM itself, a person, or not recorded. A record so that equal origins compare equal, which is what grouping by
/// source relies on.
/// </summary>
public record ValueOrigin
{
    public ValueOriginKind Kind { get; init; }

    /// <summary>The contributing Connected System. Kept when the Synchronisation Rule has since been deleted.</summary>
    public int? ConnectedSystemId { get; init; }

    public string? ConnectedSystemName { get; init; }

    /// <summary>The contributing Synchronisation Rule; null when it has since been deleted (see <see cref="SyncRuleDeleted"/>).</summary>
    public int? SyncRuleId { get; init; }

    public string? SyncRuleName { get; init; }

    /// <summary>True when a Connected System is recorded but its contributing Synchronisation Rule no longer exists.</summary>
    public bool SyncRuleDeleted { get; init; }

    /// <summary>True when the contributor positively asserted "no value" (an asserted-null row).</summary>
    public bool AssertsNoValue { get; init; }

    /// <summary>True when JIM corrected a generated value after a collision (#242). Always false until #242 lands.</summary>
    public bool Corrected { get; init; }

    /// <summary>The person who set the value (#614). Null until #614 lands.</summary>
    public Guid? PersonId { get; init; }

    public string? PersonName { get; init; }

    public static ValueOrigin NotRecorded { get; } = new() { Kind = ValueOriginKind.NotRecorded };
}
