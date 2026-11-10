// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// One purpose's totals on an export run's shared context gate (#2895): how many times it took the gate, how long it
/// waited for it, and how long it held it.
/// </summary>
/// <param name="Purpose">Why the gate was taken.</param>
/// <param name="Count">How many times it was taken for this purpose.</param>
/// <param name="TotalWait">The time spent waiting for the gate, in total.</param>
/// <param name="TotalHeld">The time the gate was held, in total.</param>
public readonly record struct ExportContextGateTiming(ExportContextGatePurpose Purpose, int Count, TimeSpan TotalWait, TimeSpan TotalHeld);
