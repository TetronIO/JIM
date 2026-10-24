// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// An import's watermarks, as its Connector describes them: where the import started reading changes after, and the
/// point it reached, which the next Delta Import reads changes after.
/// </summary>
public class ImportReadRange
{
    /// <summary>
    /// The watermark the import started from. Null when there was none, such as before a Connected System's first
    /// import, or when the Connector found no watermark in what was recorded.
    /// </summary>
    public ImportWatermarkDescription? From { get; init; }

    /// <summary>
    /// The watermark the import reached. Null when the import recorded none (it failed or was cancelled first, or it
    /// withheld detected deletions), in which case the next Delta Import reads from <see cref="From"/> again.
    /// </summary>
    public ImportWatermarkDescription? To { get; init; }
}
