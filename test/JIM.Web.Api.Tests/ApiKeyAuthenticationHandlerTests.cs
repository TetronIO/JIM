// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using JIM.Application.Utilities;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Security;
using JIM.Web.Authentication;
using JIM.Web.Middleware.Api;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for <see cref="ApiKeyAuthenticationHandler"/>'s request handling: an API key is accepted only as an
/// <c>Authorization: Bearer</c> credential (issue #2088), and anything else is left to the other schemes.
/// </summary>
[TestFixture]
public class ApiKeyAuthenticationHandlerTests
{
    private const string ValidKey = "jim_ak_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string ClientIp = "203.0.113.7";

    private Mock<IApiKeyRepository> _apiKeyRepository = null!;
    private Mock<IFailedAuthenticationRecorder> _recorder = null!;

    [SetUp]
    public void SetUp()
    {
        _apiKeyRepository = new Mock<IApiKeyRepository>();
        _recorder = new Mock<IFailedAuthenticationRecorder>();
        _recorder.Setup(r => r.RecordAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(Task.CompletedTask);
    }

    [Test]
    public async Task AuthenticateAsync_KeyAsBearer_SucceedsAsTheKeyAsync()
    {
        var apiKey = StoredKey();

        var result = await AuthenticateAsync(authorization: $"Bearer {ValidKey}");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That(result.Principal!.FindFirst("auth_method")?.Value, Is.EqualTo("api_key"));
            Assert.That(result.Principal!.FindFirst("key_prefix")?.Value, Is.EqualTo(apiKey.KeyPrefix));
            VerifyNothingRecorded();
        }
    }

    [Test]
    public async Task AuthenticateAsync_KeyAsBearerWithLowerCaseScheme_SucceedsAsync()
    {
        StoredKey();

        var result = await AuthenticateAsync(authorization: $"bearer {ValidKey}");

        Assert.That(result.Succeeded, Is.True);
    }

    [Test]
    public async Task AuthenticateAsync_KeyInXApiKeyHeaderOnly_ReturnsNoResultAsync()
    {
        // X-API-Key is no longer read: a key sent there is not a credential this scheme recognises.
        StoredKey();

        var result = await AuthenticateAsync(authorization: null, xApiKey: ValidKey);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.None, Is.True);
            VerifyNothingRecorded();
        }
    }

    [Test]
    public async Task AuthenticateAsync_SsoJwtAsBearer_ReturnsNoResultAsync()
    {
        // An SSO token is the JWT bearer scheme's to validate, not this one's to reject.
        var result = await AuthenticateAsync(authorization: "Bearer eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiIxIn0.sig");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.None, Is.True);
            _apiKeyRepository.Verify(r => r.GetByHashAsync(It.IsAny<string>()), Times.Never);
            VerifyNothingRecorded();
        }
    }

    [Test]
    public async Task AuthenticateAsync_NoCredential_ReturnsNoResultAsync()
    {
        var result = await AuthenticateAsync(authorization: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.None, Is.True);
            VerifyNothingRecorded();
        }
    }

    [Test]
    public async Task AuthenticateAsync_UnknownKeyAsBearer_FailsAsync()
    {
        _apiKeyRepository.Setup(r => r.GetByHashAsync(It.IsAny<string>())).ReturnsAsync((ApiKey?)null);

        var result = await AuthenticateAsync(authorization: $"Bearer {ValidKey}");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.None, Is.False);
            Assert.That(result.Failure, Is.Not.Null);
            VerifyRecorded("API key not found", ValidKey[..12]);
        }
    }

    [Test]
    public async Task AuthenticateAsync_DisabledKeyAsBearer_FailsAsync()
    {
        var apiKey = StoredKey();
        apiKey.IsEnabled = false;

        var result = await AuthenticateAsync(authorization: $"Bearer {ValidKey}");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Failure?.Message, Is.EqualTo("API key is disabled"));
            VerifyRecorded("API key is disabled", apiKey.KeyPrefix);
        }
    }

    [Test]
    public async Task AuthenticateAsync_ExpiredKeyAsBearer_FailsAsync()
    {
        var apiKey = StoredKey();
        apiKey.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);

        var result = await AuthenticateAsync(authorization: $"Bearer {ValidKey}");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Failure?.Message, Is.EqualTo("API key has expired"));
            VerifyRecorded("API key has expired", apiKey.KeyPrefix);
        }
    }

    [Test]
    public async Task AuthenticateAsync_ScopedKeyOutsideScim_FailsAndIsRecordedAsync()
    {
        // A SCIM client's key opens its SCIM endpoint and nothing else; anywhere else it is not a credential.
        var apiKey = StoredScopedKey(connectedSystemId: 7);

        var result = await AuthenticateAsync(authorization: $"Bearer {ValidKey}", path: "/api/v1/connected-systems");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Failure, Is.Not.Null);
            VerifyRecorded("API key is scoped to the SCIM endpoint", apiKey.KeyPrefix);
        }
    }

    [Test]
    public async Task AuthenticateAsync_ScopedKeyOnScim_SucceedsWithItsScopeAndNoRolesAsync()
    {
        StoredScopedKey(connectedSystemId: 7);

        var result = await AuthenticateAsync(authorization: $"Bearer {ValidKey}", path: "/scim/v2/7/Users");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That(result.Principal!.FindFirst(Constants.BuiltInClaims.KeyScope)?.Value, Is.EqualTo(ApiKeyScopeNames.ScimServiceProviderReadWrite));
            Assert.That(result.Principal!.FindFirst(Constants.BuiltInClaims.KeyScopeConnectedSystemId)?.Value, Is.EqualTo("7"));
            Assert.That(result.Principal!.FindAll(Constants.BuiltInRoles.RoleClaimType), Is.Empty);
            VerifyNothingRecorded();
        }
    }

    [Test]
    public async Task AuthenticateAsync_ScopedKeyOnAPathMerelyStartingWithScim_FailsAsync()
    {
        // The SCIM endpoint is the /scim segment, not any path that happens to begin with those letters.
        StoredScopedKey(connectedSystemId: 7);

        var result = await AuthenticateAsync(authorization: $"Bearer {ValidKey}", path: "/scimitar");

        Assert.That(result.Succeeded, Is.False);
    }

    [Test]
    public async Task AuthenticateAsync_BareScopedKeyOnScim_SucceedsAsync()
    {
        // Some SCIM clients send the configured header value verbatim, with no scheme; tolerated on /scim only.
        StoredScopedKey(connectedSystemId: 7);

        var result = await AuthenticateAsync(authorization: ValidKey, path: "/scim/v2/7/Users");

        Assert.That(result.Succeeded, Is.True);
    }

    [Test]
    public async Task AuthenticateAsync_BareKeyOutsideScim_ReturnsNoResultAsync()
    {
        StoredKey();

        var result = await AuthenticateAsync(authorization: ValidKey, path: "/api/v1/userinfo");

        Assert.That(result.None, Is.True);
    }

    private ApiKey StoredScopedKey(int connectedSystemId)
    {
        var apiKey = StoredKey();
        apiKey.Scope = ApiKeyScope.ScimServiceProvider;
        apiKey.ScopeConnectedSystemId = connectedSystemId;
        apiKey.ExpiresAt = DateTime.UtcNow.AddDays(30);
        return apiKey;
    }

    private void VerifyRecorded(string reason, string apiKeyPrefix) =>
        _recorder.Verify(r => r.RecordAsync(FailedAuthenticationEventNames.ApiKey, reason, apiKeyPrefix, ClientIp), Times.Once);

    private void VerifyNothingRecorded() =>
        _recorder.Verify(r => r.RecordAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()),
            Times.Never);

    private ApiKey StoredKey()
    {
        var apiKey = new ApiKey
        {
            Id = Guid.NewGuid(),
            Name = "Automation",
            KeyHash = ApiKeyMaterial.Hash(ValidKey),
            KeyPrefix = ValidKey[..12],
            IsEnabled = true,
            Roles = new List<Role> { new() { Name = "Administrator" } }
        };
        _apiKeyRepository.Setup(r => r.GetByHashAsync(apiKey.KeyHash)).ReturnsAsync(apiKey);
        return apiKey;
    }

    private async Task<AuthenticateResult> AuthenticateAsync(string? authorization, string? xApiKey = null, string path = "/api/v1/userinfo")
    {
        var repository = new Mock<IRepository>();
        repository.Setup(r => r.ApiKeys).Returns(_apiKeyRepository.Object);
        // Usage recording runs on a background task against this; it need only exist.
        repository.Setup(r => r.Activity).Returns(Mock.Of<IActivityRepository>());
        var application = new JimApplication(repository.Object);

        var services = new ServiceCollection();
        services.AddSingleton(application);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var options = new Mock<IOptionsMonitor<ApiKeyAuthenticationOptions>>();
        options.Setup(o => o.Get(It.IsAny<string>())).Returns(new ApiKeyAuthenticationOptions());

        var handler = new ApiKeyAuthenticationHandler(
            options.Object, NullLoggerFactory.Instance, UrlEncoder.Default, scopeFactory, _recorder.Object);

        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = IPAddress.Parse(ClientIp);
        if (authorization != null)
            context.Request.Headers.Authorization = authorization;
        if (xApiKey != null)
            context.Request.Headers["X-API-Key"] = xApiKey;

        await handler.InitializeAsync(
            new AuthenticationScheme(ApiKeyAuthenticationHandler.SchemeName, null, typeof(ApiKeyAuthenticationHandler)),
            context);

        return await handler.AuthenticateAsync();
    }
}
