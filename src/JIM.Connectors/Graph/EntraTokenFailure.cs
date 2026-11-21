// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JIM.Connectors.Graph;

/// <summary>
/// Turns Microsoft Entra ID's refusal of a token request into a message that says which setting to fix. Entra ID
/// identifies each failure by an AADSTS error code, which is far more useful to an administrator than the status.
/// </summary>
/// <remarks>
/// Only the numeric codes and the OAuth <c>error</c> keyword are read from the body. The <c>error_description</c> is
/// never repeated: it carries trace and correlation ids and can echo request parameters, and this message reaches
/// Activity errors and logs.
/// </remarks>
internal static partial class EntraTokenFailure
{
    private const string FederatedMismatch =
        $"The workload identity's token matches no federated credential on the app registration. Under Certificates & secrets > Federated credentials, " +
        $"add one for the managed identity named in {GraphConnectorConstants.SettingManagedIdentityClientId}, for this cloud.";

    private static readonly Dictionary<int, string> KnownCodes = new()
    {
        [7000215] = $"The {GraphConnectorConstants.SettingClientSecret} is not valid. Supply the secret's Value from Certificates & secrets on the app registration, not its Secret ID.",
        [7000222] = $"The {GraphConnectorConstants.SettingClientSecret} has expired. Create a new client secret on the app registration and supply it here.",
        [700016] = $"No application with this {GraphConnectorConstants.SettingClientId} was found in the tenant. Check the {GraphConnectorConstants.SettingClientId}, and that the app registration belongs to the tenant named in {GraphConnectorConstants.SettingTenantId}.",
        [90002] = $"The tenant named in {GraphConnectorConstants.SettingTenantId} was not found. Check it, and check the {GraphConnectorConstants.SettingCloud} setting: a tenant in a national cloud is not found on the global endpoints, nor the other way round.",
        [900023] = $"The {GraphConnectorConstants.SettingTenantId} is not a valid tenant identifier. Supply the tenant's ID (a GUID) or one of its verified domain names.",
        [700027] = $"Microsoft Entra ID could not verify the assertion signed with the {GraphConnectorConstants.SettingCertificate}. Check that the same certificate is uploaded under Certificates & secrets on the app registration.",
        [700024] = "The client assertion was outside its valid time range. Check that the clock on the JIM server is correct.",
        [7000112] = "The application is disabled in the tenant. Enable its enterprise application (service principal) and try again.",
        [70021] = FederatedMismatch,
        [700211] = FederatedMismatch,
        [700212] = FederatedMismatch,
        [700213] = FederatedMismatch,
        [500011] = $"Microsoft Graph was not found in the tenant. Check that the {GraphConnectorConstants.SettingCloud} setting matches the cloud the tenant is in."
    };

    public static string Describe(HttpStatusCode statusCode, string? body)
    {
        var (error, codes) = Read(body);
        var known = codes.FirstOrDefault(KnownCodes.ContainsKey);
        if (known != 0)
            return $"Microsoft Entra ID refused the token request (AADSTS{known}). {KnownCodes[known]}";

        var detail = string.Join(", ", new[] { error }.Concat(codes.Select(c => $"AADSTS{c}")).Where(d => d != null));
        return detail.Length > 0
            ? $"Microsoft Entra ID refused the token request with HTTP {(int)statusCode} ({detail}). Check the {GraphConnectorConstants.SettingTenantId}, {GraphConnectorConstants.SettingClientId} and credential settings."
            : $"Microsoft Entra ID refused the token request with HTTP {(int)statusCode}. Check the {GraphConnectorConstants.SettingTenantId}, {GraphConnectorConstants.SettingClientId} and credential settings.";
    }

    private static (string? Error, List<int> Codes) Read(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return (null, []);

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return (null, []);

            string? error = null;
            if (document.RootElement.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.String)
            {
                var value = errorElement.GetString();
                // An OAuth error code is a lower-case keyword (RFC 6749 section 5.2); anything else is not repeated.
                if (value != null && OAuthErrorKeyword().IsMatch(value))
                    error = value;
            }

            var codes = new List<int>();
            if (document.RootElement.TryGetProperty("error_codes", out var codesElement) && codesElement.ValueKind == JsonValueKind.Array)
                codes.AddRange(codesElement.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out _)).Select(c => c.GetInt32()));

            return (error, codes);
        }
        catch (JsonException)
        {
            return (null, []);
        }
    }

    [GeneratedRegex("^[a-z_]{1,64}$")]
    private static partial Regex OAuthErrorKeyword();
}
