// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Security;
using Moq;
using NUnit.Framework;
using System.Text;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests configuration change-history capture for Roles: definition creation and static-membership changes each
/// record a versioned, metadata-only snapshot on their audit Activity, keyed by <see cref="Activity.RoleId"/>. The
/// auditable question for a membership change is "who was in this Role and when", so the Activity always targets
/// the Role, not the member. The shared toggle and semantic-dedupe behaviours apply, mirroring
/// <c>ApiKeyConfigurationChangeCaptureTests</c>. Roles have no delete or definition-update path yet (arriving with
/// #612), so there is no tombstone or update coverage here.
/// </summary>
[TestFixture]
public class RoleConfigurationChangeCaptureTests
{
    private const int RoleId = 5;

    private Mock<IRepository> _repo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<IServiceSettingsRepository> _settingsRepo = null!;
    private Mock<ISecurityRepository> _securityRepo = null!;
    private Mock<IMetaverseRepository> _metaverseRepo = null!;
    private FakeProtection _protection = null!;
    private JimApplication _jim = null!;
    private Activity? _completedActivity;
    private List<Activity> _createdActivities = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _repo = new Mock<IRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _settingsRepo = new Mock<IServiceSettingsRepository>();
        _securityRepo = new Mock<ISecurityRepository>();
        _metaverseRepo = new Mock<IMetaverseRepository>();
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _repo.Setup(r => r.ServiceSettings).Returns(_settingsRepo.Object);
        _repo.Setup(r => r.Security).Returns(_securityRepo.Object);
        _repo.Setup(r => r.Metaverse).Returns(_metaverseRepo.Object);

        _createdActivities = new List<Activity>();
        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => _createdActivities.Add(a))
            .Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => _completedActivity = a)
            .Returns(Task.CompletedTask);

        _protection = new FakeProtection();
        _jim = new JimApplication(_repo.Object) { CredentialProtection = _protection };

        // NUnit reuses one fixture instance across every [Test] by default, so a value left over from a previous
        // test would otherwise leak in here; explicit reset keeps assertions reliable regardless of execution order.
        _completedActivity = null;

        SetupTrackingSetting(enabled: true);
        SetupHashKeySetting();
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    // -- CreateRoleAsync -------------------------------------------------------------------------------------------

    [Test]
    public async Task RecordSeededRoleBaselineAsync_RecordsSystemCreateChildWithVersionOneBaselineAsync()
    {
        var role = BuildRole(id: RoleId, name: "Administrator");
        _securityRepo.Setup(r => r.GetRoleByIdAsync(RoleId)).ReturnsAsync(() => role);
        SetupMaxVersion(0);
        var parentActivityId = Guid.NewGuid();

        await _jim.Security.RecordSeededRoleBaselineAsync(RoleId, "Administrator", parentActivityId);

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.TargetType, Is.EqualTo(ActivityTargetType.Role));
        Assert.That(_completedActivity!.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Create));
        Assert.That(_completedActivity!.InitiatedByType, Is.EqualTo(ActivityInitiatorType.System));
        Assert.That(_completedActivity!.ParentActivityId, Is.EqualTo(parentActivityId), "the baseline must group under the seeding parent Activity");
        Assert.That(_completedActivity!.RoleId, Is.EqualTo(RoleId));
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.EqualTo(1));
        Assert.That(_completedActivity!.ConfigurationChangeSnapshot, Does.Contain("\"objectType\":\"Role\""));
    }

    [Test]
    public async Task CreateRoleAsync_NoInitiator_RecordsSystemAttributedActivityWithVersionOneSnapshotAsync()
    {
        Role? persisted = null;
        _securityRepo.Setup(r => r.CreateRoleAsync(It.IsAny<Role>()))
            .Callback<Role>(r => persisted = r)
            .ReturnsAsync((Role r) => r);
        _securityRepo.Setup(r => r.GetRoleByIdAsync(RoleId)).ReturnsAsync(() => persisted);
        SetupMaxVersion(0);

        // Mirrors SeedingServer.SeedBuiltInRolesAsync, which has no MetaverseObject to attribute the change to.
        var role = BuildRole(id: RoleId, name: "Administrator");
        await _jim.Security.CreateRoleAsync(role, changeReason: "Built-in Role created automatically by JIM.");

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.TargetType, Is.EqualTo(ActivityTargetType.Role));
        Assert.That(_completedActivity!.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Create));
        Assert.That(_completedActivity!.InitiatedByType, Is.EqualTo(ActivityInitiatorType.System));
        Assert.That(_completedActivity!.InitiatedByName, Is.EqualTo("System"));
        Assert.That(_completedActivity!.RoleId, Is.EqualTo(RoleId), "the activity must carry the Role id so history is queryable");
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.EqualTo(1));
        var snapshot = _completedActivity!.ConfigurationChangeSnapshot;
        Assert.That(snapshot, Is.Not.Null);
        Assert.That(snapshot, Does.Contain("\"objectType\":\"Role\""));
    }

    [Test]
    public async Task CreateRoleAsync_UserInitiated_AttributesActivityToTheUserAsync()
    {
        Role? persisted = null;
        _securityRepo.Setup(r => r.CreateRoleAsync(It.IsAny<Role>()))
            .Callback<Role>(r => persisted = r)
            .ReturnsAsync((Role r) => r);
        _securityRepo.Setup(r => r.GetRoleByIdAsync(RoleId)).ReturnsAsync(() => persisted);
        SetupMaxVersion(0);

        var role = BuildRole(id: RoleId, name: "Auditor");
        await _jim.Security.CreateRoleAsync(role, NewUser(), changeReason: "new custom role");

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.InitiatedByType, Is.EqualTo(ActivityInitiatorType.User));
        Assert.That(_completedActivity!.RoleId, Is.EqualTo(RoleId));
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.EqualTo(1));
        Assert.That(_completedActivity!.ChangeReason, Is.EqualTo("new custom role"));
    }

    [Test]
    public async Task CreateRoleAsync_ApiKeyInitiated_AttributesActivityToTheInitiatingKeyAsync()
    {
        Role? persisted = null;
        _securityRepo.Setup(r => r.CreateRoleAsync(It.IsAny<Role>()))
            .Callback<Role>(r => persisted = r)
            .ReturnsAsync((Role r) => r);
        _securityRepo.Setup(r => r.GetRoleByIdAsync(RoleId)).ReturnsAsync(() => persisted);
        SetupMaxVersion(0);

        var initiatingKey = new ApiKey { Id = Guid.NewGuid(), Name = "provisioning-key" };
        var role = BuildRole(id: RoleId, name: "Auditor");
        await _jim.Security.CreateRoleAsync(role, initiatingKey, changeReason: "provisioned via API");

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.InitiatedByType, Is.EqualTo(ActivityInitiatorType.ApiKey));
        Assert.That(_completedActivity!.InitiatedById, Is.EqualTo(initiatingKey.Id));
        Assert.That(_completedActivity!.RoleId, Is.EqualTo(RoleId));
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.EqualTo(1));
    }

    // -- AddObjectToRoleByIdAsync ----------------------------------------------------------------------------------

    [Test]
    public async Task AddObjectToRoleByIdAsync_UserInitiated_RecordsRoleTargetedUpdateWithMemberSnapshotAsync()
    {
        var member = NewMember("Jane Smith");
        var role = BuildRole(id: RoleId, name: "Administrator");
        _securityRepo.Setup(r => r.GetRoleByIdAsync(RoleId)).ReturnsAsync(() => role);
        _metaverseRepo.Setup(r => r.GetMetaverseObjectAsync(member.Id)).ReturnsAsync(member);
        _securityRepo.Setup(r => r.AddObjectToRoleByIdAsync(member.Id, RoleId))
            .Callback(() => role.StaticMembers.Add(member))
            .Returns(Task.CompletedTask);
        SetupMaxVersion(0);

        await _jim.Security.AddObjectToRoleByIdAsync(member.Id, RoleId, NewUser(), changeReason: "granting access");

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.TargetType, Is.EqualTo(ActivityTargetType.Role));
        Assert.That(_completedActivity!.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Update));
        Assert.That(_completedActivity!.TargetName, Is.EqualTo("Administrator"));
        Assert.That(_completedActivity!.InitiatedByType, Is.EqualTo(ActivityInitiatorType.User));
        Assert.That(_completedActivity!.RoleId, Is.EqualTo(RoleId));
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.EqualTo(1));
        Assert.That(_completedActivity!.ChangeReason, Is.EqualTo("granting access"));
        Assert.That(_completedActivity!.Message, Does.Contain("Jane Smith").And.Contain("Administrator"));
        var snapshot = _completedActivity!.ConfigurationChangeSnapshot;
        Assert.That(snapshot, Is.Not.Null);
        Assert.That(snapshot, Does.Contain("Jane Smith"));
        Assert.That(snapshot, Does.Contain(member.Id.ToString("D")));
    }

    [Test]
    public async Task AddObjectToRoleByIdAsync_ApiKeyInitiated_AttributesActivityToTheInitiatingKeyAsync()
    {
        var member = NewMember("Jane Smith");
        var role = BuildRole(id: RoleId, name: "Administrator");
        _securityRepo.Setup(r => r.GetRoleByIdAsync(RoleId)).ReturnsAsync(() => role);
        _metaverseRepo.Setup(r => r.GetMetaverseObjectAsync(member.Id)).ReturnsAsync(member);
        _securityRepo.Setup(r => r.AddObjectToRoleByIdAsync(member.Id, RoleId))
            .Callback(() => role.StaticMembers.Add(member))
            .Returns(Task.CompletedTask);
        SetupMaxVersion(0);

        var initiatingKey = new ApiKey { Id = Guid.NewGuid(), Name = "provisioning-key" };
        await _jim.Security.AddObjectToRoleByIdAsync(member.Id, RoleId, initiatingKey);

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.InitiatedByType, Is.EqualTo(ActivityInitiatorType.ApiKey));
        Assert.That(_completedActivity!.InitiatedById, Is.EqualTo(initiatingKey.Id));
        Assert.That(_completedActivity!.RoleId, Is.EqualTo(RoleId));
    }

    [Test]
    public async Task AddObjectToRoleByIdAsync_WhenTrackingDisabled_RecordsActivityButNoSnapshotAsync()
    {
        SetupTrackingSetting(enabled: false);
        var member = NewMember("Jane Smith");
        var role = BuildRole(id: RoleId, name: "Administrator");
        _securityRepo.Setup(r => r.GetRoleByIdAsync(RoleId)).ReturnsAsync(() => role);
        _metaverseRepo.Setup(r => r.GetMetaverseObjectAsync(member.Id)).ReturnsAsync(member);
        _securityRepo.Setup(r => r.AddObjectToRoleByIdAsync(member.Id, RoleId))
            .Callback(() => role.StaticMembers.Add(member))
            .Returns(Task.CompletedTask);

        await _jim.Security.AddObjectToRoleByIdAsync(member.Id, RoleId, NewUser(), changeReason: "no tracking");

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.TargetType, Is.EqualTo(ActivityTargetType.Role));
        Assert.That(_completedActivity!.ConfigurationChangeSnapshot, Is.Null);
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.Null);
        Assert.That(_completedActivity!.ChangeReason, Is.EqualTo("no tracking"));
    }

    [Test]
    public async Task AddObjectToRoleByIdAsync_WhenResaveIsUnchanged_SkipsVersionAndSnapshotAsync()
    {
        var member = NewMember("Jane Smith");
        var role = BuildRole(id: RoleId, name: "Administrator");
        _securityRepo.Setup(r => r.GetRoleByIdAsync(RoleId)).ReturnsAsync(() => role);
        _metaverseRepo.Setup(r => r.GetMetaverseObjectAsync(member.Id)).ReturnsAsync(member);

        // The mock is deliberately idempotent (unlike the real repository, which throws for a genuine duplicate
        // add) so this test isolates the capture layer's semantic dedupe guard from the repository's own
        // duplicate-membership validation, which is covered separately at the controller/repository level.
        _securityRepo.Setup(r => r.AddObjectToRoleByIdAsync(member.Id, RoleId))
            .Callback(() =>
            {
                if (role.StaticMembers.All(m => m.Id != member.Id))
                    role.StaticMembers.Add(member);
            })
            .Returns(Task.CompletedTask);
        SetupMaxVersion(0);

        await _jim.Security.AddObjectToRoleByIdAsync(member.Id, RoleId, NewUser(), changeReason: "first add");
        var storedSnapshot = _completedActivity!.ConfigurationChangeSnapshot;
        Assert.That(storedSnapshot, Is.Not.Null);
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.EqualTo(1));
        _activityRepo.Setup(r => r.GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType.Role, RoleId))
            .ReturnsAsync(storedSnapshot);
        _completedActivity = null;

        await _jim.Security.AddObjectToRoleByIdAsync(member.Id, RoleId, NewUser(), changeReason: "resend, already a member");

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.Null, "an unchanged membership must not consume a version");
        Assert.That(_completedActivity!.ConfigurationChangeSnapshot, Is.Null);
        Assert.That(_completedActivity!.RoleId, Is.EqualTo(RoleId), "the activity still deep-links to the Role when the capture is skipped");
        Assert.That(_completedActivity!.ChangeReason, Is.EqualTo("resend, already a member"));
    }

    // -- RemoveObjectFromRoleAsync ----------------------------------------------------------------------------------

    [Test]
    public async Task RemoveObjectFromRoleAsync_RecordsNewVersionWithoutRemovedMemberAsync()
    {
        var remainingMember = NewMember("Bob Jones");
        var removedMember = NewMember("Jane Smith");
        var role = BuildRole(id: RoleId, name: "Administrator", members: [remainingMember, removedMember]);
        _securityRepo.Setup(r => r.GetRoleByIdAsync(RoleId)).ReturnsAsync(() => role);
        _metaverseRepo.Setup(r => r.GetMetaverseObjectAsync(removedMember.Id)).ReturnsAsync(removedMember);
        _securityRepo.Setup(r => r.RemoveObjectFromRoleAsync(removedMember.Id, RoleId))
            .Callback(() => role.StaticMembers.RemoveAll(m => m.Id == removedMember.Id))
            .Returns(Task.CompletedTask);
        SetupMaxVersion(1);

        await _jim.Security.RemoveObjectFromRoleAsync(removedMember.Id, RoleId, NewUser(), changeReason: "revoking access");

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.TargetType, Is.EqualTo(ActivityTargetType.Role));
        Assert.That(_completedActivity!.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Update));
        Assert.That(_completedActivity!.RoleId, Is.EqualTo(RoleId));
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.EqualTo(2), "version is the existing maximum (1) + 1");
        Assert.That(_completedActivity!.Message, Does.Contain("Jane Smith").And.Contain("Administrator"));
        var snapshot = _completedActivity!.ConfigurationChangeSnapshot;
        Assert.That(snapshot, Is.Not.Null);
        Assert.That(snapshot, Does.Contain("Bob Jones"));
        Assert.That(snapshot, Does.Not.Contain("Jane Smith"), "the removed member must not still appear in the new version");
    }

    // -- AddObjectToRoleAsync (name-keyed) --------------------------------------------------------------------------

    [Test]
    public async Task AddObjectToRoleAsync_NoInitiator_RecordsSystemAttributedActivityAsync()
    {
        var member = NewMember("Jay Admin");
        var role = BuildRole(id: RoleId, name: Constants.BuiltInRoles.Administrator);
        _securityRepo.Setup(r => r.GetRoleAsync(Constants.BuiltInRoles.Administrator)).ReturnsAsync(() => role);
        _securityRepo.Setup(r => r.GetRoleByIdAsync(RoleId)).ReturnsAsync(() => role);
        _securityRepo.Setup(r => r.AddObjectToRoleAsync(member.Id, Constants.BuiltInRoles.Administrator))
            .Callback(() => role.StaticMembers.Add(member))
            .Returns(Task.CompletedTask);
        SetupMaxVersion(0);

        // Mirrors AuthServer's initial-admin/retention bootstrap path (AuthServer.cs), which has no separate
        // initiator to attribute the change to.
        await _jim.Security.AddObjectToRoleAsync(member, Constants.BuiltInRoles.Administrator);

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.TargetType, Is.EqualTo(ActivityTargetType.Role));
        Assert.That(_completedActivity!.InitiatedByType, Is.EqualTo(ActivityInitiatorType.System));
        Assert.That(_completedActivity!.RoleId, Is.EqualTo(RoleId));
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.EqualTo(1));
        Assert.That(_completedActivity!.ConfigurationChangeSnapshot, Does.Contain("Jay Admin"));
    }

    // -- SeedBuiltInRolesAsync --------------------------------------------------------------------------------------

    [Test]
    public async Task SeedBuiltInRolesAsync_NoRoleExists_CreatesAdministratorRoleThroughAuditedPathAsync()
    {
        _securityRepo.Setup(r => r.GetRoleAsync(Constants.BuiltInRoles.Administrator)).ReturnsAsync((Role?)null);
        Role? createdRole = null;
        _securityRepo.Setup(r => r.CreateRoleAsync(It.IsAny<Role>()))
            .Callback<Role>(r => createdRole = r)
            .ReturnsAsync((Role r) => r);
        _securityRepo.Setup(r => r.GetRoleByIdAsync(It.IsAny<int>())).ReturnsAsync(() => createdRole);
        SetupMaxVersion(0);

        await _jim.Seeding.SeedBuiltInRolesAsync();

        Assert.That(createdRole, Is.Not.Null, "the built-in Administrator Role must be created");
        Assert.That(createdRole!.BuiltIn, Is.True);
        Assert.That(createdRole.Name, Is.EqualTo(Constants.BuiltInRoles.Administrator));
        Assert.That(createdRole.CreatedByType, Is.EqualTo(ActivityInitiatorType.System));

        Assert.That(_completedActivity, Is.Not.Null, "seeding must record a Create Activity for the built-in Role");
        Assert.That(_completedActivity!.TargetType, Is.EqualTo(ActivityTargetType.Role));
        Assert.That(_completedActivity!.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Create));
        Assert.That(_completedActivity!.InitiatedByType, Is.EqualTo(ActivityInitiatorType.System));
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.EqualTo(1),
            "the seeded creation must be version 1 of the Role's configuration change history");
        Assert.That(_completedActivity!.ConfigurationChangeSnapshot, Does.Contain("\"objectType\":\"Role\""));
        Assert.That(_completedActivity!.ChangeReason, Is.Not.Null.And.Not.Empty,
            "the seeded creation should explain its provenance in the change history");

        // The seeded creation must be grouped under a single System Initialisation parent Activity, so a fresh
        // deployment's built-in configuration appears as one top-level Activity, not one row per seeded object.
        var roleActivity = _createdActivities.Single(a => a.TargetType == ActivityTargetType.Role);
        var parentActivity = _createdActivities.SingleOrDefault(a => a.TargetType == ActivityTargetType.SystemInitialisation);
        Assert.That(parentActivity, Is.Not.Null,
            "seeding must record a parent System Initialisation Activity when it creates the built-in Role");
        Assert.That(roleActivity.ParentActivityId, Is.EqualTo(parentActivity!.Id));
    }

    [Test]
    public async Task SeedBuiltInRolesAsync_RoleAlreadyExists_DoesNothingAsync()
    {
        var existing = BuildRole(id: RoleId, name: Constants.BuiltInRoles.Administrator, builtIn: true);
        _securityRepo.Setup(r => r.GetRoleAsync(Constants.BuiltInRoles.Administrator)).ReturnsAsync(existing);

        await _jim.Seeding.SeedBuiltInRolesAsync();

        _securityRepo.Verify(r => r.CreateRoleAsync(It.IsAny<Role>()), Times.Never,
            "seeding is idempotent: an existing built-in Role must not be recreated");
        _activityRepo.Verify(r => r.CreateActivityAsync(It.IsAny<Activity>()), Times.Never);
    }

    // -- helpers -------------------------------------------------------------------------------------------------------

    private static readonly byte[] HashKeyBytes = new byte[32];

    private void SetupTrackingSetting(bool enabled) =>
        _settingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled))
            .ReturnsAsync(new ServiceSetting
            {
                Key = Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled,
                DisplayName = "Track configuration changes",
                ValueType = ServiceSettingValueType.Boolean,
                Value = enabled ? "true" : "false"
            });

    private void SetupHashKeySetting() =>
        _settingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ConfigurationChangeHashKey))
            .ReturnsAsync(new ServiceSetting
            {
                Key = Constants.SettingKeys.ConfigurationChangeHashKey,
                DisplayName = "Configuration change hash key",
                ValueType = ServiceSettingValueType.StringEncrypted,
                Value = _protection.Protect(Convert.ToBase64String(HashKeyBytes))
            });

    private void SetupMaxVersion(int max) =>
        _activityRepo.Setup(r => r.GetMaxConfigurationChangeVersionAsync(ActivityTargetType.Role, It.IsAny<int>()))
            .ReturnsAsync(max);

    private static Role BuildRole(int? id = null, string? name = null, bool builtIn = false, List<MetaverseObject>? members = null) => new()
    {
        Id = id ?? RoleId,
        Name = name ?? "Administrator",
        BuiltIn = builtIn,
        StaticMembers = members ?? []
    };

    private static MetaverseObject NewUser() => new() { Id = Guid.NewGuid(), CachedDisplayName = "Admin User" };

    private static MetaverseObject NewMember(string displayName) => new() { Id = Guid.NewGuid(), CachedDisplayName = displayName };

    /// <summary>A round-trip credential-protection test double using a recognisable encrypted-value prefix.</summary>
    private sealed class FakeProtection : ICredentialProtectionService
    {
        private const string Prefix = "$JIM$v1$";

        public string? Protect(string? plainText) =>
            string.IsNullOrEmpty(plainText) ? plainText : Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(plainText));

        public string? Unprotect(string? protectedData) =>
            string.IsNullOrEmpty(protectedData) || !IsProtected(protectedData)
                ? protectedData
                : Encoding.UTF8.GetString(Convert.FromBase64String(protectedData[Prefix.Length..]));

        public bool IsProtected(string? value) =>
            !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);
    }
}
