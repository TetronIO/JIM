// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities;

/// <summary>
/// How many of an Activity's Run Profile Execution Items recorded errors, broken down as far as deciding the
/// Activity's final status needs.
/// </summary>
/// <param name="TotalWithErrors">Items carrying any error.</param>
/// <param name="TotalRpeis">Every item recorded.</param>
/// <param name="TotalUnhandledErrors">Items whose error was unhandled, i.e. a fault in JIM rather than in the data.</param>
/// <param name="TotalObjectTypeChangeRefusals">Items recording an Object Type change the import refused (#2049).</param>
public readonly record struct ActivityRpeiErrorCounts(int TotalWithErrors, int TotalRpeis, int TotalUnhandledErrors, int TotalObjectTypeChangeRefusals);
