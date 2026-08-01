// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// One row of a change's numeric impact, for example "Connected System Objects: 12,405". Used by
/// <c>ConsequenceConfirmationDialog</c> where the impact is better stated as counts than as an
/// enumerated list.
/// </summary>
public sealed class ImpactCount
{
    /// <summary>
    /// What is being counted.
    /// </summary>
    public required string Label { get; init; }

    /// <summary>
    /// How many. Rendered with thousands separators.
    /// </summary>
    public required long Count { get; init; }

    /// <summary>
    /// Optional qualifier shown beside the label, for example to note that the objects counted are
    /// preserved rather than deleted.
    /// </summary>
    public string? Note { get; init; }
}
