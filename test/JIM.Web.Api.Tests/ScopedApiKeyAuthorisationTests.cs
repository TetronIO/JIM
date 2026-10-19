// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Tasks;
using JIM.Models.Core;
using JIM.Models.Security;
using JIM.Web.Authentication;
using JIM.Web.Controllers.Api;
using JIM.Web.Hubs;
using JIM.Web.Middleware.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// A SCIM client's key opens nothing in JIM but its own SCIM endpoint (#124, D3). Authentication already refuses the
/// key off that endpoint; these tests prove the second line holds on its own, so a route or handler change cannot
/// open JIM to one: the default policy refuses a scoped principal, and every controller and hub, as declared today
/// and as anyone adds one later, ends up refusing it too.
/// </summary>
[TestFixture]
public class ScopedApiKeyAuthorisationTests
{
    private IAuthorizationService _authorisation = null!;
    private IAuthorizationPolicyProvider _policies = null!;

    [SetUp]
    public void SetUp()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddJimAuthorisation();
        var provider = services.BuildServiceProvider();
        _authorisation = provider.GetRequiredService<IAuthorizationService>();
        _policies = provider.GetRequiredService<IAuthorizationPolicyProvider>();
    }

    [Test]
    public async Task DefaultPolicy_ScopedKey_RefusedAsync()
    {
        var result = await _authorisation.AuthorizeAsync(ScopedKeyPrincipal(), (await _policies.GetDefaultPolicyAsync()));

        Assert.That(result.Succeeded, Is.False);
    }

    [Test]
    public async Task DefaultPolicy_UnscopedKey_AllowedAsync()
    {
        var result = await _authorisation.AuthorizeAsync(Principal(new ApiKey { Id = Guid.NewGuid(), Name = "Automation", KeyPrefix = "jim_ak_abcd" }),
            (await _policies.GetDefaultPolicyAsync()));

        Assert.That(result.Succeeded, Is.True);
    }

    [Test]
    public async Task EveryControllerAndHub_ScopedKey_RefusedAsync()
    {
        var admitted = new List<string>();
        foreach (var (endpoint, authorizeData) in ProtectedEndpoints())
        {
            var policy = await AuthorizationPolicy.CombineAsync(_policies, authorizeData);
            Assert.That(policy, Is.Not.Null, $"{endpoint} declares no authorisation at all.");
            if ((await _authorisation.AuthorizeAsync(ScopedKeyPrincipal(), policy!)).Succeeded)
                admitted.Add(endpoint);
        }

        Assert.That(admitted, Is.Empty, "A SCIM client's key is admitted by these endpoints.");
    }

    [Test]
    public void EveryControllerAndHub_IsCoveredBySweep()
    {
        // Guards the sweep itself: if reflection stopped finding the controllers, the test above would pass on nothing.
        Assert.That(ProtectedEndpoints().Select(e => e.Endpoint), Has.Some.StartsWith(nameof(SynchronisationController)).And.Some.StartsWith(nameof(JimNotificationHub)));
    }

    /// <summary>
    /// Every controller action and hub in JIM.Web, with the authorisation declared on it and its type, leaving out
    /// the deliberately anonymous ones (health, sign-in), which admit everyone and carry nothing a key could reach.
    /// </summary>
    private static IEnumerable<(string Endpoint, IEnumerable<IAuthorizeData> AuthorizeData)> ProtectedEndpoints()
    {
        var types = typeof(HealthController).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && (typeof(ControllerBase).IsAssignableFrom(t) || typeof(Hub).IsAssignableFrom(t)));

        foreach (var type in types)
        {
            var typeData = type.GetCustomAttributes<AuthorizeAttribute>(inherit: true).Cast<IAuthorizeData>().ToList();
            var typeAnonymous = type.GetCustomAttributes(inherit: true).OfType<IAllowAnonymous>().Any();

            if (typeof(Hub).IsAssignableFrom(type))
            {
                yield return (type.Name, typeData);
                continue;
            }

            var actions = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName && m.GetCustomAttribute<NonActionAttribute>() == null);
            foreach (var action in actions)
            {
                if (typeAnonymous || action.GetCustomAttributes(inherit: true).OfType<IAllowAnonymous>().Any())
                    continue;
                yield return ($"{type.Name}.{action.Name}",
                    typeData.Concat(action.GetCustomAttributes<AuthorizeAttribute>(inherit: true)).ToList());
            }
        }
    }

    private static ClaimsPrincipal ScopedKeyPrincipal() => Principal(new ApiKey
    {
        Id = Guid.NewGuid(),
        Name = "Entra provisioning",
        KeyPrefix = "jim_ak_efgh",
        Scope = ApiKeyScope.ScimServiceProvider,
        ScopeConnectedSystemId = 7
    });

    private static ClaimsPrincipal Principal(ApiKey apiKey) =>
        new(new ClaimsIdentity(ApiKeyAuthenticationHandler.BuildApiKeyClaims(apiKey), ApiKeyAuthenticationHandler.SchemeName,
            nameType: ClaimTypes.Name, roleType: Constants.BuiltInRoles.RoleClaimType));
}
