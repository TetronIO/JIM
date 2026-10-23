// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace JIM.Web.Authentication;

/// <summary>
/// How JIM follows its identity provider's token-signing keys, for both the OpenID Connect (portal) and JWT bearer
/// (<c>/api</c>) schemes, so that a key rollover needs no action in JIM (#2442).
/// </summary>
public static class IdentityProviderSigningKeys
{
    /// <summary>
    /// The least time between two early fetches of the identity provider's key set, made when a token names a key JIM
    /// has not seen. IdentityModel's default is 5 minutes, during which every token signed by a key introduced without
    /// notice is refused; 30 seconds bounds that while still stopping a stream of tokens naming made-up keys from
    /// making JIM fetch the key set on every request.
    /// </summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Turns off IdentityModel's last known good configuration for both schemes. Left on, a token whose signing key is
    /// not in the key set JIM holds is tried against the key sets JIM held over the past hour, so a key the identity
    /// provider has retired, perhaps because it was compromised, is still accepted for up to an hour after the
    /// rollover. Call it after the schemes are added: it runs after their own post-configuration, which is what creates
    /// the configuration managers.
    /// </summary>
    public static IServiceCollection AddIdentityProviderSigningKeyPolicy(this IServiceCollection services)
    {
        services.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme,
            options => StopTrustingRetiredKeys(options.ConfigurationManager));
        services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme,
            options => StopTrustingRetiredKeys(options.ConfigurationManager));
        return services;
    }

    private static void StopTrustingRetiredKeys(object? configurationManager)
    {
        if (configurationManager is BaseConfigurationManager manager)
            manager.UseLastKnownGoodConfiguration = false;
    }
}
