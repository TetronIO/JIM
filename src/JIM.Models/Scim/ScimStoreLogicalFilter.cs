// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Scim;

/// <summary>
/// Two filters joined by <c>and</c> or <c>or</c>.
/// </summary>
public sealed record ScimStoreLogicalFilter(ScimStoreLogicalOperator Operator, ScimStoreFilter Left, ScimStoreFilter Right) : ScimStoreFilter;
