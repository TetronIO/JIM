// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Enums;

namespace JIM.Models.Core.DTOs;

/// <summary>
/// One raw Add or Remove value-change row for an attribute on a Metaverse Object (#399), as read from
/// <see cref="MetaverseObjectChangeAttributeValue"/>, before <see cref="ProvenanceValue"/>-shaped pairing into
/// <see cref="AttributeHistoryEntry"/> records. Kept as its own model so the pairing logic
/// (<c>JIM.Application.Utilities.ProvenanceLogic.PairAttributeHistory</c>) is a pure function the unit tests can
/// drive without a database.
/// </summary>
public class MetaverseAttributeHistoryRawEntry
{
    /// <summary>The parent <see cref="MetaverseObjectChange"/> id; rows from the same change are paired together.</summary>
    public Guid ChangeId { get; set; }

    public ValueChangeType ValueChangeType { get; set; }

    /// <summary>The value as display text; null for a value type with no representable value.</summary>
    public string? DisplayValue { get; set; }

    /// <summary>Snapshot of the contributing Synchronisation Rule at the time of the change.</summary>
    public int? SyncRuleId { get; set; }

    public string? SyncRuleName { get; set; }

    public ProvenanceChange Change { get; set; } = new();
}
