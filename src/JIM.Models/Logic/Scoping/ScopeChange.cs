// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic.Scoping;

/// <summary>
/// When an object's scope next changes as time passes, its values staying as they are (#2055): the first instant a
/// relative date criterion resolves differently enough to change the rule's outcome.
/// </summary>
/// <param name="AtUtc">The instant the outcome changes, in UTC.</param>
/// <param name="Outcome">The outcome from that instant.</param>
public sealed record ScopeChange(DateTime AtUtc, ScopingRuleOutcome Outcome);
