// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.History;

namespace JIM.Web.Models;

/// <summary>
/// One attribute condition as the Object Changes page edits it. A row with no attribute chosen yet is still being
/// written, and is left out of the search and the URL.
/// </summary>
public class ObjectChangeConditionRow
{
    /// <summary>
    /// Identifies the row across renders, so removing one row never hands its editors' state to the next.
    /// </summary>
    public Guid Key { get; } = Guid.NewGuid();

    public int? AttributeId { get; set; }

    public AttributeChangeOperator Operator { get; set; } = AttributeChangeOperator.ChangedTo;

    public string? Value { get; set; }
}
