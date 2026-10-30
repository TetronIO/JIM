// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// A narrowed filter on the Activity tab as its chip reads: which filter, and the label naming it and its values.
/// Closing the chip puts that filter back at its default (<see cref="OperationsActivityFilters.Without"/>).
/// </summary>
public sealed record ActiveFilterChip(ActivityFilterKind Kind, string Label);
