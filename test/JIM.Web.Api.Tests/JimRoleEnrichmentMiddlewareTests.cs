// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Security;
using JIM.Web.Middleware.Api;
using Microsoft.AspNetCore.Http;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Covers <see cref="JimRoleEnrichmentMiddleware"/>: the roles an /api request carries are the ones the database gives
/// the person now, each once, whatever authentication attached before it ran (#2441).
/// </summary>
[TestFixture]
public class JimRoleEnrichmentMiddlewareTests
{
    private const string Subject = "employee-sub-0001";

    private readonly Guid _personId = Guid.NewGuid();
    private Mock<ISecurityRepository> _security = null!;
    private JimApplication _application = null!;

    [SetUp]
    public void SetUp()
    {
        var identifierAttribute = new MetaverseAttribute { Id = 1, Name = "Subject Identifier" };
        var userType = new MetaverseObjectType { Id = 1, Name = Constants.BuiltInObjectTypes.User, PluralName = "Users" };

        var serviceSettings = new Mock<IServiceSettingsRepository>();
        serviceSettings.Setup(r => r.GetServiceSettingsAsync()).ReturnsAsync(new ServiceSettings
        {
            SSOUniqueIdentifierClaimType = "sub",
            SSOUniqueIdentifierMetaverseAttribute = identifierAttribute
        });

        var metaverse = new Mock<IMetaverseRepository>();
        metaverse.Setup(r => r.GetMetaverseObjectTypeAsync(Constants.BuiltInObjectTypes.User, false)).ReturnsAsync(userType);
        metaverse.Setup(r => r.GetMetaverseObjectByTypeAndAttributeAsync(userType, identifierAttribute, Subject))
            .ReturnsAsync(new MetaverseObject { Id = _personId, Type = userType });

        _security = new Mock<ISecurityRepository>();
        _security.Setup(r => r.GetMetaverseObjectRolesAsync(_personId)).ReturnsAsync(new List<Role>());

        var repository = new Mock<IRepository>();
        repository.Setup(r => r.ServiceSettings).Returns(serviceSettings.Object);
        repository.Setup(r => r.Metaverse).Returns(metaverse.Object);
        repository.Setup(r => r.Security).Returns(_security.Object);
        _application = new JimApplication(repository.Object);
    }

    [TearDown]
    public void TearDown() => _application.Dispose();

    [Test]
    public async Task InvokeAsync_WhenAuthenticationAttachedTheJimIdentity_GivesEachRoleOnceAsync()
    {
        // The bearer token's validation (and the portal's sign-in, for its cookie) attaches the JIM identity first.
        _security.Setup(r => r.GetMetaverseObjectRolesAsync(_personId))
            .ReturnsAsync(new List<Role> { new() { Name = Constants.BuiltInRoles.Administrator } });
        var context = NewRequest(JimIdentity(Constants.BuiltInRoles.Administrator, Constants.BuiltInRoles.User));

        await new JimRoleEnrichmentMiddleware(_ => Task.CompletedTask).InvokeAsync(context, _application);

        Assert.That(Roles(context), Is.EquivalentTo(new[] { Constants.BuiltInRoles.Administrator, Constants.BuiltInRoles.User }));
        Assert.That(context.User.FindAll(Constants.BuiltInClaims.MetaverseObjectId).Select(c => c.Value), Is.EqualTo(new[] { _personId.ToString() }));
    }

    [Test]
    public async Task InvokeAsync_WhenARoleWasRemovedSinceSignIn_LeavesItOffAsync()
    {
        // The portal's cookie still carries the Administrator role it was given at sign-in; the database no longer does.
        var context = NewRequest(JimIdentity(Constants.BuiltInRoles.Administrator, Constants.BuiltInRoles.User));

        await new JimRoleEnrichmentMiddleware(_ => Task.CompletedTask).InvokeAsync(context, _application);

        Assert.That(Roles(context), Is.EqualTo(new[] { Constants.BuiltInRoles.User }));
    }

    [Test]
    public async Task InvokeAsync_WhenNoJimIdentityIsAttached_AttachesTheDatabasesRolesAsync()
    {
        var context = NewRequest();

        await new JimRoleEnrichmentMiddleware(_ => Task.CompletedTask).InvokeAsync(context, _application);

        Assert.That(Roles(context), Is.EqualTo(new[] { Constants.BuiltInRoles.User }));
        Assert.That(context.User.FindFirstValue(Constants.BuiltInClaims.MetaverseObjectId), Is.EqualTo(_personId.ToString()));
        Assert.That(context.User.FindFirstValue("sub"), Is.EqualTo(Subject), "The authenticated identity stays.");
    }

    private ClaimsIdentity JimIdentity(params string[] roles) =>
        new(roles.Select(role => new Claim(Constants.BuiltInRoles.RoleClaimType, role))
                .Append(new Claim(Constants.BuiltInClaims.MetaverseObjectId, _personId.ToString())),
            authenticationType: null, nameType: null, roleType: Constants.BuiltInRoles.RoleClaimType) { Label = "JIM" };

    private static DefaultHttpContext NewRequest(params ClaimsIdentity[] attached)
    {
        var authenticated = new ClaimsIdentity(new[] { new Claim("sub", Subject) }, authenticationType: "Bearer");
        return new DefaultHttpContext { User = new ClaimsPrincipal(new[] { authenticated }.Concat(attached)) };
    }

    private static string[] Roles(HttpContext context) =>
        context.User.FindAll(Constants.BuiltInRoles.RoleClaimType).Select(c => c.Value).ToArray();
}
