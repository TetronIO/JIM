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
        Environment.SetEnvironmentVariable("JIM_SSO_CLIENT_ID", null);
        Environment.SetEnvironmentVariable("JIM_SSO_API_SCOPE", null);

        _controller = new AuthController();
    }

    [TearDown]
    public void TearDown()
    {
        // Clean up environment variables after each test
        Environment.SetEnvironmentVariable("JIM_SSO_AUTHORITY", null);
        Environment.SetEnvironmentVariable("JIM_SSO_CLIENT_ID", null);
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
        Environment.SetEnvironmentVariable("JIM_SSO_AUTHORITY", "https://login.example.com");
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
        Environment.SetEnvironmentVariable("JIM_SSO_AUTHORITY", "https://login.example.com");
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
        var authority = "https://login.example.com";
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
        Environment.SetEnvironmentVariable("JIM_SSO_AUTHORITY", "https://login.example.com");
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
        Environment.SetEnvironmentVariable("JIM_SSO_AUTHORITY", "https://login.example.com");
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
        Environment.SetEnvironmentVariable("JIM_SSO_AUTHORITY", "https://login.example.com");
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
        Environment.SetEnvironmentVariable("JIM_SSO_AUTHORITY", "https://login.example.com");
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
        Environment.SetEnvironmentVariable("JIM_SSO_AUTHORITY", "https://login.example.com");
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
        Environment.SetEnvironmentVariable("JIM_SSO_AUTHORITY", "https://login.example.com");
        Environment.SetEnvironmentVariable("JIM_SSO_CLIENT_ID", "test-client-id");

        // Act
        var result = _controller.GetConfig() as OkObjectResult;
        var config = result?.Value as AuthConfigResponse;

        // Assert
        Assert.That(config, Is.Not.Null);
        Assert.That(config!.CodeChallengeMethod, Is.EqualTo("S256"));
    }

    #endregion
}
