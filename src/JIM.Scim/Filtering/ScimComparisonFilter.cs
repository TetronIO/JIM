// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Scim.Filtering;

/// <summary>
/// An attribute compared with a value (<c>userName eq "bjensen"</c>), or tested for presence (<c>title pr</c>), when
/// <see cref="Value"/> is null.
/// </summary>
public sealed record ScimComparisonFilter(ScimAttributePath Path, ScimComparisonOperator Operator, ScimFilterValue? Value) : ScimFilter;
