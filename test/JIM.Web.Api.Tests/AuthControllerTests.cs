// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Web.Controllers.Api;
using Microsoft.AspNetCore.Mvc;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

[TestFixture]
public class AuthControllerTests
{
    private AuthController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        // Clear environment variables before each test
        Environment.SetEnvironmentVariable("JIM_SSO_AUTHORITY", null);
        Environment.SetEnvironmentVariable("JIM_SSO_PUBLIC_AUTHORITY", null);
        Environment.SetEnvironmentVariable("JIM_SSO_CLIENT_ID", null);
        Environment.SetEnvironmentVariable("JIM_SSO_PUBLIC_CLIENT_ID", null);
        Environment.SetEnvironmentVariable("JIM_SSO_API_SCOPE", null);

        _controller = new AuthController();
    }

    [TearDown]
    public void TearDown()
    {
        // Clean up environment variables after each test
        Environment.SetEnvironmentVariable("JIM_SSO_AUTHORITY", null);
        Environment.SetEnvironmentVariable("JIM_SSO_PUBLIC_AUTHORITY", null);
        Environment.SetEnvironmentVariable("JIM_SSO_CLIENT_ID", null);
        Environment.SetEnvironmentVariable("JIM_SSO_PUBLIC_CLIENT_ID", null);
        Environment.SetEnvironmentVariable("JIM_SSO_API_SCOPE", null);
    }

    #region GetConfig tests

    [Test]
    public void GetConfig_WhenSsoNotConfigured_Returns503()
    {
        // Arrange - SSO environment variables not set

        // Act
        var result = _controller.GetConfig();

        // Assert
        Assert.That(result, Is.InstanceOf<ObjectResult>());
        var objectResult = result as ObjectResult;
        Assert.That(objectResult?.StatusCode, Is.EqualTo(503));
    }

    [Test]
    public void GetConfig_WhenSsoNotConfigured_ReturnsErrorMessage()
    {
        // Arrange - SSO environment variables not set

        // Act
        var result = _controller.GetConfig() as ObjectResult;
        var value = result?.Value;

        // Assert
        Assert.That(value, Is.Not.Null);
        var errorProperty = value!.GetType().GetProperty("error");
        Assert.That(errorProperty?.GetValue(value), Is.EqualTo("sso_not_configured"));
    }

    [Test]
    public void GetConfig_WhenOnlyAuthoritySet_Returns503()
    {
        // Arrange
        Environment.SetEnvironmentVariable("JIM_SSO_AUTHORITY", "https://login.panoply.org");
        // Client ID not set

        // Act
        var result = _controller.GetConfig();

        // Assert
        Assert.That(result, Is.InstanceOf<ObjectResult>());
        var objectResult = result as ObjectResult;
        Assert.That(objectResult?.StatusCode, Is.EqualTo(503));
    }

    [Test]
    public void GetConfig_WhenOnlyClientIdSet_Returns503()
    {
        // Arrange
        Environment.SetEnvironmentVariable("JIM_SSO_CLIENT_ID", "test-client-id");
        // Authority not set

        // Act
        var result = _controller.GetConfig();

        // Assert
        Assert.That(result, Is.InstanceOf<ObjectResult>());
        var objectResult = result as ObjectResult;
        Assert.That(objectResult?.StatusCode, Is.EqualTo(503));
    }

    [Test]
    public void GetConfig_WhenSsoConfigured_ReturnsOk()
    {
        // Arrange
        Environment.SetEnvironmentVariable("JIM_SSO_AUTHORITY", "https://login.panoply.org");
        Environment.SetEnvironmentVariable("JIM_SSO_CLIENT_ID", "test-client-id");

        // Act
        var result = _controller.GetConfig();

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public void GetConfig_WhenSsoConfigured_ReturnsAuthority()
    {
        // Arrange
        var authority = "https://login.panoply.org";
        Environment.SetEnvironmentVariable("JIM_SSO_AUTHORITY", authority);
        Environment.SetEnvironmentVariable("JIM_SSO_CLIENT_ID", "test-client-id");

        // Act
        var result = _controller.GetConfig() as OkObjectResult;
        var config = result?.Value as AuthConfigResponse;

        // Assert
        Assert.That(config, Is.Not.Null);
        Assert.That(config!.Authority, Is.EqualTo(authority));
    }

    [Test]
    public void GetConfig_WhenSsoConfigured_ReturnsClientId()
    {
        // Arrange
        var clientId = "test-client-id";
        Environment.SetEnvironmentVariable("JIM_SSO_AUTHORITY", "https://login.panoply.org");
        Environment.SetEnvironmentVariable("JIM_SSO_CLIENT_ID", clientId);

        // Act
        var result = _controller.GetConfig() as OkObjectResult;
        var config = result?.Value as AuthConfigResponse;

        // Assert
        Assert.That(config, Is.Not.Null);
        Assert.That(config!.ClientId, Is.EqualTo(clientId));
    }

    [Test]
    public void GetConfig_WhenSsoConfigured_ReturnsDefaultScopes()
    {
        // Arrange
        Environment.SetEnvironmentVariable("JIM_SSO_AUTHORITY", "https://login.panoply.org");
        Environment.SetEnvironmentVariable("JIM_SSO_CLIENT_ID", "test-client-id");
        // No API scope set

        // Act
        var result = _controller.GetConfig() as OkObjectResult;
        var config = result?.Value as AuthConfigResponse;

        // Assert
        Assert.That(config, Is.Not.Null);
        Assert.That(config!.Scopes, Contains.Item("openid"));
        Assert.That(config.Scopes, Contains.Item("profile"));
        Assert.That(config.Scopes.Count, Is.EqualTo(2));
    }

    [Test]
    public void GetConfig_WhenApiScopeConfigured_IncludesApiScope()
    {
        // Arrange
        var apiScope = "api://test-client-id/access_as_user";
        Environment.SetEnvironmentVariable("JIM_SSO_AUTHORITY", "https://login.panoply.org");
        Environment.SetEnvironmentVariable("JIM_SSO_CLIENT_ID", "test-client-id");
        Environment.SetEnvironmentVariable("JIM_SSO_API_SCOPE", apiScope);

        // Act
        var result = _controller.GetConfig() as OkObjectResult;
        var config = result?.Value as AuthConfigResponse;

        // Assert
        Assert.That(config, Is.Not.Null);
        Assert.That(config!.Scopes, Contains.Item("openid"));
        Assert.That(config.Scopes, Contains.Item("profile"));
        Assert.That(config.Scopes, Contains.Item(apiScope));
        Assert.That(config.Scopes.Count, Is.EqualTo(3));
    }

    [Test]
    public void GetConfig_WhenSsoConfigured_ReturnsCodeResponseType()
    {
        // Arrange
        Environment.SetEnvironmentVariable("JIM_SSO_AUTHORITY", "https://login.panoply.org");
        Environment.SetEnvironmentVariable("JIM_SSO_CLIENT_ID", "test-client-id");

        // Act
        var result = _controller.GetConfig() as OkObjectResult;
        var config = result?.Value as AuthConfigResponse;

        // Assert
        Assert.That(config, Is.Not.Null);
        Assert.That(config!.ResponseType, Is.EqualTo("code"));
    }

    [Test]
    public void GetConfig_WhenSsoConfigured_RequiresPkce()
    {
        // Arrange
        Environment.SetEnvironmentVariable("JIM_SSO_AUTHORITY", "https://login.panoply.org");
        Environment.SetEnvironmentVariable("JIM_SSO_CLIENT_ID", "test-client-id");

        // Act
        var result = _controller.GetConfig() as OkObjectResult;
        var config = result?.Value as AuthConfigResponse;

        // Assert
        Assert.That(config, Is.Not.Null);
        Assert.That(config!.UsePkce, Is.True);
    }

    [Test]
    public void GetConfig_WhenSsoConfigured_UsesS256CodeChallengeMethod()
    {
        // Arrange
        Environment.SetEnvironmentVariable("JIM_SSO_AUTHORITY", "https://login.panoply.org");
        Environment.SetEnvironmentVariable("JIM_SSO_CLIENT_ID", "test-client-id");

        // Act
        var result = _controller.GetConfig() as OkObjectResult;
        var config = result?.Value as AuthConfigResponse;

        // Assert
        Assert.That(config, Is.Not.Null);
        Assert.That(config!.CodeChallengeMethod, Is.EqualTo("S256"));
    }

    [Test]
    public void GetConfig_WhenPublicAuthoritySet_ReturnsPublicAuthority()
    {
        // Arrange - backend authority differs from client/browser-facing authority
        // (typical dev devcontainer: jim.web reaches Keycloak via Docker DNS,
        // browsers and PowerShell on the host reach it via localhost).
        Environment.SetEnvironmentVariable("JIM_SSO_AUTHORITY", "http://jim.keycloak:8080/realms/jim");
        Environment.SetEnvironmentVariable("JIM_SSO_PUBLIC_AUTHORITY", "http://localhost:8181/realms/jim");
        Environment.SetEnvironmentVariable("JIM_SSO_CLIENT_ID", "test-client-id");

        // Act
        var result = _controller.GetConfig() as OkObjectResult;
        var config = result?.Value as AuthConfigResponse;

        // Assert
        Assert.That(config, Is.Not.Null);
        Assert.That(config!.Authority, Is.EqualTo("http://localhost:8181/realms/jim"));
    }

    [Test]
    public void GetConfig_WhenPublicAuthorityUnset_FallsBackToAuthority()
    {
        // Arrange - typical production: one public URL, JIM_SSO_PUBLIC_AUTHORITY not set
        var authority = "https://login.panoply.org";
        Environment.SetEnvironmentVariable("JIM_SSO_AUTHORITY", authority);
        Environment.SetEnvironmentVariable("JIM_SSO_CLIENT_ID", "test-client-id");

        // Act
        var result = _controller.GetConfig() as OkObjectResult;
        var config = result?.Value as AuthConfigResponse;

        // Assert
        Assert.That(config, Is.Not.Null);
        Assert.That(config!.Authority, Is.EqualTo(authority));
    }

    [Test]
    public void GetConfig_WhenPublicAuthorityEmpty_FallsBackToAuthority()
    {
        // Arrange - treat empty string the same as unset
        var authority = "https://login.panoply.org";
        Environment.SetEnvironmentVariable("JIM_SSO_AUTHORITY", authority);
        Environment.SetEnvironmentVariable("JIM_SSO_PUBLIC_AUTHORITY", "");
        Environment.SetEnvironmentVariable("JIM_SSO_CLIENT_ID", "test-client-id");

        // Act
        var result = _controller.GetConfig() as OkObjectResult;
        var config = result?.Value as AuthConfigResponse;

        // Assert
        Assert.That(config, Is.Not.Null);
        Assert.That(config!.Authority, Is.EqualTo(authority));
    }

    [Test]
    public void GetConfig_WhenOnlyPublicAuthoritySet_Returns503()
    {
        // Arrange - JIM_SSO_AUTHORITY remains the mandatory trigger; a public override
        // alone does not satisfy the "SSO is configured" precondition.
        Environment.SetEnvironmentVariable("JIM_SSO_PUBLIC_AUTHORITY", "http://localhost:8181/realms/jim");
        Environment.SetEnvironmentVariable("JIM_SSO_CLIENT_ID", "test-client-id");

        // Act
        var result = _controller.GetConfig();

        // Assert
        Assert.That(result, Is.InstanceOf<ObjectResult>());
        var objectResult = result as ObjectResult;
        Assert.That(objectResult?.StatusCode, Is.EqualTo(503));
    }

    [Test]
    public void GetConfig_WhenPublicClientIdSet_ReturnsPublicClientId()
    {
        // Arrange - public (PKCE/loopback) client is registered separately from the
        // confidential web client. Keycloak mandates this split; other IDPs allow
        // same-id or separate registrations. The returned ClientId must be the one
        // the PowerShell module presents at the IdP authorise endpoint, which must
        // match the public client's allowed loopback redirect URIs.
        Environment.SetEnvironmentVariable("JIM_SSO_AUTHORITY", "https://login.panoply.org");
        Environment.SetEnvironmentVariable("JIM_SSO_CLIENT_ID", "jim-web");
        Environment.SetEnvironmentVariable("JIM_SSO_PUBLIC_CLIENT_ID", "jim-powershell");

        // Act
        var result = _controller.GetConfig() as OkObjectResult;
        var config = result?.Value as AuthConfigResponse;

        // Assert
        Assert.That(config, Is.Not.Null);
        Assert.That(config!.ClientId, Is.EqualTo("jim-powershell"));
    }

    [Test]
    public void GetConfig_WhenPublicClientIdUnset_FallsBackToClientId()
    {
        // Arrange - IdPs that allow the web and PowerShell clients to share a
        // registration (Entra ID, AD FS) don't need the override; clients get the
        // same ID that backs the web app.
        Environment.SetEnvironmentVariable("JIM_SSO_AUTHORITY", "https://login.panoply.org");
        Environment.SetEnvironmentVariable("JIM_SSO_CLIENT_ID", "test-client-id");

        // Act
        var result = _controller.GetConfig() as OkObjectResult;
        var config = result?.Value as AuthConfigResponse;

        // Assert
        Assert.That(config, Is.Not.Null);
        Assert.That(config!.ClientId, Is.EqualTo("test-client-id"));
    }

    [Test]
    public void GetConfig_WhenPublicClientIdEmpty_FallsBackToClientId()
    {
        // Arrange - treat empty string the same as unset
        Environment.SetEnvironmentVariable("JIM_SSO_AUTHORITY", "https://login.panoply.org");
        Environment.SetEnvironmentVariable("JIM_SSO_CLIENT_ID", "test-client-id");
        Environment.SetEnvironmentVariable("JIM_SSO_PUBLIC_CLIENT_ID", "");

        // Act
        var result = _controller.GetConfig() as OkObjectResult;
        var config = result?.Value as AuthConfigResponse;

        // Assert
        Assert.That(config, Is.Not.Null);
        Assert.That(config!.ClientId, Is.EqualTo("test-client-id"));
    }

    #endregion
}
