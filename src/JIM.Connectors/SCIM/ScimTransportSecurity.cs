// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Diagnostics.CodeAnalysis;

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

    /// <summary>
    /// True when the address is absolute and either HTTPS, or HTTP to a loopback host.
    /// </summary>
    public static bool IsPermitted(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (!uri.IsAbsoluteUri)
            return false;

        return uri.Scheme == Uri.UriSchemeHttps
               || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);
    }

    /// <summary>
    /// Checks a configured address against the rule.
    /// </summary>
    /// <param name="url">The configured value; must not be empty (a missing value is reported elsewhere).</param>
    /// <param name="settingName">The setting an administrator must correct.</param>
    /// <param name="whatIsSent">What travels to this address, so the refusal says why it matters.</param>
    /// <param name="uri">The parsed address, when it is permitted.</param>
    /// <param name="errorMessage">Why the address is refused, when it is.</param>
    public static bool TryValidate(string url, string settingName, string whatIsSent, [NotNullWhen(true)] out Uri? uri, [NotNullWhen(false)] out string? errorMessage)
    {
        uri = null;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            errorMessage = $"The '{settingName}' setting is not a valid absolute URL.";
            return false;
        }

        if (!IsPermitted(parsed))
        {
            errorMessage = $"The '{settingName}' setting must use HTTPS. HTTP is only permitted for loopback addresses when testing locally, because {whatIsSent} is sent to this address.";
            return false;
        }

        uri = parsed;
        errorMessage = null;
        return true;
    }
}
