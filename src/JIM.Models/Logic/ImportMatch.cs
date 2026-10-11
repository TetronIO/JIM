// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;

namespace JIM.Models.Logic;

/// <summary>
/// The Metaverse Object an import join found (#2052), with the rule that found it and what that rule computed, so the
/// join can record which rule matched and on what value.
/// </summary>
/// <param name="MetaverseObject">The Metaverse Object to join.</param>
/// <param name="Rule">The Object Matching Rule that found it.</param>
/// <param name="Resolution">The values the rule computed and looked up.</param>
/// <param name="MatchedValue">The looked-up values the Metaverse Object holds, as text, separated by commas: what the
/// join matched on.</param>
public sealed record ImportMatch(MetaverseObject MetaverseObject, ObjectMatchingRule Rule, MatchValueResolution Resolution, string MatchedValue);
