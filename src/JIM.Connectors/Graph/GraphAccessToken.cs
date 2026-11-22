// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json;

namespace JIM.Connectors.Graph;

/// <summary>
/// Reads the application permissions an access token grants, from its <c>roles</c> claim, and the directory roles the
/// app holds tenant-wide, from <c>wids</c>. JIM reads its own token to
/// tell an administrator which permission is missing before an import fails on it; it never validates the token,
/// which is Graph's job, and never logs it.
/// </summary>
internal static class GraphAccessToken
{
    /// <returns>The permissions granted, or none when the token is not a readable JWT or carries no roles.</returns>
    public static IReadOnlyList<string> ReadRoles(string accessToken) => ReadStrings(accessToken, "roles");

    /// <summary>
    /// The directory roles assigned to the app tenant-wide, as role template ids, from the token's <c>wids</c> claim. A role
    /// assigned since the token was issued, or one scoped more narrowly than the tenant, is not listed, so an absent role
    /// proves nothing.
    /// </summary>
    public static IReadOnlyList<string> ReadDirectoryRoles(string accessToken) => ReadStrings(accessToken, "wids");

    private static IReadOnlyList<string> ReadStrings(string accessToken, string claim)
    {
        var parts = accessToken.Split('.');
        if (parts.Length != 3)
            return [];

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');

            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(claim, out var values)
                || values.ValueKind != JsonValueKind.Array)
                return [];

            return values.EnumerateArray()
                .Where(r => r.ValueKind == JsonValueKind.String)
                .Select(r => r.GetString()!)
                .ToList();
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return [];
        }
    }
}
