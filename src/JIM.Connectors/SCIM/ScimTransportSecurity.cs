// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Diagnostics.CodeAnalysis;
using JIM.Connectors.Http;
using JIM.Connectors.Resources;
using JIM.Models.Localisation;

namespace JIM.Connectors.SCIM;

/// <summary>
/// The one transport rule for every address the connector sends data or credentials to: HTTPS, or HTTP to
/// a loopback address only, so a local test service provider or authorisation server still works.
/// <para>
/// Both the Base URL and the OAuth Token Endpoint URL go through here, at configuration validation and
/// again when a client is built, so the two settings cannot drift apart. Messages name the setting and
/// never echo the URL, which may carry credentials in its user information.
/// </para>
/// </summary>
internal static class ScimTransportSecurity
{
    /// <summary>What travels to the Base URL, for the refusal message.</summary>
    public const string BaseUrlPayload = "identity data";

    /// <summary>What travels to the Token Endpoint URL (client_secret_post), for the refusal message.</summary>
    public const string TokenEndpointPayload = "the Client Secret";

    /// <summary>A well-formed Base URL, shown when the configured value does not parse.</summary>
    public const string BaseUrlExample = "https://example.com/scim/v2";

    /// <summary>
    /// True when the address is absolute and either HTTPS, or HTTP to a loopback host.
    /// </summary>
    public static bool IsPermitted(Uri uri) => HttpTransportSecurity.IsPermitted(uri);

    /// <summary>
    /// Checks a configured address against the rule.
    /// </summary>
    /// <param name="url">The configured value; must not be empty (a missing value is reported elsewhere).</param>
    /// <param name="settingName">The setting an administrator must correct.</param>
    /// <param name="whatIsSent">What travels to this address, so the refusal says why it matters.</param>
    /// <param name="uri">The parsed address, when it is permitted.</param>
    /// <param name="errorMessage">Why the address is refused, when it is.</param>
    /// <param name="exampleUrl">A well-formed value to show when the configured one does not parse, if any.</param>
    public static bool TryValidate(string url, string settingName, string whatIsSent, [NotNullWhen(true)] out Uri? uri, [NotNullWhen(false)] out string? errorMessage, string? exampleUrl = null)
    {
        var valid = TryValidateMessage(url, settingName, whatIsSent, out uri, out var detail, exampleUrl);
        errorMessage = detail?.ToEnglish();
        return valid;
    }

    /// <summary>
    /// <see cref="TryValidate"/> with the refusal held as a message, so the portal can show it in the viewer's language.
    /// </summary>
    public static bool TryValidateMessage(string url, string settingName, string whatIsSent, [NotNullWhen(true)] out Uri? uri, [NotNullWhen(false)] out JimMessage? error, string? exampleUrl = null)
    {
        uri = null;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            error = exampleUrl == null
                ? Message(nameof(ConnectorMessages.ScimUrlInvalid), settingName)
                : Message(nameof(ConnectorMessages.ScimUrlInvalidWithExample), settingName, exampleUrl);
            return false;
        }

        if (!IsPermitted(parsed))
        {
            // what is sent is one of two known phrases; anything else (a caller's own) is passed as it is
            MessageArgument payload = whatIsSent switch
            {
                BaseUrlPayload => Message(nameof(ConnectorMessages.ScimPayloadIdentityData)),
                TokenEndpointPayload => Message(nameof(ConnectorMessages.ScimPayloadClientSecret)),
                _ => whatIsSent
            };
            error = Message(nameof(ConnectorMessages.ScimUrlNotHttps), settingName, payload);
            return false;
        }

        uri = parsed;
        error = null;
        return true;
    }

    private static JimMessage Message(string key, params MessageArgument[] arguments) =>
        JimMessage.Create(ConnectorMessageCatalogues.Connectors, key, arguments);
}
