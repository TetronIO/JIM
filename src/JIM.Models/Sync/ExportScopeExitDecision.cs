// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;

namespace JIM.Models.Sync;

/// <summary>
/// What an export Synchronisation Rule leaving a Metaverse Object's scope does to the account it finds (#2822, #2826),
/// as <c>SyncEngine.DecideExportScopeExit</c> decides it.
/// </summary>
/// <param name="Outcome">Whether the departing rule deprovisions the account, another rule keeps it, or another rule
/// manages it.</param>
/// <param name="CoveringRule">The rule that keeps the account when <paramref name="Outcome"/> is
/// <see cref="ExportScopeExitOutcome.KeptByCoveringRule"/>; otherwise null.</param>
public sealed record ExportScopeExitDecision(ExportScopeExitOutcome Outcome, SyncRule? CoveringRule = null);
