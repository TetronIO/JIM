// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Models.Logic;

/// <summary>
/// The existing Connected System Object export matching found for a Metaverse Object about to be provisioned (#2052),
/// with the rule that found it and what it matched on, so the join can record both.
/// </summary>
/// <param name="ConnectedSystemObject">The Connected System Object to join instead of provisioning a new one.</param>
/// <param name="Rule">The Object Matching Rule that found it.</param>
/// <param name="MatchedValue">The looked-up values that found it, as text, separated by commas: what the join matched
/// on.</param>
public sealed record ExportMatch(ConnectedSystemObject ConnectedSystemObject, ObjectMatchingRule Rule, string MatchedValue);
