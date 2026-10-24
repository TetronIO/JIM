// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// One position an import watermark holds. Exactly one of <see cref="Number"/>, <see cref="Time"/> and
/// <see cref="Text"/> is set.
/// </summary>
public class ImportWatermarkPoint
{
    /// <summary>
    /// What this position belongs to, such as an Object Type's name, when the Connector keeps more than one. Null when
    /// it is the Connected System's only position.
    /// </summary>
    public string? Scope { get; init; }

    /// <summary>
    /// The position, when it is a number: a sequence or change number.
    /// </summary>
    public long? Number { get; init; }

    /// <summary>
    /// The position, when it is a point in time (UTC).
    /// </summary>
    public DateTime? Time { get; init; }

    /// <summary>
    /// The position, when it is neither a number nor a point in time, as the Connector renders it: for example a date
    /// a database column holds, which is compared only against that column's own values and so is not a point in time.
    /// </summary>
    public string? Text { get; init; }
}
