// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Scim;

/// <summary>
/// Standard SCIM 2.0 endpoint paths, relative to a service provider's base URL (RFC 7644 section 3.2).
/// Paths are relative and never rooted, so they compose onto base URLs that include a path prefix
/// (for example <c>https://provider.example.com/scim/v2</c>).
/// </summary>
public static class ScimEndpoints
{
    public const string Users = "Users";
    public const string Groups = "Groups";
    public const string Me = "Me";

    // Discovery endpoints (RFC 7644 section 4). Note ServiceProviderConfig is singular by specification.
    public const string ServiceProviderConfig = "ServiceProviderConfig";
    public const string ResourceTypes = "ResourceTypes";
    public const string Schemas = "Schemas";

    public const string Bulk = "Bulk";

    /// <summary>
    /// The POST-based query endpoint suffix (RFC 7644 section 3.4.3), used when a filter is too long
    /// for a query string or the provider restricts GET queries.
    /// </summary>
    public const string Search = ".search";
}
