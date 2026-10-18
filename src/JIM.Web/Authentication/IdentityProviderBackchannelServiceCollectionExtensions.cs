// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace JIM.Web.Authentication;

public static class IdentityProviderBackchannelServiceCollectionExtensions
{
    /// <summary>
    /// Routes the OpenID Connect and JWT bearer handlers' requests to the identity provider through
    /// <see cref="IdentityProviderBackchannel"/>. Set through options rather than inside the handlers' configuration
    /// lambdas because the back-channel is a singleton that needs the application factory.
    /// </summary>
    public static IServiceCollection AddIdentityProviderBackchannel(this IServiceCollection services)
    {
        services.AddSingleton<SignInCertificateSnapshot>();
        services.AddSingleton<IdentityProviderBackchannel>();
        services.AddOptions<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme)
            .Configure<IdentityProviderBackchannel>((options, backchannel) => options.BackchannelHttpHandler = backchannel.CreateHandler());
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IdentityProviderBackchannel>((options, backchannel) => options.BackchannelHttpHandler = backchannel.CreateHandler());
        return services;
    }
}
