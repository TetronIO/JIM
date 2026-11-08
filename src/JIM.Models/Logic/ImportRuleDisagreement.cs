// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;

namespace JIM.Models.Logic;

/// <summary>
/// Import Synchronisation Rules that found different Metaverse Objects for one Connected System Object (#2823). JIM never
/// chooses between them, so the object is reported as an ambiguous match and neither joined nor projected.
/// </summary>
/// <param name="Message">What each rule matched, and what to do about it.</param>
/// <param name="Candidates">One candidate per Metaverse Object the rules found, ordered by display name.</param>
public sealed record ImportRuleDisagreement(string Message, IReadOnlyList<ActivityRunProfileExecutionItemMatchCandidate> Candidates);
