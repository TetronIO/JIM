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
    public async Task EvaluateConnectedSystemAsync_DeselectingAContainer_StatesTheConsequenceAsync()
    {
        SetStoredBaseline(SystemWithContainers(("OU=Users", true), ("OU=Service Accounts", true)));
        var proposed = SystemWithContainers(("OU=Users", true), ("OU=Service Accounts", false));

        var result = await _jim.ConfigurationChangePreflight.EvaluateConnectedSystemAsync(proposed);

        var item = result.DestructiveItems.SingleOrDefault();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsDestructive, Is.True,
                "deselecting a container removes its objects from scope, exactly as deselecting a partition does");
            Assert.That(item?.Label, Does.Contain("OU=Service Accounts"),
                "the administrator needs to know which container they are removing from scope");
            Assert.That(item?.Consequence, Is.Not.Null.And.Not.Empty,
                "a destructive change with no stated consequence is a dialog nobody can weigh");
        }
    }

    [Test]
    public async Task EvaluateConnectedSystemAsync_DeselectingAContainer_ReportsItAsOneChangeAsync()
    {
        // The container's name, external id and hidden flag all disappear together. Listing them as three separate
        // changes describes the mechanism rather than the act, and buries the one line that matters.
        SetStoredBaseline(SystemWithContainers(("OU=Users", true), ("OU=Service Accounts", true)));
        var proposed = SystemWithContainers(("OU=Users", true), ("OU=Service Accounts", false));

        var result = await _jim.ConfigurationChangePreflight.EvaluateConnectedSystemAsync(proposed);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Items, Has.Count.EqualTo(1));
            Assert.That(result.Items[0].ChangeType, Is.EqualTo(ConfigurationDiffChangeType.Removed));
        }
    }

    [Test]
    public async Task EvaluateConnectedSystemAsync_SelectingAContainer_AsksForAcknowledgementAsync()
    {
        SetStoredBaseline(SystemWithContainers(("OU=Users", true), ("OU=Contractors", false)));
        var proposed = SystemWithContainers(("OU=Users", true), ("OU=Contractors", true));

        var result = await _jim.ConfigurationChangePreflight.EvaluateConnectedSystemAsync(proposed);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.RequiresAcknowledgement, Is.True, "a wider import scope is a synchronisation-affecting change");
            Assert.That(result.IsDestructive, Is.False, "nothing leaves scope, so nothing existing is at risk");
            Assert.That(result.Items.Single().ChangeType, Is.EqualTo(ConfigurationDiffChangeType.Added));
        }
    }

    [Test]
    public async Task EvaluateConnectedSystemAsync_NarrowingAContainerToOneLevel_StatesTheConsequenceAsync()
    {
        SetStoredBaseline(SystemWithScopedContainers(("OU=Users", true, ConnectedSystemContainerScope.Subtree)));
        var proposed = SystemWithScopedContainers(("OU=Users", true, ConnectedSystemContainerScope.OneLevel));

        var result = await _jim.ConfigurationChangePreflight.EvaluateConnectedSystemAsync(proposed);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsDestructive, Is.True,
                "narrowing to one level removes everything below that level from scope");
            Assert.That(result.DestructiveItems.SingleOrDefault()?.Consequence, Is.Not.Null.And.Not.Empty,
                "a destructive change with no stated consequence is a dialog nobody can weigh");
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

    #region What is recorded in the change history

    [Test]
    public void Classify_ContainerDeselected_IsDestructive()
    {
        var classification = Classify(
            SystemWithContainers(("OU=Users", true), ("OU=Service Accounts", true)),
            SystemWithContainers(("OU=Users", true), ("OU=Service Accounts", false)));

        Assert.That(classification, Is.EqualTo(ConfigurationChangeClass.Destructive));
    }

    [Test]
    public void Classify_ContainerRenamedInTheDirectory_IsNotDestructive()
    {
        // The distinction the removal classification turns on: a container that changed is not a container that left.
        var classification = Classify(
            SystemWithContainers(("OU=Users", true)),
            SystemWithContainers(("OU=Colleagues", true)));

        Assert.That(classification, Is.Not.EqualTo(ConfigurationChangeClass.Destructive));
    }

    [Test]
    public void Classify_ContainerNarrowedToOneLevel_IsDestructive()
    {
        // Narrowing a container's scope takes every object below its own level out of scope. That is the same act
        // as deselecting the containers beneath it, and it has to be classified the same way; treating it as one
        // more cosmetic container scalar would let it save in silence.
        var classification = Classify(
            SystemWithScopedContainers(("OU=Users", true, ConnectedSystemContainerScope.Subtree)),
            SystemWithScopedContainers(("OU=Users", true, ConnectedSystemContainerScope.OneLevel)));

        Assert.That(classification, Is.EqualTo(ConfigurationChangeClass.Destructive));
    }

    [Test]
    public void Classify_ContainerSelectedWithOneLevelScope_IsNotDestructive()
    {
        // A newly selected container brings its whole node into the snapshot, scope included, so the scope scalar
        // arrives as an addition. Nothing has left scope, and classifying the arrival destructively would make
        // selecting any container at all raise a destructive warning.
        var classification = Classify(
            SystemWithScopedContainers(("OU=Users", true, ConnectedSystemContainerScope.Subtree), ("OU=Contractors", false, ConnectedSystemContainerScope.Subtree)),
            SystemWithScopedContainers(("OU=Users", true, ConnectedSystemContainerScope.Subtree), ("OU=Contractors", true, ConnectedSystemContainerScope.OneLevel)));

        Assert.That(classification, Is.Not.EqualTo(ConfigurationChangeClass.Destructive));
    }

    [Test]
    public void Classify_ContainerScopeUnchanged_IsNotDestructive()
    {
        // The guard against classifying every container edit destructively: scope has to have actually moved.
        var classification = Classify(
            SystemWithScopedContainers(("OU=Users", true, ConnectedSystemContainerScope.OneLevel)),
            SystemWithScopedContainers(("OU=Users", true, ConnectedSystemContainerScope.OneLevel)));

        Assert.That(classification, Is.Not.EqualTo(ConfigurationChangeClass.Destructive));
    }

    #endregion

    #region Exclusions (#1255)

    [Test]
    public void Snapshot_AnExcludedContainer_IsCaptured()
    {
        // An exclusion is not a selection, and the snapshot used to capture only selections, so carving a branch out
        // of a managed system left no trace in the configuration history at all: nothing to acknowledge before
        // saving, nothing to audit afterwards, and nothing to roll back to.
        var snapshot = _snapshots.CreateSnapshot(SystemWithCarveOut(excluded: true, reIncluded: false), HashKey);

        Assert.That(ConfigurationSnapshotService.Serialise(snapshot), Does.Contain("OU=Service Accounts"));
    }

    [Test]
    public void Snapshot_AReInclusionInsideAnExcludedBranch_IsCaptured()
    {
        // The path to a stated Container runs through Containers that state nothing themselves, and through the
        // exclusion above it. Capturing only what is stated at each level loses the whole branch below the first
        // silent one, which is exactly where a re-inclusion lives.
        var snapshot = _snapshots.CreateSnapshot(SystemWithCarveOut(excluded: true, reIncluded: true), HashKey);

        Assert.That(ConfigurationSnapshotService.Serialise(snapshot), Does.Contain("OU=App1"));
    }

    [Test]
    public void Classify_ContainerExcluded_IsDestructive()
    {
        // Carving a branch out of a selection takes its objects out of scope, exactly as deselecting a Container or
        // narrowing one to One Level does, and has to be acknowledged the same way.
        var classification = Classify(
            SystemWithCarveOut(excluded: false, reIncluded: false),
            SystemWithCarveOut(excluded: true, reIncluded: false));

        Assert.That(classification, Is.EqualTo(ConfigurationChangeClass.Destructive));
    }

    [Test]
    public void Classify_ContainerExclusionCleared_IsClassifiedTheSameWayAsWideningAContainersScope()
    {
        // Handing a branch back takes nothing out of scope, so this is over-classified, deliberately and for exactly
        // the reason widening a container's scope is: the classifier decides on the key and how it changed, never on
        // the values, and splitting one key by direction would mean threading values through both the classifier and
        // the preflight, whose agreement is the invariant this fixture exists to hold. The consequence text is what
        // tells the two directions apart.
        var classification = Classify(
            SystemWithCarveOut(excluded: true, reIncluded: false),
            SystemWithCarveOut(excluded: false, reIncluded: false));

        Assert.That(classification, Is.EqualTo(ConfigurationChangeClass.Destructive));
    }

    [Test]
    public async Task EvaluateConnectedSystemAsync_ExcludingAContainer_ReadsAsAnExclusionRatherThanAnAdditionAsync()
    {
        // A carved-out container arrives in the snapshot as a whole node, exactly as a selected one does, because
        // neither was there before. Read from the arrival alone the confirmation tells an administrator that a
        // container was "Added" and that its objects are coming into scope, at the moment they are leaving it. What
        // the node states has to decide how the act is described.
        SetStoredBaseline(SystemWithCarveOut(excluded: false, reIncluded: false));

        var result = await _jim.ConfigurationChangePreflight.EvaluateConnectedSystemAsync(
            SystemWithCarveOut(excluded: true, reIncluded: false));

        var item = result.Items.Single(i => i.Label.EndsWith("OU=Service Accounts", StringComparison.Ordinal));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(item.CollectionItemVerb, Is.EqualTo("Excluded"));
            Assert.That(item.Consequence, Does.StartWith("Excluding this container"));
            Assert.That(item.Class, Is.EqualTo(ConfigurationChangeClass.Destructive));
        }
    }

    [Test]
    public async Task EvaluateConnectedSystemAsync_SelectingAContainer_StillReadsAsAnAdditionAsync()
    {
        // The guard on the rule above: a container arriving because it was selected is described exactly as before.
        SetStoredBaseline(SystemWithContainers(("OU=Users", true), ("OU=Contractors", false)));

        var result = await _jim.ConfigurationChangePreflight.EvaluateConnectedSystemAsync(
            SystemWithContainers(("OU=Users", true), ("OU=Contractors", true)));

        var item = result.Items.Single(i => i.Label.EndsWith("OU=Contractors", StringComparison.Ordinal));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(item.CollectionItemVerb, Is.Null, "nothing overrides the plain Added the dialog already shows");
            Assert.That(item.Consequence, Does.StartWith("Selecting this container"));
        }
    }

    [Test]
    public async Task EvaluateConnectedSystemAsync_ExcludingAContainer_AsksBeforeItSavesAsync()
    {
        var before = SystemWithCarveOut(excluded: false, reIncluded: false);
        var after = SystemWithCarveOut(excluded: true, reIncluded: false);
        SetStoredBaseline(before);

        var result = await _jim.ConfigurationChangePreflight.EvaluateConnectedSystemAsync(after);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.HighestClass, Is.EqualTo(Classify(before, after)),
                "the class acknowledged must be the class the change history goes on to record");
            Assert.That(result.HighestClass, Is.EqualTo(ConfigurationChangeClass.Destructive));
        }
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
    [TestCase("narrowed", TestName = "PreflightClassMatchesRecordedClass_ContainerNarrowedToOneLevel")]
    [TestCase("widened", TestName = "PreflightClassMatchesRecordedClass_ContainerWidenedToSubtree")]
    public async Task EvaluateConnectedSystemAsync_ClassMatchesTheClassCaptureWillRecordAsync(string change)
    {
        var (before, after) = change switch
        {
            "deselected" => (SystemWithContainers(("OU=Users", true), ("OU=Service Accounts", true)),
                             SystemWithContainers(("OU=Users", true), ("OU=Service Accounts", false))),
            "selected" => (SystemWithContainers(("OU=Users", true), ("OU=Contractors", false)),
                           SystemWithContainers(("OU=Users", true), ("OU=Contractors", true))),
            "narrowed" => (SystemWithScopedContainers(("OU=Users", true, ConnectedSystemContainerScope.Subtree)),
                           SystemWithScopedContainers(("OU=Users", true, ConnectedSystemContainerScope.OneLevel))),
            "widened" => (SystemWithScopedContainers(("OU=Users", true, ConnectedSystemContainerScope.OneLevel)),
                          SystemWithScopedContainers(("OU=Users", true, ConnectedSystemContainerScope.Subtree))),
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
    /// A managed branch with a Container inside it optionally carved out, and a Container inside that optionally
    /// brought back. Ids are stable across calls so the diff matches the containers up rather than reporting
    /// wholesale replacement.
    /// </summary>
    private static ConnectedSystem SystemWithCarveOut(bool excluded, bool reIncluded)
    {
        var app1 = new ConnectedSystemContainer
        {
            Id = 202,
            Name = "OU=App1",
            ExternalId = "OU=App1,OU=Service Accounts,OU=Corp,DC=emea",
            Selected = reIncluded
        };
        var serviceAccounts = new ConnectedSystemContainer
        {
            Id = 201,
            Name = "OU=Service Accounts",
            ExternalId = "OU=Service Accounts,OU=Corp,DC=emea",
            Excluded = excluded
        };
        serviceAccounts.AddChildContainer(app1);

        var corp = new ConnectedSystemContainer
        {
            Id = 200,
            Name = "OU=Corp",
            ExternalId = "OU=Corp,DC=emea",
            Selected = true
        };
        corp.AddChildContainer(serviceAccounts);

        return new ConnectedSystem
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
                    Containers = [corp]
                }
            ]
        };
    }

    /// <summary>
    /// A Connected System with one selected partition holding the given containers. Container ids are stable across
    /// calls so the diff matches them up rather than reporting wholesale replacement.
    /// </summary>
    private static ConnectedSystem SystemWithContainers(params (string ExternalId, bool Selected)[] containers) =>
        SystemWithScopedContainers(containers.Select(c => (c.ExternalId, c.Selected, ConnectedSystemContainerScope.Subtree)).ToArray());

    /// <summary>
    /// As <see cref="SystemWithContainers"/>, with each container's scope stated rather than defaulted.
    /// </summary>
    private static ConnectedSystem SystemWithScopedContainers(params (string ExternalId, bool Selected, ConnectedSystemContainerScope Scope)[] containers) => new()
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
                        Selected = c.Selected,
                        Scope = c.Scope
                    })
                    .ToHashSet()
            }
        ]
    };

    #endregion
}
