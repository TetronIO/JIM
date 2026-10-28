// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Serilog;

namespace JIM.Web.Authentication;

/// <summary>
/// The identity provider's authorisation and token endpoints, for the API reference's Try It sign-in. Read through the
/// configuration manager the OpenID Connect handler holds, so through the sign-in back-channel and its certificate
/// trust (#2363). Only Try It uses them, so failing to read them is never an error.
/// </summary>
public static class IdentityProviderOAuthEndpoints
{
    /// <summary>
    /// Reads the endpoints from the identity provider's configuration.
    /// </summary>
    /// <param name="configurationManager">The OpenID Connect handler's configuration manager, if it has one.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The endpoints, or null when the identity provider cannot be reached, is not trusted yet, or publishes
    /// neither.</returns>
    public static async Task<(string AuthorizationEndpoint, string TokenEndpoint)?> ReadAsync(
        IConfigurationManager<OpenIdConnectConfiguration>? configurationManager, CancellationToken cancellationToken)
    {
        if (configurationManager == null)
            return null;

        try
        {
            var configuration = await configurationManager.GetConfigurationAsync(cancellationToken);
            if (!string.IsNullOrEmpty(configuration?.AuthorizationEndpoint) && !string.IsNullOrEmpty(configuration.TokenEndpoint))
                return (configuration.AuthorizationEndpoint, configuration.TokenEndpoint);
        }
        catch (InvalidOperationException ex)
        {
            Log.Warning(ex, "Could not read the identity provider's configuration for the API reference; its sign-in uses placeholder endpoints");
        }

        return null;
    }
}
