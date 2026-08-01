// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Services;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Container selection decides which parts of a directory a Connected System imports from, so changing it changes
/// what synchronisation sees. Containers are the one collection whose every scalar is Cosmetic (a name, an external
/// id, a hidden flag) while the collection they hang from is not, which made them the case that proved the preflight
/// and the change history were reducing the same diff two different ways: the save went through in silence and the
/// change history then recorded it as synchronisation-affecting.
///
/// These cover both halves: that container selection asks before it saves, and that whatever class the administrator
/// is asked to acknowledge is the class the change history goes on to record.
/// </summary>
[TestFixture]
public class ContainerSelectionClassificationTests
{
    private Mock<IRepository> _repo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<IServiceSettingsRepository> _serviceSettingsRepo = null!;
    private JimApplication _jim = null!;
    private ConfigurationSnapshotService _snapshots = null!;

    private const int ConnectedSystemId = 3;
    private static readonly byte[] HashKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();
        _repo = new Mock<IRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _serviceSettingsRepo = new Mock<IServiceSettingsRepository>();
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _repo.Setup(r => r.ServiceSettings).Returns(_serviceSettingsRepo.Object);

        _serviceSettingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled))
            .ReturnsAsync(new ServiceSetting
            {
                Key = Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled,
                ValueType = ServiceSettingValueType.Boolean,
                Value = "true"
            });
        _serviceSettingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ConfigurationChangeHashKey))
            .ReturnsAsync(new ServiceSetting
            {
                Key = Constants.SettingKeys.ConfigurationChangeHashKey,
                ValueType = ServiceSettingValueType.StringEncrypted,
                Value = Convert.ToBase64String(HashKey)
            });

        _jim = new JimApplication(_repo.Object);
        _snapshots = _jim.ConfigurationSnapshots;
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    #region What the administrator is asked before saving

    [Test]
    public async Task EvaluateConnectedSystemAsync_DeselectingAContainer_AsksForAcknowledgementAsync()
    {
        SetStoredBaseline(SystemWithContainers(("OU=Users", true), ("OU=Service Accounts", true)));
        var proposed = SystemWithContainers(("OU=Users", true), ("OU=Service Accounts", false));

        var result = await _jim.ConfigurationChangePreflight.EvaluateConnectedSystemAsync(proposed);

        Assert.That(result.RequiresAcknowledgement, Is.True,
            "narrowing what a Connected System imports from changes what synchronisation sees, so it cannot save in silence");
    }

    [Test]
    public async Task EvaluateConnectedSystemAsync_SelectingAContainer_AsksForAcknowledgementAsync()
    {
        SetStoredBaseline(SystemWithContainers(("OU=Users", true), ("OU=Contractors", false)));
        var proposed = SystemWithContainers(("OU=Users", true), ("OU=Contractors", true));

        var result = await _jim.ConfigurationChangePreflight.EvaluateConnectedSystemAsync(proposed);

        Assert.Multiple(() =>
        {
            Assert.That(result.RequiresAcknowledgement, Is.True, "a wider import scope is a synchronisation-affecting change");
            Assert.That(result.IsDestructive, Is.False, "nothing leaves scope, so nothing existing is at risk");
        });
    }

    [Test]
    public async Task EvaluateConnectedSystemAsync_ContainerRenamedInTheDirectory_IsNotDestructiveAsync()
    {
        // A rename is discovery, not an administrator taking objects out of scope. Sweeping it up with deselection
        // would raise a destructive warning every time a directory tidies its OU names.
        SetStoredBaseline(SystemWithContainers(("OU=Users", true)));
        var proposed = SystemWithContainers(("OU=Colleagues", true));

        var result = await _jim.ConfigurationChangePreflight.EvaluateConnectedSystemAsync(proposed);

        Assert.That(result.IsDestructive, Is.False);
    }

    #endregion

    #region The preflight and the change history must reach the same verdict

    /// <summary>
    /// The invariant that container selection broke, asserted over the change shapes a Connected System's partition
    /// tab can produce. An administrator consents to a class; the change history records a class. If those can differ
    /// then one of the two is lying, and the acknowledgement is the one nobody can audit after the fact.
    /// </summary>
    [TestCase("deselected", TestName = "PreflightClassMatchesRecordedClass_ContainerDeselected")]
    [TestCase("selected", TestName = "PreflightClassMatchesRecordedClass_ContainerSelected")]
    [TestCase("renamed", TestName = "PreflightClassMatchesRecordedClass_ContainerRenamed")]
    public async Task EvaluateConnectedSystemAsync_ClassMatchesTheClassCaptureWillRecordAsync(string change)
    {
        var (before, after) = change switch
        {
            "deselected" => (SystemWithContainers(("OU=Users", true), ("OU=Service Accounts", true)),
                             SystemWithContainers(("OU=Users", true), ("OU=Service Accounts", false))),
            "selected" => (SystemWithContainers(("OU=Users", true), ("OU=Contractors", false)),
                           SystemWithContainers(("OU=Users", true), ("OU=Contractors", true))),
            _ => (SystemWithContainers(("OU=Users", true)), SystemWithContainers(("OU=Colleagues", true)))
        };
        SetStoredBaseline(before);

        var result = await _jim.ConfigurationChangePreflight.EvaluateConnectedSystemAsync(after);

        Assert.That(result.HighestClass, Is.EqualTo(Classify(before, after)));
    }

    #endregion

    #region Helpers

    private void SetStoredBaseline(ConnectedSystem connectedSystem) =>
        _activityRepo.Setup(r => r.GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType.ConnectedSystem, ConnectedSystemId))
            .ReturnsAsync(ConfigurationSnapshotService.Serialise(_snapshots.CreateSnapshot(connectedSystem, HashKey)));

    private ConfigurationChangeClass Classify(ConnectedSystem before, ConnectedSystem after)
    {
        var diff = _jim.ConfigurationDiffs.Diff(_snapshots.CreateSnapshot(before, HashKey), _snapshots.CreateSnapshot(after, HashKey));
        return ConfigurationChangeClassifier.Classify(diff);
    }

    /// <summary>
    /// A Connected System with one selected partition holding the given containers. Container ids are stable across
    /// calls so the diff matches them up rather than reporting wholesale replacement.
    /// </summary>
    private static ConnectedSystem SystemWithContainers(params (string ExternalId, bool Selected)[] containers) => new()
    {
        Id = ConnectedSystemId,
        Name = "Corporate Directory",
        ConnectorDefinitionId = 1,
        Partitions =
        [
            new ConnectedSystemPartition
            {
                Id = 50,
                Name = "EMEA",
                ExternalId = "DC=emea",
                Selected = true,
                Containers = containers
                    .Select((c, index) => new ConnectedSystemContainer
                    {
                        Id = 100 + index,
                        Name = c.ExternalId,
                        ExternalId = c.ExternalId,
                        Selected = c.Selected
                    })
                    .ToHashSet()
            }
        ]
    };

    #endregion
}
