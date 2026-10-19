// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace JIM.Web.Authentication;

public static class IdentityProviderTrustServiceCollectionExtensions
{
    /// <summary>
    /// Routes the OpenID Connect and JWT bearer handlers' requests to the identity provider through
    /// <see cref="IdentityProviderBackchannel"/>, and registers <see cref="IdentityProviderTrustMonitor"/>, which checks the
    /// identity provider in the background (#2363). The back-channel is set through options rather than inside the
    /// handlers' configuration lambdas because it is a singleton that needs the application factory.
    /// </summary>
    public static IServiceCollection AddIdentityProviderTrust(this IServiceCollection services)
    {
        services.AddSingleton<SignInCertificateSnapshot>();
        services.AddSingleton<IdentityProviderBackchannel>();
        services.AddOptions<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme)
            .Configure<IdentityProviderBackchannel>((options, backchannel) => options.BackchannelHttpHandler = backchannel.CreateHandler());
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IdentityProviderBackchannel>((options, backchannel) => options.BackchannelHttpHandler = backchannel.CreateHandler());

        services.AddSingleton<IdentityProviderTrustMonitor>();
        services.AddSingleton<IIdentityProviderTrustMonitor>(sp => sp.GetRequiredService<IdentityProviderTrustMonitor>());
        services.AddHostedService(sp => sp.GetRequiredService<IdentityProviderTrustMonitor>());
        return services;
    }
}
