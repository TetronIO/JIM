// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Expressions;

namespace JIM.Models.Logic;

/// <summary>
/// One attribute's values as a saved configuration test case gives them (#2055 Phase 4). Values are held as text in
/// their culture-invariant form (a date in ISO 8601, a number with a full stop, a reference as the id of the object
/// it refers to, Binary in base64) and typed from the schema when the case runs, so a case reads the same on every
/// server.
/// </summary>
/// <param name="Source">For an Attribute Flow or Generated Value, which side the Expression reads it from. Null for a
/// Scope or Object Matching Rule case, which read only one side.</param>
/// <param name="AttributeName">The attribute's name.</param>
/// <param name="Values">Its values; none for an attribute with no value.</param>
public sealed record ConfigurationTestInput(ExpressionInputSource? Source, string AttributeName, IReadOnlyList<string> Values);
