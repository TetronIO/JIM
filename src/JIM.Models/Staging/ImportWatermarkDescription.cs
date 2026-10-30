// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// A Connector's reading of one of its import watermarks: what it counts in, and where it stands.
/// </summary>
public class ImportWatermarkDescription
{
    /// <summary>
    /// What the watermark is measured in, as a short phrase, for example <c>access log</c>, <c>Update Sequence
    /// Number</c> or <c>change sequence</c>. Lower case unless it is a proper name, because it is shown after the
    /// value, as in "24 Oct 2026 21:21:13 (access log)".
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Where the watermark stands. One point for a Connector that keeps a single position for the whole Connected
    /// System; one per Object Type, or per source, for a Connector that keeps a position for each.
    /// </summary>
    public required List<ImportWatermarkPoint> Points { get; init; }
}
