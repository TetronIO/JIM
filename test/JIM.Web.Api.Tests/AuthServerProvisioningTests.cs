// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Security;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for <see cref="JIM.Application.Servers.AuthServer"/>, covering the just-in-time
/// initial-admin bootstrap that now runs for any authenticated SSO identity (web or API/CLI).
///
/// The repository is mocked because the provisioning path crosses several servers and the
/// underlying queries use Postgres-only operators (ILike) that the EF in-memory provider
/// cannot translate. Mocking at the repository boundary keeps the test deterministic while
/// still exercising AuthServer's real decision and orchestration logic.
/// </summary>
[TestFixture]
public class AuthServerProvisioningTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IMetaverseRepository> _mockMetaverse = null!;
    private Mock<ISecurityRepository> _mockSecurity = null!;
    private Mock<IServiceSettingsRepository> _mockServiceSettings = null!;
    private Mock<IActivityRepository> _mockActivity = null!;
    private JimApplication _application = null!;
    private MetaverseAttribute _uniqueIdAttribute = null!;
    private MetaverseObjectType _userType = null!;
    private string? _originalInitialAdmin;

    private const string AdminSub = "admin-sub-0001";

    [SetUp]
    public void SetUp()
    {
        _originalInitialAdmin = Environment.GetEnvironmentVariable(Constants.Config.SsoInitialAdmin);

        _mockRepository = new Mock<IRepository>();
        _mockMetaverse = new Mock<IMetaverseRepository>();
        _mockSecurity = new Mock<ISecurityRepository>();
        _mockServiceSettings = new Mock<IServiceSettingsRepository>();
        _mockActivity = new Mock<IActivityRepository>();

        _mockRepository.Setup(r => r.Metaverse).Returns(_mockMetaverse.Object);
        _mockRepository.Setup(r => r.Security).Returns(_mockSecurity.Object);
        _mockRepository.Setup(r => r.ServiceSettings).Returns(_mockServiceSettings.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivity.Object);

        _uniqueIdAttribute = new MetaverseAttribute { Id = 1, Name = "Subject Identifier" };
        _userType = new MetaverseObjectType { Id = 1, Name = Constants.BuiltInObjectTypes.User, PluralName = "Users" };

        _mockServiceSettings.Setup(r => r.GetServiceSettingsAsync()).ReturnsAsync(new ServiceSettings
        {
            SSOUniqueIdentifierClaimType = "sub",
            SSOUniqueIdentifierMetaverseAttribute = _uniqueIdAttribute
        });

        // Disable MVO change tracking so creation/update don't pull change-history into scope.
        _mockServiceSettings.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ChangeTrackingMvoChangesEnabled))
            .ReturnsAsync(new ServiceSetting
            {
                Key = Constants.SettingKeys.ChangeTrackingMvoChangesEnabled,
                DisplayName = "MVO change tracking",
                ValueType = ServiceSettingValueType.Boolean,
                Value = "false"
            });

        _mockMetaverse.Setup(r => r.GetMetaverseObjectTypeAsync(Constants.BuiltInObjectTypes.User, false))
            .ReturnsAsync(_userType);

        // Any built-in attribute requested by name resolves to a matching attribute.
        _mockMetaverse.Setup(r => r.GetMetaverseAttributeAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync((string name, bool _) => new MetaverseAttribute { Name = name });

        // Persisting a new MVO assigns it an Id, mirroring the database.
        _mockMetaverse.Setup(r => r.CreateMetaverseObjectAsync(It.IsAny<MetaverseObject>()))
            .Callback<MetaverseObject>(m => m.Id = Guid.NewGuid())
            .Returns(Task.CompletedTask);

        // Stateful role membership, mirroring the database: once added, the object is in the
        // role, so the "ensure admin role" guard does not re-add it.
        var inAdminRole = false;
        _mockSecurity.Setup(r => r.IsObjectInRoleAsync(It.IsAny<Guid>(), Constants.BuiltInRoles.Administrator))
            .ReturnsAsync(() => inAdminRole);
        _mockSecurity.Setup(r => r.AddObjectToRoleAsync(It.IsAny<Guid>(), Constants.BuiltInRoles.Administrator))
            .Callback(() => inAdminRole = true)
            .Returns(Task.CompletedTask);

        _application = new JimApplication(_mockRepository.Object);
    }

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable(Constants.Config.SsoInitialAdmin, _originalInitialAdmin);
        _application?.Dispose();
    }

    [Test]
    public async Task ResolveOrProvisionSsoIdentityAsync_UnknownUserNotInitialAdmin_ReturnsNullAndCreatesNothingAsync()
    {
        Environment.SetEnvironmentVariable(Constants.Config.SsoInitialAdmin, AdminSub);
        _mockMetaverse.Setup(r => r.GetMetaverseObjectByTypeAndAttributeAsync(_userType, _uniqueIdAttribute, It.IsAny<string>()))
            .ReturnsAsync((MetaverseObject?)null);

        var result = await _application.Auth.ResolveOrProvisionSsoIdentityAsync(
            new SsoIdentity { UniqueId = "some-other-user" });

        Assert.That(result, Is.Null);
        _mockMetaverse.Verify(r => r.CreateMetaverseObjectAsync(It.IsAny<MetaverseObject>()), Times.Never);
        _mockSecurity.Verify(r => r.AddObjectToRoleAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task ResolveOrProvisionSsoIdentityAsync_InitialAdminFirstContact_CreatesInternalUserWithAttributesAndAdminRoleAsync()
    {
        Environment.SetEnvironmentVariable(Constants.Config.SsoInitialAdmin, AdminSub);
        _mockMetaverse.Setup(r => r.GetMetaverseObjectByTypeAndAttributeAsync(_userType, _uniqueIdAttribute, AdminSub))
            .ReturnsAsync((MetaverseObject?)null);

        MetaverseObject? created = null;
        _mockMetaverse.Setup(r => r.CreateMetaverseObjectAsync(It.IsAny<MetaverseObject>()))
            .Callback<MetaverseObject>(m => { m.Id = Guid.NewGuid(); created = m; })
            .Returns(Task.CompletedTask);

        var result = await _application.Auth.ResolveOrProvisionSsoIdentityAsync(new SsoIdentity
        {
            UniqueId = AdminSub,
            DisplayName = "Jay Admin",
            FirstName = "Jay",
            LastName = "Admin"
        });

        Assert.That(result, Is.Not.Null);
        Assert.That(created, Is.Not.Null);
        Assert.That(created!.Origin, Is.EqualTo(MetaverseObjectOrigin.Internal), "Initial admin must be Internal-origin to survive deletion rules.");
        Assert.That(created.AttributeValues.Single(av => av.Attribute!.Name == "Subject Identifier").StringValue, Is.EqualTo(AdminSub));
        Assert.That(created.AttributeValues.Single(av => av.Attribute!.Name == Constants.BuiltInAttributes.DisplayName).StringValue, Is.EqualTo("Jay Admin"));
        Assert.That(created.AttributeValues.Any(av => av.Attribute!.Name == Constants.BuiltInAttributes.Type), Is.True);
        _mockSecurity.Verify(r => r.AddObjectToRoleAsync(created.Id, Constants.BuiltInRoles.Administrator), Times.Once);
    }

    [Test]
    public async Task ResolveOrProvisionSsoIdentityAsync_ExistingNonAdminUser_SupplementsMissingAttributesAndDoesNotGrantAdminAsync()
    {
        Environment.SetEnvironmentVariable(Constants.Config.SsoInitialAdmin, AdminSub);

        var existing = new MetaverseObject { Id = Guid.NewGuid(), Type = _userType, Origin = MetaverseObjectOrigin.Projected };
        existing.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            MetaverseObject = existing,
            Attribute = _uniqueIdAttribute,
            StringValue = "synced-user-sub"
        });
        _mockMetaverse.Setup(r => r.GetMetaverseObjectByTypeAndAttributeAsync(_userType, _uniqueIdAttribute, "synced-user-sub"))
            .ReturnsAsync(existing);

        var result = await _application.Auth.ResolveOrProvisionSsoIdentityAsync(new SsoIdentity
        {
            UniqueId = "synced-user-sub",
            DisplayName = "Synced User"
        });

        Assert.That(result, Is.SameAs(existing));
        Assert.That(existing.AttributeValues.Single(av => av.Attribute!.Name == Constants.BuiltInAttributes.DisplayName).StringValue, Is.EqualTo("Synced User"));
        _mockMetaverse.Verify(r => r.UpdateMetaverseObjectAsync(existing), Times.Once);
        _mockMetaverse.Verify(r => r.CreateMetaverseObjectAsync(It.IsAny<MetaverseObject>()), Times.Never);
        _mockSecurity.Verify(r => r.AddObjectToRoleAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task ResolveOrProvisionSsoIdentityAsync_ExistingInitialAdminMissingRole_EnsuresRoleWithoutRecreatingAsync()
    {
        Environment.SetEnvironmentVariable(Constants.Config.SsoInitialAdmin, AdminSub);

        var existingAdmin = new MetaverseObject { Id = Guid.NewGuid(), Type = _userType, Origin = MetaverseObjectOrigin.Internal };
        existingAdmin.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            MetaverseObject = existingAdmin,
            Attribute = _uniqueIdAttribute,
            StringValue = AdminSub
        });
        _mockMetaverse.Setup(r => r.GetMetaverseObjectByTypeAndAttributeAsync(_userType, _uniqueIdAttribute, AdminSub))
            .ReturnsAsync(existingAdmin);

        var result = await _application.Auth.ResolveOrProvisionSsoIdentityAsync(new SsoIdentity { UniqueId = AdminSub });

        Assert.That(result, Is.SameAs(existingAdmin));
        _mockMetaverse.Verify(r => r.CreateMetaverseObjectAsync(It.IsAny<MetaverseObject>()), Times.Never);
        _mockSecurity.Verify(r => r.AddObjectToRoleAsync(existingAdmin.Id, Constants.BuiltInRoles.Administrator), Times.Once);
    }
}
