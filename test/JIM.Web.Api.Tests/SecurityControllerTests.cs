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
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Security;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

[TestFixture]
public class SecurityControllerTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<ISecurityRepository> _mockSecurityRepo = null!;
    private Mock<IMetaverseRepository> _mockMetaverseRepo = null!;
    private Mock<IActivityRepository> _mockActivityRepo = null!;
    private Mock<IApiKeyRepository> _mockApiKeyRepo = null!;
    private Mock<IServiceSettingsRepository> _mockServiceSettingsRepo = null!;
    private Mock<ILogger<SecurityController>> _mockLogger = null!;
    private JimApplication _application = null!;
    private SecurityController _controller = null!;
    private Guid _callerObjectId;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockSecurityRepo = new Mock<ISecurityRepository>();
        _mockMetaverseRepo = new Mock<IMetaverseRepository>();
        _mockActivityRepo = new Mock<IActivityRepository>();
        _mockApiKeyRepo = new Mock<IApiKeyRepository>();
        _mockServiceSettingsRepo = new Mock<IServiceSettingsRepository>();
        _mockLogger = new Mock<ILogger<SecurityController>>();

        _mockRepository.Setup(r => r.Security).Returns(_mockSecurityRepo.Object);
        _mockRepository.Setup(r => r.Metaverse).Returns(_mockMetaverseRepo.Object);
        // Role membership changes now write an audit Activity through the application layer.
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepo.Object);
        // The controller now resolves the calling principal (ApiKeysController pattern) to attribute membership
        // changes, so ApiKeys/ServiceSettings must be wired even though most tests use SSO user context: an
        // unmocked property dereferences to null and the caller-resolution helpers are not exception-guarded.
        _mockRepository.Setup(r => r.ApiKeys).Returns(_mockApiKeyRepo.Object);
        _mockRepository.Setup(r => r.ServiceSettings).Returns(_mockServiceSettingsRepo.Object);
        _mockServiceSettingsRepo.Setup(r => r.GetServiceSettingsAsync()).ReturnsAsync((ServiceSettings?)null);

        _application = new JimApplication(_mockRepository.Object);
        _controller = new SecurityController(_mockLogger.Object, _application);

        // Set up SSO user authentication context (Metaverse Object ID as sub claim)
        _callerObjectId = Guid.NewGuid();
        SetupUserContext(_callerObjectId);
    }

    private void SetupUserContext(Guid userId)
    {
        var claims = new List<Claim>
        {
            new("sub", userId.ToString()),
            new("name", "Test User"),
            new(ClaimTypes.Role, "Administrator")
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var principal = new ClaimsPrincipal(identity);

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
    }

    private void SetupApiKeyContext(Guid apiKeyId)
    {
        var claims = new List<Claim>
        {
            new("auth_method", "api_key"),
            new(ClaimTypes.NameIdentifier, apiKeyId.ToString()),
            new(ClaimTypes.Name, "TestApiKey"),
            new(ClaimTypes.Role, "Administrator")
        };
        var identity = new ClaimsIdentity(claims, "ApiKey");
        var principal = new ClaimsPrincipal(identity);

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
    }

    [TearDown]
    public void TearDown()
    {
        _application.Dispose();
    }

    // ──────────────────────────────────────────────
    // GET /api/v1/security/roles
    // ──────────────────────────────────────────────

    [Test]
    public async Task GetRolesAsync_ReturnsOkWithRoleHeadersAsync()
    {
        // Arrange
        var headers = new List<JIM.Models.Security.DTOs.RoleHeader>
        {
            new() { Id = 1, Name = "Administrator", BuiltIn = true, Created = DateTime.UtcNow, StaticMemberCount = 3 },
            new() { Id = 2, Name = "Reader", BuiltIn = true, Created = DateTime.UtcNow, StaticMemberCount = 0 }
        };
        _mockSecurityRepo.Setup(r => r.GetRoleHeadersAsync()).ReturnsAsync(headers);

        // Act
        var result = await _controller.GetRolesAsync();

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var okResult = (OkObjectResult)result;
        var returned = okResult.Value as IEnumerable<JIM.Models.Security.DTOs.RoleHeader>;
        Assert.That(returned, Is.Not.Null);
        var list = returned!.ToList();
        Assert.That(list, Has.Count.EqualTo(2));
        Assert.That(list.Single(h => h.Name == "Administrator").StaticMemberCount, Is.EqualTo(3));
        Assert.That(list.Single(h => h.Name == "Reader").StaticMemberCount, Is.EqualTo(0));
    }

    // ──────────────────────────────────────────────
    // GET /api/v1/security/roles/{roleId}
    // ──────────────────────────────────────────────

    [Test]
    public async Task GetRoleByIdAsync_WithValidId_ReturnsOkWithRoleAsync()
    {
        // Arrange
        var role = new Role { Id = 1, Name = "Administrator", BuiltIn = true, Created = DateTime.UtcNow, StaticMembers = new List<MetaverseObject>() };
        _mockSecurityRepo.Setup(r => r.GetRoleByIdAsync(1)).ReturnsAsync(role);

        // Act
        var result = await _controller.GetRoleByIdAsync(1);

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var okResult = (OkObjectResult)result;
        Assert.That(okResult.Value, Is.InstanceOf<RoleDto>());
        var dto = (RoleDto)okResult.Value!;
        Assert.That(dto.Id, Is.EqualTo(1));
        Assert.That(dto.Name, Is.EqualTo("Administrator"));
    }

    [Test]
    public async Task GetRoleByIdAsync_WithInvalidId_ReturnsNotFoundAsync()
    {
        // Arrange
        _mockSecurityRepo.Setup(r => r.GetRoleByIdAsync(999)).ReturnsAsync((Role?)null);

        // Act
        var result = await _controller.GetRoleByIdAsync(999);

        // Assert
        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    // ──────────────────────────────────────────────
    // GET /api/v1/security/roles/{roleId}/members
    // ──────────────────────────────────────────────

    [Test]
    public async Task GetRoleMembersAsync_WithValidRole_ReturnsOkWithMembersAsync()
    {
        // Arrange
        var role = new Role { Id = 1, Name = "Administrator", BuiltIn = true };
        var objectType = new MetaverseObjectType { Id = 1, Name = "Person" };
        var members = new List<MetaverseObject>
        {
            new() { Id = Guid.NewGuid(), CachedDisplayName = "Alice", Type = objectType },
            new() { Id = Guid.NewGuid(), CachedDisplayName = "Bob", Type = objectType }
        };

        _mockSecurityRepo.Setup(r => r.GetRoleByIdAsync(1)).ReturnsAsync(role);
        _mockSecurityRepo.Setup(r => r.GetRoleMembersAsync(1)).ReturnsAsync(members);

        // Act
        var result = await _controller.GetRoleMembersAsync(1);

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var okResult = (OkObjectResult)result;
        var dtos = okResult.Value as IEnumerable<RoleMemberDto>;
        Assert.That(dtos, Is.Not.Null);
        Assert.That(dtos!.Count(), Is.EqualTo(2));
        // Object type is nested to match the single-object response shape (issue #813).
        var firstDto = dtos!.First();
        Assert.That(firstDto.Type, Is.Not.Null);
        Assert.That(firstDto.Type!.Id, Is.EqualTo(1));
        Assert.That(firstDto.Type.Name, Is.EqualTo("Person"));
    }

    [Test]
    public async Task GetRoleMembersAsync_WithInvalidRole_ReturnsNotFoundAsync()
    {
        // Arrange
        _mockSecurityRepo.Setup(r => r.GetRoleByIdAsync(999)).ReturnsAsync((Role?)null);

        // Act
        var result = await _controller.GetRoleMembersAsync(999);

        // Assert
        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task GetRoleMembersAsync_WithEmptyRole_ReturnsEmptyListAsync()
    {
        // Arrange
        var role = new Role { Id = 1, Name = "User", BuiltIn = true };
        _mockSecurityRepo.Setup(r => r.GetRoleByIdAsync(1)).ReturnsAsync(role);
        _mockSecurityRepo.Setup(r => r.GetRoleMembersAsync(1)).ReturnsAsync(new List<MetaverseObject>());

        // Act
        var result = await _controller.GetRoleMembersAsync(1);

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var okResult = (OkObjectResult)result;
        var dtos = okResult.Value as IEnumerable<RoleMemberDto>;
        Assert.That(dtos, Is.Not.Null);
        Assert.That(dtos!.Count(), Is.EqualTo(0));
    }

    // ──────────────────────────────────────────────
    // PUT /api/v1/security/roles/{roleId}/members/{metaverseObjectId}
    // ──────────────────────────────────────────────

    [Test]
    public async Task AddRoleMemberAsync_WithValidIds_ReturnsNoContentAsync()
    {
        // Arrange
        var role = new Role { Id = 1, Name = "Administrator", BuiltIn = true };
        var objectId = Guid.NewGuid();

        _mockSecurityRepo.Setup(r => r.GetRoleByIdAsync(1)).ReturnsAsync(role);
        _mockSecurityRepo.Setup(r => r.AddObjectToRoleByIdAsync(objectId, 1)).Returns(Task.CompletedTask);

        // Act
        var result = await _controller.AddRoleMemberAsync(1, objectId);

        // Assert
        Assert.That(result, Is.InstanceOf<NoContentResult>());
        _mockSecurityRepo.Verify(r => r.AddObjectToRoleByIdAsync(objectId, 1), Times.Once);
    }

    [Test]
    public async Task AddRoleMemberAsync_WithInvalidRole_ReturnsNotFoundAsync()
    {
        // Arrange
        _mockSecurityRepo.Setup(r => r.GetRoleByIdAsync(999)).ReturnsAsync((Role?)null);

        // Act
        var result = await _controller.AddRoleMemberAsync(999, Guid.NewGuid());

        // Assert
        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task AddRoleMemberAsync_WithInvalidObject_ReturnsBadRequestAsync()
    {
        // Arrange
        var role = new Role { Id = 1, Name = "Administrator", BuiltIn = true };
        var objectId = Guid.NewGuid();

        _mockSecurityRepo.Setup(r => r.GetRoleByIdAsync(1)).ReturnsAsync(role);
        _mockSecurityRepo.Setup(r => r.AddObjectToRoleByIdAsync(objectId, 1))
            .ThrowsAsync(new ArgumentException("No such object found: " + objectId));

        // Act
        var result = await _controller.AddRoleMemberAsync(1, objectId);

        // Assert
        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task AddRoleMemberAsync_WhenAlreadyMember_ReturnsConflictAsync()
    {
        // Arrange
        var role = new Role { Id = 1, Name = "Administrator", BuiltIn = true };
        var objectId = Guid.NewGuid();

        _mockSecurityRepo.Setup(r => r.GetRoleByIdAsync(1)).ReturnsAsync(role);
        _mockSecurityRepo.Setup(r => r.AddObjectToRoleByIdAsync(objectId, 1))
            .ThrowsAsync(new ArgumentException("Object is already in that role"));

        // Act
        var result = await _controller.AddRoleMemberAsync(1, objectId);

        // Assert
        Assert.That(result, Is.InstanceOf<ConflictObjectResult>());
    }

    [Test]
    public async Task AddRoleMemberAsync_WithChangeReason_RecordsReasonOnAuditActivityAsync()
    {
        // Arrange
        var role = new Role { Id = 1, Name = "Administrator", BuiltIn = true };
        var objectId = Guid.NewGuid();

        _mockSecurityRepo.Setup(r => r.GetRoleByIdAsync(1)).ReturnsAsync(role);
        _mockSecurityRepo.Setup(r => r.AddObjectToRoleByIdAsync(objectId, 1)).Returns(Task.CompletedTask);

        Activity? capturedActivity = null;
        _mockActivityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => capturedActivity = a)
            .Returns(Task.CompletedTask);
        _mockActivityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _controller.AddRoleMemberAsync(1, objectId, "Granting temporary access for audit (CHG0125)");

        // Assert
        Assert.That(result, Is.InstanceOf<NoContentResult>());
        Assert.That(capturedActivity, Is.Not.Null);
        Assert.That(capturedActivity!.ChangeReason, Is.EqualTo("Granting temporary access for audit (CHG0125)"));
        Assert.That(capturedActivity.TargetType, Is.EqualTo(ActivityTargetType.Role));
        Assert.That(capturedActivity.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Update));
    }

    // ──────────────────────────────────────────────
    // DELETE /api/v1/security/roles/{roleId}/members/{metaverseObjectId}
    // ──────────────────────────────────────────────

    [Test]
    public async Task RemoveRoleMemberAsync_WithValidIds_ReturnsNoContentAsync()
    {
        // Arrange
        var role = new Role { Id = 1, Name = "User", BuiltIn = true };
        var objectId = Guid.NewGuid();

        _mockSecurityRepo.Setup(r => r.GetRoleByIdAsync(1)).ReturnsAsync(role);
        _mockSecurityRepo.Setup(r => r.RemoveObjectFromRoleAsync(objectId, 1)).Returns(Task.CompletedTask);

        // Act
        var result = await _controller.RemoveRoleMemberAsync(1, objectId);

        // Assert
        Assert.That(result, Is.InstanceOf<NoContentResult>());
        _mockSecurityRepo.Verify(r => r.RemoveObjectFromRoleAsync(objectId, 1), Times.Once);
    }

    [Test]
    public async Task RemoveRoleMemberAsync_WithInvalidRole_ReturnsNotFoundAsync()
    {
        // Arrange
        _mockSecurityRepo.Setup(r => r.GetRoleByIdAsync(999)).ReturnsAsync((Role?)null);

        // Act
        var result = await _controller.RemoveRoleMemberAsync(999, Guid.NewGuid());

        // Assert
        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task RemoveRoleMemberAsync_SelfRemovalFromAdminRole_ReturnsBadRequestAsync()
    {
        // Arrange - caller is trying to remove themselves from the Administrator role
        var role = new Role { Id = 1, Name = "Administrator", BuiltIn = true };

        _mockSecurityRepo.Setup(r => r.GetRoleByIdAsync(1)).ReturnsAsync(role);

        // Act - _callerObjectId is the same as the metaverseObjectId being removed
        var result = await _controller.RemoveRoleMemberAsync(1, _callerObjectId);

        // Assert
        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        var badRequest = (BadRequestObjectResult)result;
        Assert.That(badRequest.Value, Is.InstanceOf<ApiErrorResponse>());
        var error = (ApiErrorResponse)badRequest.Value!;
        Assert.That(error.Code, Is.EqualTo("VALIDATION_ERROR"));
        Assert.That(error.Message, Does.Contain("cannot remove yourself"));
    }

    [Test]
    public async Task RemoveRoleMemberAsync_LastAdminMember_ReturnsBadRequestAsync()
    {
        // Arrange - only one member left in the Administrator role
        var role = new Role { Id = 1, Name = "Administrator", BuiltIn = true };
        var lastMemberId = Guid.NewGuid();
        var members = new List<MetaverseObject>
        {
            new() { Id = lastMemberId, CachedDisplayName = "Last Admin", Type = new MetaverseObjectType { Id = 1, Name = "Person" } }
        };

        _mockSecurityRepo.Setup(r => r.GetRoleByIdAsync(1)).ReturnsAsync(role);
        _mockSecurityRepo.Setup(r => r.GetRoleMembersAsync(1)).ReturnsAsync(members);

        // Act
        var result = await _controller.RemoveRoleMemberAsync(1, lastMemberId);

        // Assert
        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        var badRequest = (BadRequestObjectResult)result;
        Assert.That(badRequest.Value, Is.InstanceOf<ApiErrorResponse>());
        var error = (ApiErrorResponse)badRequest.Value!;
        Assert.That(error.Code, Is.EqualTo("VALIDATION_ERROR"));
        Assert.That(error.Message, Does.Contain("last member"));
    }

    [Test]
    public async Task RemoveRoleMemberAsync_ApiKeyCaller_CanRemoveOtherFromAdminAsync()
    {
        // Arrange - API key caller removing someone from Admin role (not self-removal)
        SetupApiKeyContext(Guid.NewGuid());
        var role = new Role { Id = 1, Name = "Administrator", BuiltIn = true };
        var objectId = Guid.NewGuid();
        var members = new List<MetaverseObject>
        {
            new() { Id = objectId, CachedDisplayName = "Admin 1", Type = new MetaverseObjectType { Id = 1, Name = "Person" } },
            new() { Id = Guid.NewGuid(), CachedDisplayName = "Admin 2", Type = new MetaverseObjectType { Id = 1, Name = "Person" } }
        };

        _mockSecurityRepo.Setup(r => r.GetRoleByIdAsync(1)).ReturnsAsync(role);
        _mockSecurityRepo.Setup(r => r.GetRoleMembersAsync(1)).ReturnsAsync(members);
        _mockSecurityRepo.Setup(r => r.RemoveObjectFromRoleAsync(objectId, 1)).Returns(Task.CompletedTask);

        // Act
        var result = await _controller.RemoveRoleMemberAsync(1, objectId);

        // Assert
        Assert.That(result, Is.InstanceOf<NoContentResult>());
    }

    [Test]
    public async Task RemoveRoleMemberAsync_NonAdminRole_AllowsSelfRemovalAsync()
    {
        // Arrange - self-removal from a non-Administrator role is allowed
        var role = new Role { Id = 2, Name = "User", BuiltIn = true };

        _mockSecurityRepo.Setup(r => r.GetRoleByIdAsync(2)).ReturnsAsync(role);
        _mockSecurityRepo.Setup(r => r.RemoveObjectFromRoleAsync(_callerObjectId, 2)).Returns(Task.CompletedTask);

        // Act
        var result = await _controller.RemoveRoleMemberAsync(2, _callerObjectId);

        // Assert
        Assert.That(result, Is.InstanceOf<NoContentResult>());
    }

    // ──────────────────────────────────────────────
    // GET /api/v1/security/metaverse-objects/{metaverseObjectId}/roles
    // ──────────────────────────────────────────────

    [Test]
    public async Task GetMetaverseObjectRolesAsync_WithValidId_ReturnsOkWithRolesAsync()
    {
        // Arrange
        var objectId = Guid.NewGuid();
        var mvo = new MetaverseObject { Id = objectId, CachedDisplayName = "Alice", Type = new MetaverseObjectType { Id = 1, Name = "Person" } };
        var roles = new List<Role>
        {
            new() { Id = 1, Name = "Administrator", BuiltIn = true, Created = DateTime.UtcNow, StaticMembers = new List<MetaverseObject> { mvo } },
            new() { Id = 2, Name = "Auditor", BuiltIn = false, Created = DateTime.UtcNow, StaticMembers = new List<MetaverseObject> { mvo } }
        };

        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectAsync(objectId)).ReturnsAsync(mvo);
        _mockSecurityRepo.Setup(r => r.GetMetaverseObjectRolesAsync(objectId)).ReturnsAsync(roles);

        // Act
        var result = await _controller.GetMetaverseObjectRolesAsync(objectId);

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var okResult = (OkObjectResult)result;
        var dtos = okResult.Value as IEnumerable<RoleDto>;
        Assert.That(dtos, Is.Not.Null);
        Assert.That(dtos!.Count(), Is.EqualTo(2));
        Assert.That(dtos!.Any(r => r.Name == "Administrator"), Is.True);
        Assert.That(dtos!.Any(r => r.Name == "Auditor"), Is.True);
    }

    [Test]
    public async Task GetMetaverseObjectRolesAsync_WithUnknownMetaverseObjectId_ReturnsNotFoundAsync()
    {
        // Arrange
        var objectId = Guid.NewGuid();
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectAsync(objectId)).ReturnsAsync((MetaverseObject?)null);

        // Act
        var result = await _controller.GetMetaverseObjectRolesAsync(objectId);

        // Assert
        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
        _mockSecurityRepo.Verify(r => r.GetMetaverseObjectRolesAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Test]
    public async Task GetMetaverseObjectRolesAsync_WhenObjectHasNoRoles_ReturnsEmptyListAsync()
    {
        // Arrange
        var objectId = Guid.NewGuid();
        var mvo = new MetaverseObject { Id = objectId, CachedDisplayName = "Eve", Type = new MetaverseObjectType { Id = 1, Name = "Person" } };

        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectAsync(objectId)).ReturnsAsync(mvo);
        _mockSecurityRepo.Setup(r => r.GetMetaverseObjectRolesAsync(objectId)).ReturnsAsync(new List<Role>());

        // Act
        var result = await _controller.GetMetaverseObjectRolesAsync(objectId);

        // Assert
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var okResult = (OkObjectResult)result;
        var dtos = okResult.Value as IEnumerable<RoleDto>;
        Assert.That(dtos, Is.Not.Null);
        Assert.That(dtos!.Count(), Is.EqualTo(0));
    }

    [Test]
    public async Task RemoveRoleMemberAsync_ObjectNotInRole_ReturnsBadRequestAsync()
    {
        // Arrange
        var role = new Role { Id = 1, Name = "User", BuiltIn = true };
        var objectId = Guid.NewGuid();

        _mockSecurityRepo.Setup(r => r.GetRoleByIdAsync(1)).ReturnsAsync(role);
        _mockSecurityRepo.Setup(r => r.RemoveObjectFromRoleAsync(objectId, 1))
            .ThrowsAsync(new ArgumentException("Object is not a member of this role"));

        // Act
        var result = await _controller.RemoveRoleMemberAsync(1, objectId);

        // Assert
        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task RemoveRoleMemberAsync_WithChangeReason_RecordsReasonOnAuditActivityAsync()
    {
        // Arrange - non-Administrator role so the lockout safety checks don't interfere
        var role = new Role { Id = 2, Name = "User", BuiltIn = true };
        var objectId = Guid.NewGuid();

        _mockSecurityRepo.Setup(r => r.GetRoleByIdAsync(2)).ReturnsAsync(role);
        _mockSecurityRepo.Setup(r => r.RemoveObjectFromRoleAsync(objectId, 2)).Returns(Task.CompletedTask);

        Activity? capturedActivity = null;
        _mockActivityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => capturedActivity = a)
            .Returns(Task.CompletedTask);
        _mockActivityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _controller.RemoveRoleMemberAsync(2, objectId, "Offboarding: role no longer required (CHG0126)");

        // Assert
        Assert.That(result, Is.InstanceOf<NoContentResult>());
        Assert.That(capturedActivity, Is.Not.Null);
        Assert.That(capturedActivity!.ChangeReason, Is.EqualTo("Offboarding: role no longer required (CHG0126)"));
        Assert.That(capturedActivity.TargetType, Is.EqualTo(ActivityTargetType.Role));
        Assert.That(capturedActivity.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Update));
    }
}
