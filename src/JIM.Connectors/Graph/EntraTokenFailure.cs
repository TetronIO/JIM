// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using JIM.Connectors.Http;
using JIM.Connectors.Resources;
using JIM.Models.Localisation;

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
    private static JimMessage Message(string key, params MessageArgument[] arguments) =>
        JimMessage.Create(ConnectorMessageCatalogues.Connectors, key, arguments);

    private static readonly JimMessage FederatedMismatch =
        Message(nameof(ConnectorMessages.EntraFederatedMismatch), GraphConnectorConstants.SettingManagedIdentityClientId);

    private static readonly Dictionary<int, JimMessage> KnownCodes = new()
    {
        [7000215] = Message(nameof(ConnectorMessages.EntraSecretInvalid), GraphConnectorConstants.SettingClientSecret),
        [7000222] = Message(nameof(ConnectorMessages.EntraSecretExpired), GraphConnectorConstants.SettingClientSecret),
        [700016] = Message(nameof(ConnectorMessages.EntraApplicationNotFound), GraphConnectorConstants.SettingClientId, GraphConnectorConstants.SettingTenantId),
        [90002] = Message(nameof(ConnectorMessages.EntraTenantNotFound), GraphConnectorConstants.SettingTenantId, GraphConnectorConstants.SettingCloud),
        [900023] = Message(nameof(ConnectorMessages.EntraTenantIdInvalid), GraphConnectorConstants.SettingTenantId),
        [700027] = Message(nameof(ConnectorMessages.EntraAssertionNotVerified), GraphConnectorConstants.SettingCertificate),
        [700024] = Message(nameof(ConnectorMessages.EntraAssertionOutOfTime)),
        [7000112] = Message(nameof(ConnectorMessages.EntraApplicationDisabled)),
        [70021] = FederatedMismatch,
        [700211] = FederatedMismatch,
        [700212] = FederatedMismatch,
        [700213] = FederatedMismatch,
        [500011] = Message(nameof(ConnectorMessages.EntraGraphNotFound), GraphConnectorConstants.SettingCloud)
    };

    /// <summary>
    /// The refusals Entra ID gives while it is still applying a credential added moments ago, and how long JIM waits
    /// before each fresh token request. A run waits up to ten minutes in all: Entra ID has been seen refusing a
    /// certificate on every request from three to six minutes after it was uploaded, having accepted it in between, and a
    /// scheduled run that waits beats one that fails. A certificate that is simply wrong is refused the same way, so it
    /// too takes the ten minutes to fail, with a warning at every wait. The connection test waits half a minute, because
    /// an administrator is waiting on the form, and its message already says to try again shortly.
    /// </summary>
    internal static OAuthStillBeingAppliedRetry StillBeingAppliedRetryFor(GraphClientPurpose purpose) =>
        purpose == GraphClientPurpose.ConnectionTest ? ConnectionTestRetry : RunRetry;

    private static readonly OAuthStillBeingAppliedRetry RunRetry = new(IsStillBeingApplied,
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60),
         TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(180)]);

    private static readonly OAuthStillBeingAppliedRetry ConnectionTestRetry = new(IsStillBeingApplied,
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15)]);

    /// <summary>
    /// Whether Entra ID refused the token request only because it has not yet applied a newly uploaded certificate
    /// everywhere (AADSTS700027): it learns one unevenly, so a request can be refused for some minutes after another
    /// has succeeded. Nothing else counts, because every other refusal also means a wrong setting, and asking again
    /// would only make the administrator wait longer for the same answer.
    /// </summary>
    public static bool IsStillBeingApplied(HttpStatusCode statusCode, string? body) => Read(body).Codes.Contains(700027);

    public static string Describe(HttpStatusCode statusCode, string? body) => DescribeMessage(statusCode, body).ToEnglish();

    /// <summary>The same words as <see cref="Describe"/>, held as a message the portal shows in the viewer's language.</summary>
    public static JimMessage DescribeMessage(HttpStatusCode statusCode, string? body)
    {
        var (error, codes) = Read(body);
        var known = codes.FirstOrDefault(KnownCodes.ContainsKey);
        if (known != 0)
            return Message(nameof(ConnectorMessages.EntraRefusedKnown), known.ToString(CultureInfo.InvariantCulture), KnownCodes[known]);

        var detail = string.Join(", ", new[] { error }.Concat(codes.Select(c => $"AADSTS{c}")).Where(d => d != null));
        return detail.Length > 0
            ? Message(nameof(ConnectorMessages.EntraRefusedWithDetail), ((int)statusCode).ToString(CultureInfo.InvariantCulture), detail,
                GraphConnectorConstants.SettingTenantId, GraphConnectorConstants.SettingClientId)
            : Message(nameof(ConnectorMessages.EntraRefusedWithStatus), ((int)statusCode).ToString(CultureInfo.InvariantCulture),
                GraphConnectorConstants.SettingTenantId, GraphConnectorConstants.SettingClientId);
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
