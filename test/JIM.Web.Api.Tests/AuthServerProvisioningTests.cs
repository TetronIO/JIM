// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Application.Servers;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
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
        // Every Metaverse Object edit reads its type's import Synchronisation Rules, to mark the Connected Systems whose
        // Metaverse-Derived Attribute Flows read a changed attribute (#1750): none here.
        var connectedSystemsRepo = new Mock<IConnectedSystemRepository>();
        connectedSystemsRepo.Setup(r => r.GetImportSyncRulesForMetaverseObjectTypeAsync(It.IsAny<int>())).ReturnsAsync(new List<JIM.Models.Logic.SyncRule>());
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(connectedSystemsRepo.Object);

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

        // Production hosts always hand JimApplication a sync repository; the derived-input marking every Metaverse
        // Object edit runs (#1750), including the sign-in attribute supplement, flushes through it.
        _application = new JimApplication(_mockRepository.Object, syncRepository: new JIM.InMemoryData.SyncRepository());
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

    /// <summary>
    /// #2472: every value the initial administrator is created with records where it came from: the configured
    /// identifier claim, the profile claims each value was read from, and JIM itself for the Type it fixes.
    /// </summary>
    [Test]
    public async Task ResolveOrProvisionSsoIdentityAsync_InitialAdminFirstContact_RecordsEachValuesSourceAsync()
    {
        Environment.SetEnvironmentVariable(Constants.Config.SsoInitialAdmin, AdminSub);
        _mockMetaverse.Setup(r => r.GetMetaverseObjectByTypeAndAttributeAsync(_userType, _uniqueIdAttribute, AdminSub))
            .ReturnsAsync((MetaverseObject?)null);

        MetaverseObject? created = null;
        _mockMetaverse.Setup(r => r.CreateMetaverseObjectAsync(It.IsAny<MetaverseObject>()))
            .Callback<MetaverseObject>(m => { m.Id = Guid.NewGuid(); created = m; })
            .Returns(Task.CompletedTask);

        await _application.Auth.ResolveOrProvisionSsoIdentityAsync(new SsoIdentity
        {
            UniqueId = AdminSub,
            DisplayName = "Jay Admin",
            FirstName = "Jay",
            LastName = "Admin",
            UserPrincipalName = "jay@panoply.local"
        });

        MetaverseObjectAttributeValue Value(string attributeName) => created!.AttributeValues.Single(av => av.Attribute!.Name == attributeName);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Value("Subject Identifier").ContributedByKind, Is.EqualTo(ValueContributorKind.SignInClaim));
            Assert.That(Value("Subject Identifier").ContributedByClaimType, Is.EqualTo("sub"), "the configured identifier claim");
            Assert.That(Value(Constants.BuiltInAttributes.DisplayName).ContributedByClaimType, Is.EqualTo("name"));
            Assert.That(Value(Constants.BuiltInAttributes.FirstName).ContributedByClaimType, Is.EqualTo("given_name"));
            Assert.That(Value(Constants.BuiltInAttributes.LastName).ContributedByClaimType, Is.EqualTo("family_name"));
            Assert.That(Value(Constants.BuiltInAttributes.UserPrincipalName).ContributedByClaimType, Is.EqualTo("preferred_username"));
            Assert.That(new[] { Constants.BuiltInAttributes.DisplayName, Constants.BuiltInAttributes.FirstName, Constants.BuiltInAttributes.LastName, Constants.BuiltInAttributes.UserPrincipalName }
                .Select(n => Value(n).ContributedByKind), Is.All.EqualTo(ValueContributorKind.SignInClaim));
            Assert.That(Value(Constants.BuiltInAttributes.Type).ContributedByKind, Is.EqualTo(ValueContributorKind.System), "Type is JIM's constant, not the token's");
            Assert.That(Value(Constants.BuiltInAttributes.Type).ContributedByClaimType, Is.Null);
        }
    }

    /// <summary>
    /// The initial administrator's Created change record must say System made it, inside the Activity that recorded the
    /// creation, so its history and the Object Changes page show who made it and link to how (#2460).
    /// </summary>
    [Test]
    public async Task ResolveOrProvisionSsoIdentityAsync_InitialAdminFirstContact_RecordsTheCreationAsSystemInItsActivityAsync()
    {
        Environment.SetEnvironmentVariable(Constants.Config.SsoInitialAdmin, AdminSub);
        _mockServiceSettings.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ChangeTrackingMvoChangesEnabled))
            .ReturnsAsync(new ServiceSetting
            {
                Key = Constants.SettingKeys.ChangeTrackingMvoChangesEnabled,
                DisplayName = "MVO change tracking",
                ValueType = ServiceSettingValueType.Boolean,
                Value = "true"
            });
        _mockMetaverse.Setup(r => r.GetMetaverseObjectByTypeAndAttributeAsync(_userType, _uniqueIdAttribute, AdminSub))
            .ReturnsAsync((MetaverseObject?)null);

        // A change record needs each attribute's data type, which the fixture's attributes otherwise leave unset.
        _uniqueIdAttribute.Type = AttributeDataType.Text;
        _mockMetaverse.Setup(r => r.GetMetaverseAttributeAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync((string name, bool _) => new MetaverseAttribute { Name = name, Type = AttributeDataType.Text });

        // Persisting an Activity assigns it an Id, mirroring the database.
        var activities = new List<Activity>();
        _mockActivity.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => { a.Id = Guid.NewGuid(); activities.Add(a); })
            .Returns(Task.CompletedTask);

        MetaverseObject? created = null;
        _mockMetaverse.Setup(r => r.CreateMetaverseObjectAsync(It.IsAny<MetaverseObject>()))
            .Callback<MetaverseObject>(m => { m.Id = Guid.NewGuid(); created = m; })
            .Returns(Task.CompletedTask);

        await _application.Auth.ResolveOrProvisionSsoIdentityAsync(new SsoIdentity { UniqueId = AdminSub, DisplayName = "Jay Admin" });

        var createActivity = activities.Single(a => a.TargetOperationType == ActivityTargetOperationType.Create);
        var change = created!.Changes.Single(c => c.ChangeType == ObjectChangeType.Created);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.InitiatedByType, Is.EqualTo(ActivityInitiatorType.System));
            Assert.That(change.InitiatedByName, Is.EqualTo("System"));
            Assert.That(change.ChangeInitiatorType, Is.EqualTo(MetaverseObjectChangeInitiatorType.System));
            Assert.That(change.ActivityId, Is.EqualTo(createActivity.Id));
        }
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
        var supplemented = existing.AttributeValues.Single(av => av.Attribute!.Name == Constants.BuiltInAttributes.DisplayName);
        Assert.That(supplemented.StringValue, Is.EqualTo("Synced User"));
        Assert.That(supplemented.ContributedByKind, Is.EqualTo(ValueContributorKind.SignInClaim), "#2472: a supplemented value records the token as its source");
        Assert.That(supplemented.ContributedByClaimType, Is.EqualTo("name"));
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

    #region Guid-typed identifier attribute (AD FS objectGUID as oid, #2438)

    private static readonly Guid PersonGuid = new("550e8400-e29b-41d4-a716-446655440000");

    // PersonGuid's bytes in Microsoft byte order, base64-encoded: how AD FS issues objectGUID.
    private const string PersonGuidAsAdfsIssuesIt = "AIQOVZvi1EGnFkRmVUQAAA==";

    private MetaverseAttribute UseGuidIdentifierAttribute()
    {
        var objectGuid = new MetaverseAttribute { Id = 2, Name = Constants.BuiltInAttributes.ObjectGuid, Type = AttributeDataType.Guid };
        _mockServiceSettings.Setup(r => r.GetServiceSettingsAsync()).ReturnsAsync(new ServiceSettings
        {
            SSOUniqueIdentifierClaimType = "oid",
            SSOUniqueIdentifierMetaverseAttribute = objectGuid
        });
        return objectGuid;
    }

    [TestCase(PersonGuidAsAdfsIssuesIt)]
    [TestCase("550e8400-e29b-41d4-a716-446655440000")]
    [TestCase("{550E8400-E29B-41D4-A716-446655440000}")]
    public async Task ResolveOrProvisionSsoIdentityAsync_GuidAttribute_MatchesTheUserByTheirGuidAsync(string claimValue)
    {
        var objectGuid = UseGuidIdentifierAttribute();
        var existing = new MetaverseObject { Id = Guid.NewGuid(), Type = _userType, Origin = MetaverseObjectOrigin.Projected };
        _mockMetaverse.Setup(r => r.GetMetaverseObjectByTypeAndAttributeAsync(_userType, objectGuid, PersonGuid))
            .ReturnsAsync(existing);

        var result = await _application.Auth.ResolveOrProvisionSsoIdentityAsync(new SsoIdentity { UniqueId = claimValue });

        Assert.That(result, Is.SameAs(existing));
        _mockMetaverse.Verify(r => r.GetMetaverseObjectByTypeAndAttributeAsync(It.IsAny<MetaverseObjectType>(), It.IsAny<MetaverseAttribute>(), It.IsAny<string>()), Times.Never,
            "a Guid attribute is never compared as text");
    }

    [Test]
    public async Task ResolveOrProvisionSsoIdentityAsync_GuidAttributeAndClaimNotAGuid_ReturnsNullWithoutLookingUpAsync()
    {
        UseGuidIdentifierAttribute();
        Environment.SetEnvironmentVariable(Constants.Config.SsoInitialAdmin, PersonGuid.ToString());

        var result = await _application.Auth.ResolveOrProvisionSsoIdentityAsync(new SsoIdentity { UniqueId = "jbloggs@panoply.local" });

        Assert.That(result, Is.Null);
        _mockMetaverse.Verify(r => r.GetMetaverseObjectByTypeAndAttributeAsync(It.IsAny<MetaverseObjectType>(), It.IsAny<MetaverseAttribute>(), It.IsAny<Guid>()), Times.Never);
        _mockMetaverse.Verify(r => r.CreateMetaverseObjectAsync(It.IsAny<MetaverseObject>()), Times.Never);
    }

    [Test]
    public async Task ResolveOrProvisionSsoIdentityAsync_GuidAttributeInitialAdminFirstContact_CreatesTheUserWithTheGuidAsync()
    {
        var objectGuid = UseGuidIdentifierAttribute();
        // The administrator names the canonical GUID, in whatever case; AD FS sends the bytes.
        Environment.SetEnvironmentVariable(Constants.Config.SsoInitialAdmin, PersonGuid.ToString().ToUpperInvariant());
        _mockMetaverse.Setup(r => r.GetMetaverseObjectByTypeAndAttributeAsync(_userType, objectGuid, PersonGuid))
            .ReturnsAsync((MetaverseObject?)null);
        MetaverseObject? created = null;
        _mockMetaverse.Setup(r => r.CreateMetaverseObjectAsync(It.IsAny<MetaverseObject>()))
            .Callback<MetaverseObject>(m => { m.Id = Guid.NewGuid(); created = m; })
            .Returns(Task.CompletedTask);

        var result = await _application.Auth.ResolveOrProvisionSsoIdentityAsync(new SsoIdentity { UniqueId = PersonGuidAsAdfsIssuesIt });

        Assert.That(result, Is.Not.Null);
        var identifier = created!.AttributeValues.Single(av => av.Attribute!.Name == Constants.BuiltInAttributes.ObjectGuid);
        Assert.Multiple(() =>
        {
            Assert.That(identifier.GuidValue, Is.EqualTo(PersonGuid));
            Assert.That(identifier.StringValue, Is.Null);
        });
        _mockSecurity.Verify(r => r.AddObjectToRoleAsync(created.Id, Constants.BuiltInRoles.Administrator), Times.Once);
    }

    [Test]
    public async Task FindSsoUserAsync_GuidAttributeAndBase64ObjectGuid_FindsTheUserByTheirGuidAsync()
    {
        var objectGuid = UseGuidIdentifierAttribute();
        var existing = new MetaverseObject { Id = Guid.NewGuid(), Type = _userType };
        _mockMetaverse.Setup(r => r.GetMetaverseObjectByTypeAndAttributeAsync(_userType, objectGuid, PersonGuid))
            .ReturnsAsync(existing);

        var result = await _application.Auth.FindSsoUserAsync(PersonGuidAsAdfsIssuesIt);

        Assert.That(result, Is.SameAs(existing));
    }

    [Test]
    public async Task FindSsoUserAsync_TextAttribute_FindsTheUserByTheClaimTextAsync()
    {
        var existing = new MetaverseObject { Id = Guid.NewGuid(), Type = _userType };
        _mockMetaverse.Setup(r => r.GetMetaverseObjectByTypeAndAttributeAsync(_userType, _uniqueIdAttribute, "synced-user-sub"))
            .ReturnsAsync(existing);

        var result = await _application.Auth.FindSsoUserAsync("synced-user-sub");

        Assert.That(result, Is.SameAs(existing));
    }

    [Test]
    public async Task FindSsoUserAsync_GuidAttributeAndClaimNotAGuid_ReturnsNullAsync()
    {
        UseGuidIdentifierAttribute();

        var result = await _application.Auth.FindSsoUserAsync("jbloggs@panoply.local");

        Assert.That(result, Is.Null);
        _mockMetaverse.Verify(r => r.GetMetaverseObjectByTypeAndAttributeAsync(It.IsAny<MetaverseObjectType>(), It.IsAny<MetaverseAttribute>(), It.IsAny<string>()), Times.Never);
    }

    [TestCase(AttributeDataType.Text, null)]
    [TestCase(AttributeDataType.Text, "admin-sub-0001")]
    [TestCase(AttributeDataType.Guid, null)]
    [TestCase(AttributeDataType.Guid, "550e8400-e29b-41d4-a716-446655440000")]
    public void DescribeSsoIdentifierProblem_SupportedSettings_ReturnsNull(AttributeDataType type, string? initialAdmin)
    {
        var attribute = new MetaverseAttribute { Name = "Identifier", Type = type };

        Assert.That(AuthServer.DescribeSsoIdentifierProblem(attribute, initialAdmin), Is.Null);
    }

    [Test]
    public void DescribeSsoIdentifierProblem_GuidAttributeAndInitialAdminNotAGuid_NamesBothSettings()
    {
        var attribute = new MetaverseAttribute { Name = Constants.BuiltInAttributes.ObjectGuid, Type = AttributeDataType.Guid };

        var problem = AuthServer.DescribeSsoIdentifierProblem(attribute, "jbloggs@panoply.local");

        Assert.That(problem, Does.Contain(Constants.Config.SsoInitialAdmin).And.Contain(Constants.Config.SsoMvAttribute).And.Contain("GUID"));
    }

    [TestCase(AttributeDataType.Number)]
    [TestCase(AttributeDataType.Reference)]
    [TestCase(AttributeDataType.Binary)]
    public void DescribeSsoIdentifierProblem_AttributeNeitherTextNorGuid_NamesTheAttributeAndItsType(AttributeDataType type)
    {
        var attribute = new MetaverseAttribute { Name = "Employee Number", Type = type };

        var problem = AuthServer.DescribeSsoIdentifierProblem(attribute, null);

        Assert.That(problem, Does.Contain("Employee Number").And.Contain(type.ToString()).And.Contain(Constants.Config.SsoMvAttribute));
    }

    #endregion
}
