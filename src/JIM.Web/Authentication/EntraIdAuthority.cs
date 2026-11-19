// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Authentication;

/// <summary>
/// Recognises a Microsoft Entra ID authority, <c>https://login.microsoftonline.com/{tenant ID}/v2.0</c>, and gives the
/// issuer its tokens carry.
/// </summary>
/// <remarks>
/// JIM's registration with Entra ID asks for version 2 access tokens (<c>requestedAccessTokenVersion</c> 2), as Microsoft
/// recommends for an API: their audience is the application's client ID and their issuer is the tenant's version 2
/// issuer. Version 1 tokens (issuer <c>https://sts.windows.net/{tenant ID}/</c>, audience the App ID URI) are not
/// accepted (#2367).
/// </remarks>
public static class EntraIdAuthority
{
    private const string Host = "login.microsoftonline.com";

    /// <summary>
    /// Whether the authority is Entra ID's.
    /// </summary>
    public static bool IsEntraId(string? authority) =>
        Uri.TryCreate(authority, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Equals(Host, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The issuer Entra ID's version 2 tokens for the authority's tenant carry, or none when the authority names no
    /// tenant.
    /// </summary>
    public static string[] ValidIssuers(string authority)
    {
        var tenantId = new Uri(authority).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrEmpty(tenantId) ? [] : [$"https://{Host}/{tenantId}/v2.0"];
    }
}
