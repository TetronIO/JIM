// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.RegularExpressions;

namespace JIM.Connectors.SCIM;

/// <summary>
/// Recognises the service providers listed in <see cref="ScimKnownProvider"/> (#2094).
/// <para>
/// Keycloak serves SCIM at a fixed path under each realm, <c>/realms/&lt;realm&gt;/scim/v2</c>, and publishes no product
/// marker in its discovery documents. The path is checked twice: in the Base URL the administrator entered, and in the
/// <c>meta.location</c> Keycloak gives its own <c>/ServiceProviderConfig</c>, which it builds from its own hostname and
/// so survives a reverse proxy that serves it under some other path.
/// </para>
/// </summary>
internal static class ScimProviderRecognition
{
    private const string ServiceProviderConfigSegment = "/ServiceProviderConfig";

    private static readonly Regex KeycloakPath = new(@"/realms/[^/]+/scim/v2$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    /// <param name="baseUrl">The Connected System's Base URL setting.</param>
    /// <param name="serviceProviderConfigLocation">The <c>meta.location</c> of the provider's <c>/ServiceProviderConfig</c>, where it gave one.</param>
    public static ScimKnownProvider Recognise(string? baseUrl, string? serviceProviderConfigLocation)
    {
        return IsKeycloakPath(baseUrl) || IsKeycloakPath(serviceProviderConfigLocation)
            ? ScimKnownProvider.Keycloak
            : ScimKnownProvider.Unrecognised;
    }

    private static bool IsKeycloakPath(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith(ServiceProviderConfigSegment, StringComparison.OrdinalIgnoreCase))
            path = path[..^ServiceProviderConfigSegment.Length];

        return KeycloakPath.IsMatch(path);
    }
}
