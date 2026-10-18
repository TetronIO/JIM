// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Interfaces;
using JIM.Web.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Both authentication handlers must actually use the sign-in back-channel. Wired wrongly, it never runs: every other
/// test passes and a customer's internal-CA identity provider is refused as though nothing had changed (#2363).
/// </summary>
[TestFixture]
public class IdentityProviderBackchannelRegistrationTests
{
    [Test]
    public void AddIdentityProviderBackchannel_OpenIdConnectHandler_UsesTheBackchannel()
    {
        using var services = NewServices();

        var options = services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(OpenIdConnectDefaults.AuthenticationScheme);

        Assert.That(options.BackchannelHttpHandler, Is.Not.Null.And.Matches<object>(IdentityProviderBackchannel.IsBackchannelHandler));
    }

    [Test]
    public void AddIdentityProviderBackchannel_JwtBearerHandler_UsesTheBackchannel()
    {
        using var services = NewServices();

        var options = services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);

        Assert.That(options.BackchannelHttpHandler, Is.Not.Null.And.Matches<object>(IdentityProviderBackchannel.IsBackchannelHandler));
    }

    private static ServiceProvider NewServices()
    {
        var collection = new ServiceCollection();
        collection.AddSingleton(new Mock<IJimApplicationFactory>().Object);
        collection.AddIdentityProviderBackchannel();
        return collection.BuildServiceProvider();
    }
}
