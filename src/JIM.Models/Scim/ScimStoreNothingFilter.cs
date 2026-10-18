// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Scim;

/// <summary>
/// No resource: a filter that can match nothing, such as an <c>id eq</c> whose value is not a resource id.
/// </summary>
public sealed record ScimStoreNothingFilter : ScimStoreFilter
{
    private ScimStoreNothingFilter()
    {
    }

    public static ScimStoreNothingFilter Instance { get; } = new();
}
