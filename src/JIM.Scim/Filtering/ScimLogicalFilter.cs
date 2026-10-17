// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Scim.Filtering;

/// <summary>
/// Two filters joined by <c>and</c> or <c>or</c>. A run of the same operator groups from the left, as the parser reads it.
/// </summary>
public sealed record ScimLogicalFilter(ScimLogicalOperator Operator, ScimFilter Left, ScimFilter Right) : ScimFilter;
