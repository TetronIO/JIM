// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using System.Text;
using System.Text.Json;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// Access tokens and token endpoint responses shaped like Microsoft Entra ID's, for tests of what the Microsoft Graph
/// Connector reads from them. The tokens are unsigned: JIM reads its own token's claims and never validates them.
/// </summary>
internal static class GraphTestTokens
{
    public static readonly string[] AllPermissions =
    [
        "User.ReadWrite.All", "Group.ReadWrite.All",
        "User-OnPremisesSyncBehavior.ReadWrite.All", "Group-OnPremisesSyncBehavior.ReadWrite.All"
    ];

    public static string AccessToken(params string[] roles)
    {
        var claims = new Dictionary<string, object> { ["aud"] = "https://graph.microsoft.com", ["idtyp"] = "app" };
        if (roles.Length > 0)
            claims["roles"] = roles;

        return $"{Segment(new { alg = "RS256", typ = "JWT" })}.{Segment(claims)}.c2lnbmF0dXJl";
    }

    public static HttpResponseMessage TokenResponse(params string[] roles) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { token_type = "Bearer", expires_in = 3599, access_token = AccessToken(roles) }),
            Encoding.UTF8, "application/json")
    };

    public static HttpResponseMessage TokenRefusal(int errorCode) => new(HttpStatusCode.Unauthorized)
    {
        Content = new StringContent($$"""{"error":"invalid_client","error_description":"AADSTS{{errorCode}}: refused.","error_codes":[{{errorCode}}]}""",
            Encoding.UTF8, "application/json")
    };

    public static bool IsTokenRequest(HttpRequestMessage request) => request.RequestUri!.AbsolutePath.EndsWith("/oauth2/v2.0/token", StringComparison.Ordinal);

    private static string Segment(object value) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
