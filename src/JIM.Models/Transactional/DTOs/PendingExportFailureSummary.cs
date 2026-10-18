// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional.DTOs;

/// <summary>
/// How many of a Connected System's Pending Exports are failing, and for how long the longest-failing one has been.
/// A failing Pending Export is attempted again at every export run until it succeeds (#2119), so this is what an
/// administrator investigates: the cause is usually configuration in the Connected System.
/// </summary>
/// <param name="FailingCount">How many Pending Exports' last attempt failed.</param>
/// <param name="OldestFailingSince">When the longest-failing of them began failing (UTC); null when none is failing.</param>
public sealed record PendingExportFailureSummary(int FailingCount, DateTime? OldestFailingSince);
