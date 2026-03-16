using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Security;
using JIM.Web.Controllers.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

[TestFixture]
public class UserInfoControllerTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<ISecurityRepository> _mockSecurityRepository = null!;
    private Mock<ILogger<UserInfoController>> _mockLogger = null!;
    private JimApplication _application = null!;
    private UserInfoController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockSecurityRepository = new Mock<ISecurityRepository>();
        _mockLogger = new Mock<ILogger<UserInfoController>>();

        _mockRepository.Setup(r => r.Security).Returns(_mockSecurityRepository.Object);

        _application = new JimApplication(_mockRepository.Object);
        _controller = new UserInfoController(_mockLogger.Object, _application);
    }

    private void SetUserClaims(params Claim[] claims)
    {
        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
    }

    #region GetAsync tests - Authorised user (has MetaverseObject identity)

    [Test]
    public async Task GetAsync_WhenUserHasMetaverseObjectId_ReturnsOkResultAsync()
    {
        // Arrange
        var mvoId = Guid.NewGuid();
        SetUserClaims(
            new Claim("sub", "user@example.com"),
            new Claim("name", "Test User"),
            new Claim(Constants.BuiltInClaims.MetaverseObjectId, mvoId.ToString()),
            new Claim(Constants.BuiltInRoles.RoleClaimType, Constants.BuiltInRoles.Administrator),
            new Claim(Constants.BuiltInRoles.RoleClaimType, Constants.BuiltInRoles.User)
        );

        // Act
        var result = await _controller.GetAsync();

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task GetAsync_WhenUserHasMetaverseObjectId_ReturnsAuthorisedTrueAsync()
    {
        // Arrange
        var mvoId = Guid.NewGuid();
        SetUserClaims(
            new Claim("sub", "user@example.com"),
            new Claim("name", "Test User"),
            new Claim(Constants.BuiltInClaims.MetaverseObjectId, mvoId.ToString()),
            new Claim(Constants.BuiltInRoles.RoleClaimType, Constants.BuiltInRoles.User)
        );

        // Act
        var result = await _controller.GetAsync() as OkObjectResult;
        var value = result?.Value;

        // Assert
        Assert.That(value, Is.Not.Null);
        var authorisedProperty = value!.GetType().GetProperty("authorised");
        Assert.That(authorisedProperty?.GetValue(value), Is.True);
    }

    [Test]
    public async Task GetAsync_WhenUserHasRoles_ReturnsRolesAsync()
    {
        // Arrange
        var mvoId = Guid.NewGuid();
        SetUserClaims(
            new Claim("sub", "user@example.com"),
            new Claim("name", "Test User"),
            new Claim(Constants.BuiltInClaims.MetaverseObjectId, mvoId.ToString()),
            new Claim(Constants.BuiltInRoles.RoleClaimType, Constants.BuiltInRoles.Administrator),
            new Claim(Constants.BuiltInRoles.RoleClaimType, Constants.BuiltInRoles.User)
        );

        // Act
        var result = await _controller.GetAsync() as OkObjectResult;
        var value = result?.Value;

        // Assert
        Assert.That(value, Is.Not.Null);
        var rolesProperty = value!.GetType().GetProperty("roles");
        var roles = rolesProperty?.GetValue(value) as IEnumerable<string>;
        Assert.That(roles, Is.Not.Null);
        Assert.That(roles, Does.Contain(Constants.BuiltInRoles.Administrator));
        Assert.That(roles, Does.Contain(Constants.BuiltInRoles.User));
    }

    [Test]
    public async Task GetAsync_WhenUserHasMetaverseObjectId_ReturnsMetaverseObjectIdAsync()
    {
        // Arrange
        var mvoId = Guid.NewGuid();
        SetUserClaims(
            new Claim("sub", "user@example.com"),
            new Claim("name", "Test User"),
            new Claim(Constants.BuiltInClaims.MetaverseObjectId, mvoId.ToString()),
            new Claim(Constants.BuiltInRoles.RoleClaimType, Constants.BuiltInRoles.User)
        );

        // Act
        var result = await _controller.GetAsync() as OkObjectResult;
        var value = result?.Value;

        // Assert
        Assert.That(value, Is.Not.Null);
        var idProperty = value!.GetType().GetProperty("metaverseObjectId");
        Assert.That(idProperty?.GetValue(value)?.ToString(), Is.EqualTo(mvoId.ToString()));
    }

    [Test]
    public async Task GetAsync_WhenUserHasNameClaim_ReturnsNameAsync()
    {
        // Arrange
        var mvoId = Guid.NewGuid();
        SetUserClaims(
            new Claim("sub", "user@example.com"),
            new Claim("name", "Test User"),
            new Claim(Constants.BuiltInClaims.MetaverseObjectId, mvoId.ToString()),
            new Claim(Constants.BuiltInRoles.RoleClaimType, Constants.BuiltInRoles.User)
        );

        // Act
        var result = await _controller.GetAsync() as OkObjectResult;
        var value = result?.Value;

        // Assert
        Assert.That(value, Is.Not.Null);
        var nameProperty = value!.GetType().GetProperty("name");
        Assert.That(nameProperty?.GetValue(value), Is.EqualTo("Test User"));
    }

    [Test]
    public async Task GetAsync_WhenUserIsAdministrator_ReturnsIsAdministratorTrueAsync()
    {
        // Arrange
        var mvoId = Guid.NewGuid();
        SetUserClaims(
            new Claim("sub", "user@example.com"),
            new Claim("name", "Test User"),
            new Claim(Constants.BuiltInClaims.MetaverseObjectId, mvoId.ToString()),
            new Claim(Constants.BuiltInRoles.RoleClaimType, Constants.BuiltInRoles.Administrator),
            new Claim(Constants.BuiltInRoles.RoleClaimType, Constants.BuiltInRoles.User)
        );

        // Act
        var result = await _controller.GetAsync() as OkObjectResult;
        var value = result?.Value;

        // Assert
        Assert.That(value, Is.Not.Null);
        var isAdminProperty = value!.GetType().GetProperty("isAdministrator");
        Assert.That(isAdminProperty?.GetValue(value), Is.True);
    }

    [Test]
    public async Task GetAsync_WhenUserIsNotAdministrator_ReturnsIsAdministratorFalseAsync()
    {
        // Arrange
        var mvoId = Guid.NewGuid();
        SetUserClaims(
            new Claim("sub", "user@example.com"),
            new Claim("name", "Test User"),
            new Claim(Constants.BuiltInClaims.MetaverseObjectId, mvoId.ToString()),
            new Claim(Constants.BuiltInRoles.RoleClaimType, Constants.BuiltInRoles.User)
        );

        // Act
        var result = await _controller.GetAsync() as OkObjectResult;
        var value = result?.Value;

        // Assert
        Assert.That(value, Is.Not.Null);
        var isAdminProperty = value!.GetType().GetProperty("isAdministrator");
        Assert.That(isAdminProperty?.GetValue(value), Is.False);
    }

    #endregion

    #region GetAsync tests - Authenticated but NOT authorised (no MetaverseObject identity)

    [Test]
    public async Task GetAsync_WhenUserHasNoMetaverseObjectId_ReturnsOkResultAsync()
    {
        // Arrange - authenticated user but no JIM identity (no MetaverseObjectId claim)
        SetUserClaims(
            new Claim("sub", "user@example.com"),
            new Claim("name", "New User")
        );

        // Act
        var result = await _controller.GetAsync();

        // Assert - should still return 200 OK, not 403
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    [Test]
    public async Task GetAsync_WhenUserHasNoMetaverseObjectId_ReturnsAuthorisedFalseAsync()
    {
        // Arrange
        SetUserClaims(
            new Claim("sub", "user@example.com"),
            new Claim("name", "New User")
        );

        // Act
        var result = await _controller.GetAsync() as OkObjectResult;
        var value = result?.Value;

        // Assert
        Assert.That(value, Is.Not.Null);
        var authorisedProperty = value!.GetType().GetProperty("authorised");
        Assert.That(authorisedProperty?.GetValue(value), Is.False);
    }

    [Test]
    public async Task GetAsync_WhenUserHasNoMetaverseObjectId_ReturnsEmptyRolesAsync()
    {
        // Arrange
        SetUserClaims(
            new Claim("sub", "user@example.com"),
            new Claim("name", "New User")
        );

        // Act
        var result = await _controller.GetAsync() as OkObjectResult;
        var value = result?.Value;

        // Assert
        Assert.That(value, Is.Not.Null);
        var rolesProperty = value!.GetType().GetProperty("roles");
        var roles = rolesProperty?.GetValue(value) as IEnumerable<string>;
        Assert.That(roles, Is.Not.Null);
        Assert.That(roles, Is.Empty);
    }

    [Test]
    public async Task GetAsync_WhenUserHasNoMetaverseObjectId_ReturnsNullMetaverseObjectIdAsync()
    {
        // Arrange
        SetUserClaims(
            new Claim("sub", "user@example.com"),
            new Claim("name", "New User")
        );

        // Act
        var result = await _controller.GetAsync() as OkObjectResult;
        var value = result?.Value;

        // Assert
        Assert.That(value, Is.Not.Null);
        var idProperty = value!.GetType().GetProperty("metaverseObjectId");
        Assert.That(idProperty?.GetValue(value), Is.Null);
    }

    [Test]
    public async Task GetAsync_WhenUserHasNoMetaverseObjectId_ReturnsIsAdministratorFalseAsync()
    {
        // Arrange
        SetUserClaims(
            new Claim("sub", "user@example.com"),
            new Claim("name", "New User")
        );

        // Act
        var result = await _controller.GetAsync() as OkObjectResult;
        var value = result?.Value;

        // Assert
        Assert.That(value, Is.Not.Null);
        var isAdminProperty = value!.GetType().GetProperty("isAdministrator");
        Assert.That(isAdminProperty?.GetValue(value), Is.False);
    }

    [Test]
    public async Task GetAsync_WhenUserHasNoMetaverseObjectId_ReturnsMessageAsync()
    {
        // Arrange
        SetUserClaims(
            new Claim("sub", "user@example.com"),
            new Claim("name", "New User")
        );

        // Act
        var result = await _controller.GetAsync() as OkObjectResult;
        var value = result?.Value;

        // Assert
        Assert.That(value, Is.Not.Null);
        var messageProperty = value!.GetType().GetProperty("message");
        var message = messageProperty?.GetValue(value) as string;
        Assert.That(message, Is.Not.Null.And.Not.Empty);
        Assert.That(message, Does.Contain("sign in").IgnoreCase.Or.Contain("web portal").IgnoreCase);
    }

    #endregion

    #region GetAsync tests - Auth method detection

    [Test]
    public async Task GetAsync_WhenAuthMethodIsApiKey_ReturnsApiKeyAuthMethodAsync()
    {
        // Arrange - API key auth includes an auth_method claim
        var mvoId = Guid.NewGuid();
        SetUserClaims(
            new Claim("sub", "key-id"),
            new Claim("name", "My API Key"),
            new Claim("auth_method", "api_key"),
            new Claim(Constants.BuiltInClaims.MetaverseObjectId, mvoId.ToString()),
            new Claim(Constants.BuiltInRoles.RoleClaimType, Constants.BuiltInRoles.Administrator),
            new Claim(Constants.BuiltInRoles.RoleClaimType, Constants.BuiltInRoles.User)
        );

        // Act
        var result = await _controller.GetAsync() as OkObjectResult;
        var value = result?.Value;

        // Assert
        Assert.That(value, Is.Not.Null);
        var authMethodProperty = value!.GetType().GetProperty("authMethod");
        Assert.That(authMethodProperty?.GetValue(value), Is.EqualTo("api_key"));
    }

    [Test]
    public async Task GetAsync_WhenNoAuthMethodClaim_ReturnsOAuthAuthMethodAsync()
    {
        // Arrange - OAuth users don't have an explicit auth_method claim
        var mvoId = Guid.NewGuid();
        SetUserClaims(
            new Claim("sub", "user@example.com"),
            new Claim("name", "Test User"),
            new Claim(Constants.BuiltInClaims.MetaverseObjectId, mvoId.ToString()),
            new Claim(Constants.BuiltInRoles.RoleClaimType, Constants.BuiltInRoles.User)
        );

        // Act
        var result = await _controller.GetAsync() as OkObjectResult;
        var value = result?.Value;

        // Assert
        Assert.That(value, Is.Not.Null);
        var authMethodProperty = value!.GetType().GetProperty("authMethod");
        Assert.That(authMethodProperty?.GetValue(value), Is.EqualTo("oauth"));
    }

    #endregion
}
