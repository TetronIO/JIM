// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic;

/// <summary>
/// The Metaverse Object one import Synchronisation Rule's Object Matching Rules found for a Connected System Object
/// (#2823). Every in-scope rule is evaluated, so a join can be checked for rules that disagree.
/// </summary>
/// <param name="SyncRule">The import Synchronisation Rule whose matching found it.</param>
/// <param name="Match">What it found, and on what.</param>
public sealed record ImportRuleMatch(SyncRule SyncRule, ImportMatch Match);
