// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// One fact a collapsed SQL Object Type card shows about itself, so collapsing a card never hides what it says.
/// </summary>
/// <param name="Kind">What the chip describes.</param>
/// <param name="Text">The chip's text.</param>
public sealed record SqlObjectTypeSummaryChip(SqlObjectTypeSummaryChipKind Kind, string Text);
