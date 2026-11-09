// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;

namespace JIM.Models.Sync;

/// <summary>
/// What happens to a joined Connected System Object's join when it leaves the last import Synchronisation Rule covering
/// it, or is deleted from its Connected System (#2823). The rules that lost the object decide, and Remain Joined wins.
/// </summary>
/// <param name="Action">Whether the join is kept or broken.</param>
/// <param name="DecidingRule">The rule whose Out-of-Scope Action decided it, for attribution; null when no import rule
/// applies, in which case the action is Disconnect.</param>
public sealed record LastScopeExitDecision(InboundOutOfScopeAction Action, SyncRule? DecidingRule);
