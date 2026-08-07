// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Connectors;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Security;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Covers whether a Container created during export is auto-selected, which decides whether the objects provisioned
/// into it ever import back.
/// </summary>
/// <remarks>
/// The rule turns on Container Scope. A selected Subtree ancestor's search already returns everything beneath it, so
/// selecting the new Container as well would import those objects twice. A selected OneLevel ancestor returns only the
/// objects held directly within it, so it does NOT reach into the new Container: leaving the new Container unselected
/// there means its objects are never imported and the export silently writes into a hole. Both auto-selection entry
/// points (the initiator-pair and the initiator-triad overloads) must apply the same rule.
/// </remarks>
[TestFixture]
public class ExportCreatedContainerAutoSelectionTests
{
    private Mock<IRepository> _repo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<IServiceSettingsRepository> _settingsRepo = null!;
    private Mock<IConnectedSystemRepository> _csRepo = null!;
    private FakeProtection _protection = null!;
    private JimApplication _jim = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _repo = new Mock<IRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _settingsRepo = new Mock<IServiceSettingsRepository>();
        _csRepo = new Mock<IConnectedSystemRepository>();
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _repo.Setup(r => r.ServiceSettings).Returns(_settingsRepo.Object);
        _repo.Setup(r => r.ConnectedSystems).Returns(_csRepo.Object);

        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.GetMaxConfigurationChangeVersionAsync(It.IsAny<ActivityTargetType>(), It.IsAny<int>())).ReturnsAsync(1);
        _activityRepo.Setup(r => r.GetLatestConfigurationChangeSnapshotAsync(It.IsAny<ActivityTargetType>(), It.IsAny<int>())).ReturnsAsync((string?)null);

        _csRepo.Setup(r => r.UpdateConnectedSystemAsync(It.IsAny<ConnectedSystem>())).Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.GetConnectedSystemAsync(1, It.IsAny<bool>())).ReturnsAsync(BuildConnectedSystem);

        _protection = new FakeProtection();
        _jim = new JimApplication(_repo.Object) { CredentialProtection = _protection };

        SetupTrackingSetting();
        SetupHashKeySetting();
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    // -- the OneLevel case: the ancestor's search does not reach the new Container -----------------------------------

    [Test]
    public async Task RefreshAndAutoSelectContainersAsync_BeneathASelectedOneLevelContainer_SelectsTheNewContainerAsync()
    {
        var corp = SelectedContainer("OU=Corp,DC=corp,DC=local", "Corp", ConnectedSystemContainerScope.OneLevel);
        var connectedSystem = SystemWithContainers(corp);

        await _jim.ConnectedSystems.RefreshAndAutoSelectContainersAsync(
            connectedSystem, new FakeContainerCreatingConnector(), ["OU=NewTeam,OU=Corp,DC=corp,DC=local"], initiatedByUser: NewUser());

        var newContainer = FindContainer(connectedSystem, "OU=NewTeam,OU=Corp,DC=corp,DC=local");
        Assert.That(newContainer, Is.Not.Null, "the container created during export must be added to the hierarchy");
        Assert.That(newContainer!.Selected, Is.True,
            "OU=Corp is selected OneLevel, so its search stops at the objects directly within it; leaving the new child unselected " +
            "means the objects just provisioned into it would never be imported back");
    }

    [Test]
    public async Task RefreshAndAutoSelectContainersWithTriadAsync_BeneathASelectedOneLevelContainer_SelectsTheNewContainerAsync()
    {
        var corp = SelectedContainer("OU=Corp,DC=corp,DC=local", "Corp", ConnectedSystemContainerScope.OneLevel);
        var connectedSystem = SystemWithContainers(corp);

        await _jim.ConnectedSystems.RefreshAndAutoSelectContainersWithTriadAsync(
            connectedSystem, new FakeContainerCreatingConnector(), ["OU=NewTeam,OU=Corp,DC=corp,DC=local"],
            ActivityInitiatorType.System, null, "Infrastructure Key");

        var newContainer = FindContainer(connectedSystem, "OU=NewTeam,OU=Corp,DC=corp,DC=local");
        Assert.That(newContainer, Is.Not.Null);
        Assert.That(newContainer!.Selected, Is.True,
            "both auto-selection overloads must apply the same scope-aware coverage rule");
    }

    // -- the Subtree cases: the ancestor's search already covers the new Container -----------------------------------

    [Test]
    public async Task RefreshAndAutoSelectContainersAsync_BeneathASelectedSubtreeContainer_DoesNotSelectTheNewContainerAsync()
    {
        var corp = SelectedContainer("OU=Corp,DC=corp,DC=local", "Corp", ConnectedSystemContainerScope.Subtree);
        var connectedSystem = SystemWithContainers(corp);

        await _jim.ConnectedSystems.RefreshAndAutoSelectContainersAsync(
            connectedSystem, new FakeContainerCreatingConnector(), ["OU=NewTeam,OU=Corp,DC=corp,DC=local"], initiatedByUser: NewUser());

        var newContainer = FindContainer(connectedSystem, "OU=NewTeam,OU=Corp,DC=corp,DC=local");
        Assert.That(newContainer, Is.Not.Null);
        Assert.That(newContainer!.Selected, Is.False,
            "OU=Corp's Subtree search already returns the new container's objects; selecting it as well would import them twice");
    }

    [Test]
    public async Task RefreshAndAutoSelectContainersAsync_BeneathAOneLevelParentUnderASubtreeGrandparent_DoesNotSelectTheNewContainerAsync()
    {
        // The grandparent's Subtree search reaches every level beneath it, whatever the intermediate containers say.
        var corp = SelectedContainer("OU=Corp,DC=corp,DC=local", "Corp", ConnectedSystemContainerScope.Subtree);
        var teams = SelectedContainer("OU=Teams,OU=Corp,DC=corp,DC=local", "Teams", ConnectedSystemContainerScope.OneLevel);
        corp.AddChildContainer(teams);
        var connectedSystem = SystemWithContainers(corp);

        await _jim.ConnectedSystems.RefreshAndAutoSelectContainersAsync(
            connectedSystem, new FakeContainerCreatingConnector(), ["OU=NewTeam,OU=Teams,OU=Corp,DC=corp,DC=local"], initiatedByUser: NewUser());

        var newContainer = FindContainer(connectedSystem, "OU=NewTeam,OU=Teams,OU=Corp,DC=corp,DC=local");
        Assert.That(newContainer, Is.Not.Null);
        Assert.That(newContainer!.Selected, Is.False,
            "coverage is inherited from any Subtree ancestor, not just the immediate parent");
    }

    // -- branches the administrator has not asked for stay out of scope ----------------------------------------------

    [Test]
    public async Task RefreshAndAutoSelectContainersAsync_BeneathAnUnselectedContainer_DoesNotSelectTheNewContainerAsync()
    {
        var corp = SelectedContainer("OU=Corp,DC=corp,DC=local", "Corp", ConnectedSystemContainerScope.Subtree);
        corp.Selected = false;
        var connectedSystem = SystemWithContainers(corp);

        await _jim.ConnectedSystems.RefreshAndAutoSelectContainersAsync(
            connectedSystem, new FakeContainerCreatingConnector(), ["OU=NewTeam,OU=Corp,DC=corp,DC=local"], initiatedByUser: NewUser());

        var newContainer = FindContainer(connectedSystem, "OU=NewTeam,OU=Corp,DC=corp,DC=local");
        Assert.That(newContainer, Is.Not.Null);
        Assert.That(newContainer!.Selected, Is.False,
            "nothing beneath OU=Corp is in scope, so a container created there must not put itself in scope");
    }

    [Test]
    public async Task RefreshAndAutoSelectContainersAsync_BeneathAnUnselectedParentOfAOneLevelGrandparent_DoesNotSelectTheNewContainerAsync()
    {
        // OU=Teams sits outside OU=Corp's OneLevel search, so the branch is out of scope entirely.
        var corp = SelectedContainer("OU=Corp,DC=corp,DC=local", "Corp", ConnectedSystemContainerScope.OneLevel);
        var teams = SelectedContainer("OU=Teams,OU=Corp,DC=corp,DC=local", "Teams", ConnectedSystemContainerScope.Subtree);
        teams.Selected = false;
        corp.AddChildContainer(teams);
        var connectedSystem = SystemWithContainers(corp);

        await _jim.ConnectedSystems.RefreshAndAutoSelectContainersAsync(
            connectedSystem, new FakeContainerCreatingConnector(), ["OU=NewTeam,OU=Teams,OU=Corp,DC=corp,DC=local"], initiatedByUser: NewUser());

        var newContainer = FindContainer(connectedSystem, "OU=NewTeam,OU=Teams,OU=Corp,DC=corp,DC=local");
        Assert.That(newContainer, Is.Not.Null);
        Assert.That(newContainer!.Selected, Is.False,
            "a selected OneLevel grandparent does not put its grandchildren in scope");
    }

    [Test]
    public async Task RefreshAndAutoSelectContainersAsync_AtTheTopOfASelectedPartition_SelectsTheNewContainerAsync()
    {
        var connectedSystem = SystemWithContainers();

        await _jim.ConnectedSystems.RefreshAndAutoSelectContainersAsync(
            connectedSystem, new FakeContainerCreatingConnector(), ["OU=NewTeam,DC=corp,DC=local"], initiatedByUser: NewUser());

        var newContainer = FindContainer(connectedSystem, "OU=NewTeam,DC=corp,DC=local");
        Assert.That(newContainer, Is.Not.Null);
        Assert.That(newContainer!.Selected, Is.True,
            "a top-level container in a selected partition has no ancestor covering it, so it needs selecting in its own right");
    }

    // -- helpers -----------------------------------------------------------------------------------------------------

    private static ConnectedSystemContainer SelectedContainer(string externalId, string name, ConnectedSystemContainerScope scope) =>
        new() { ExternalId = externalId, Name = name, Selected = true, Scope = scope };

    private static ConnectedSystem SystemWithContainers(params ConnectedSystemContainer[] rootContainers)
    {
        var connectedSystem = BuildConnectedSystem();
        connectedSystem.Partitions =
        [
            new ConnectedSystemPartition
            {
                Id = 20,
                ExternalId = "DC=corp,DC=local",
                Name = "corp.local",
                Selected = true,
                Containers = [.. rootContainers]
            }
        ];
        return connectedSystem;
    }

    private static ConnectedSystemContainer? FindContainer(ConnectedSystem connectedSystem, string externalId) =>
        (connectedSystem.Partitions ?? [])
            .SelectMany(p => Flatten(p.Containers ?? []))
            .SingleOrDefault(c => c.ExternalId == externalId);

    private static IEnumerable<ConnectedSystemContainer> Flatten(IEnumerable<ConnectedSystemContainer> containers) =>
        containers.SelectMany(c => new[] { c }.Concat(Flatten(c.ChildContainers)));

    private static ConnectedSystem BuildConnectedSystem() => new()
    {
        Id = 1,
        Name = "Identity Directory",
        ConnectorDefinition = new ConnectorDefinition { Name = ConnectorConstants.FileConnectorName },
        SettingValues =
        [
            new ConnectedSystemSettingValue
            {
                Id = 10,
                Setting = new ConnectorDefinitionSetting { Id = 100, Name = "File Path", Required = true, Type = ConnectedSystemSettingType.File },
                StringValue = "/data/users.csv"
            },
            new ConnectedSystemSettingValue
            {
                Id = 11,
                Setting = new ConnectorDefinitionSetting { Id = 101, Name = "Mode", Required = true, Type = ConnectedSystemSettingType.DropDown },
                StringValue = "Import Only"
            }
        ],
        RunProfiles = [],
        ObjectTypes = [],
        Partitions = []
    };

    private void SetupTrackingSetting() =>
        _settingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled))
            .ReturnsAsync(new ServiceSetting
            {
                Key = Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled,
                DisplayName = "Track configuration changes",
                ValueType = ServiceSettingValueType.Boolean,
                Value = "true"
            });

    private void SetupHashKeySetting() =>
        _settingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ConfigurationChangeHashKey))
            .ReturnsAsync(new ServiceSetting
            {
                Key = Constants.SettingKeys.ConfigurationChangeHashKey,
                DisplayName = "Configuration change hash key",
                ValueType = ServiceSettingValueType.StringEncrypted,
                Value = _protection.Protect(Convert.ToBase64String(new byte[32]))
            });

    private static MetaverseObject NewUser() => new() { Id = Guid.NewGuid(), CachedDisplayName = "Admin User" };

    /// <summary>A connector test double that reports container-creation support for the auto-selection path.</summary>
    private sealed class FakeContainerCreatingConnector : IConnector, IConnectorContainerCreation
    {
        public string Name => "Fake Directory";
        public string? Description => null;
        public string? Url => null;
        public IReadOnlyList<string> CreatedContainerExternalIds { get; } = [];
        public Task<bool> VerifyContainerExistsAsync(string containerExternalId) => Task.FromResult(true);
        public string? GetParentContainerExternalId(string containerExternalId) =>
            containerExternalId.Contains(',') ? containerExternalId[(containerExternalId.IndexOf(',') + 1)..] : null;
        public string GetContainerDisplayName(string containerExternalId) =>
            containerExternalId.Split(',')[0].Split('=')[^1];
    }

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
