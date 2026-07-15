// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Orchestration tests for the custom Metaverse Object Type deletion lifecycle added in #376: the two hard blocks
/// (Metaverse Objects of the type, and Synchronisation Rules targeting it), built-in protection, and the
/// references-cascade delete with parent + child Activity audit. These exercise the
/// <see cref="JIM.Application.Servers.MetaverseServer"/> logic over a mocked repository; the repository queries
/// themselves (per-type object count, reference discovery, cascade behaviour) are verified against real PostgreSQL in
/// <c>MetaverseObjectTypeDeletionDatabaseTests</c>.
/// </summary>
[TestFixture]
public class MetaverseServerObjectTypeDeletionTests
{
    private Mock<IRepository> _repo = null!;
    private Mock<IMetaverseRepository> _metaverseRepo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private List<Activity> _createdActivities = null!;
    private JimApplication _jim = null!;

    [SetUp]
    public void SetUp()
    {
        _repo = new Mock<IRepository>();
        _metaverseRepo = new Mock<IMetaverseRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _repo.Setup(r => r.Metaverse).Returns(_metaverseRepo.Object);
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);

        _createdActivities = [];
        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(_createdActivities.Add)
            .Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);

        _jim = new JimApplication(_repo.Object);
    }

    [TearDown]
    public void TearDown() => _jim.Dispose();

    private static MetaverseObjectType ObjectType(int id = 5, string name = "Device", bool builtIn = false) =>
        new() { Id = id, Name = name, PluralName = name + "s", BuiltIn = builtIn };

    private static ObjectTypeReference Reference(ObjectTypeReferenceKind kind, string description) =>
        new() { Kind = kind, Description = description };

    #region hard blocks

    [Test]
    public async Task DeleteMetaverseObjectTypeAsync_WithMetaverseObjects_RefusesAndReportsCountAsync()
    {
        var objectType = ObjectType();
        _metaverseRepo.Setup(r => r.GetMetaverseObjectOfTypeCountAsync(5)).ReturnsAsync(742);
        _metaverseRepo.Setup(r => r.GetMetaverseObjectTypeReferencesAsync(5)).ReturnsAsync([]);

        var impact = await _jim.Metaverse.DeleteMetaverseObjectTypeAsync(objectType, TestUtilities.GetInitiatedBy());

        Assert.That(impact.BlockedByObjects, Is.True);
        Assert.That(impact.Blocked, Is.True);
        Assert.That(impact.Deleted, Is.False);
        Assert.That(impact.MetaverseObjectCount, Is.EqualTo(742));
        // No destructive action, and no audit Activity was opened.
        _metaverseRepo.Verify(r => r.DeleteMetaverseObjectTypeAsync(It.IsAny<int>()), Times.Never);
        Assert.That(_createdActivities, Is.Empty);
    }

    [Test]
    public async Task DeleteMetaverseObjectTypeAsync_WithSynchronisationRules_RefusesAsync()
    {
        var objectType = ObjectType();
        _metaverseRepo.Setup(r => r.GetMetaverseObjectOfTypeCountAsync(5)).ReturnsAsync(0);
        _metaverseRepo.Setup(r => r.GetMetaverseObjectTypeReferencesAsync(5)).ReturnsAsync(
        [
            Reference(ObjectTypeReferenceKind.SynchronisationRule, "Import Devices"),
            Reference(ObjectTypeReferenceKind.SynchronisationRule, "Export Devices")
        ]);

        var impact = await _jim.Metaverse.DeleteMetaverseObjectTypeAsync(objectType, TestUtilities.GetInitiatedBy());

        Assert.That(impact.BlockedBySynchronisationRules, Is.True);
        Assert.That(impact.Blocked, Is.True);
        Assert.That(impact.Deleted, Is.False);
        Assert.That(impact.SynchronisationRules, Has.Count.EqualTo(2));
        _metaverseRepo.Verify(r => r.DeleteMetaverseObjectTypeAsync(It.IsAny<int>()), Times.Never);
        Assert.That(_createdActivities, Is.Empty);
    }

    [Test]
    public void DeleteMetaverseObjectTypeAsync_WithBuiltIn_ThrowsAsync()
    {
        var objectType = ObjectType(id: 1, name: "User", builtIn: true);

        Assert.ThrowsAsync<InvalidOperationException>(
            () => _jim.Metaverse.DeleteMetaverseObjectTypeAsync(objectType, TestUtilities.GetInitiatedBy()));

        _metaverseRepo.Verify(r => r.DeleteMetaverseObjectTypeAsync(It.IsAny<int>()), Times.Never);
    }

    #endregion

    #region cascade delete with parent + child audit

    [Test]
    public async Task DeleteMetaverseObjectTypeAsync_WithCascadeReferencesNoObjects_CascadesAndAuditsChildrenAsync()
    {
        var objectType = ObjectType();
        var references = new List<ObjectTypeReference>
        {
            Reference(ObjectTypeReferenceKind.PredefinedSearch, "All Devices"),
            Reference(ObjectTypeReferenceKind.ExampleDataTemplate, "Contoso Devices"),
            Reference(ObjectTypeReferenceKind.AttributeBinding, "serialNumber")
        };
        _metaverseRepo.Setup(r => r.GetMetaverseObjectOfTypeCountAsync(5)).ReturnsAsync(0);
        _metaverseRepo.Setup(r => r.GetMetaverseObjectTypeReferencesAsync(5)).ReturnsAsync(references);
        _metaverseRepo.Setup(r => r.DeleteMetaverseObjectTypeAsync(5)).Returns(Task.CompletedTask);

        var impact = await _jim.Metaverse.DeleteMetaverseObjectTypeAsync(objectType, TestUtilities.GetInitiatedBy());

        Assert.That(impact.Deleted, Is.True);
        Assert.That(impact.Blocked, Is.False);
        Assert.That(impact.RequiresConfirmation, Is.True);
        Assert.That(impact.CascadeReferences, Has.Count.EqualTo(3));
        _metaverseRepo.Verify(r => r.DeleteMetaverseObjectTypeAsync(5), Times.Once);

        // Audit shape: exactly one parent (no ParentActivityId), and a child per cascade reference plus one for the
        // object type removal itself.
        var parents = _createdActivities.Where(a => a.ParentActivityId == null).ToList();
        Assert.That(parents, Has.Count.EqualTo(1), "there must be exactly one parent delete Activity");
        var parent = parents[0];
        Assert.That(parent.TargetType, Is.EqualTo(ActivityTargetType.MetaverseObjectType));
        Assert.That(parent.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Delete));

        var children = _createdActivities.Where(a => a.ParentActivityId == parent.Id && !ReferenceEquals(a, parent)).ToList();
        Assert.That(children, Has.Count.EqualTo(references.Count + 1),
            "one child Activity per cascade reference, plus one for the object type removal");
        Assert.That(children.Count(c => c.TargetType == ActivityTargetType.MetaverseObjectType && c.Message!.Contains("Removed Metaverse Object Type")), Is.EqualTo(1));
        // Reference child target types map correctly.
        Assert.That(children.Count(c => c.TargetType == ActivityTargetType.PredefinedSearch), Is.EqualTo(1));
        Assert.That(children.Count(c => c.TargetType == ActivityTargetType.ExampleDataTemplate), Is.EqualTo(1));
        Assert.That(children.Count(c => c.TargetType == ActivityTargetType.MetaverseAttribute), Is.EqualTo(1));
    }

    [Test]
    public async Task DeleteMetaverseObjectTypeAsync_WithNoReferencesOrObjects_DeletesWithoutConfirmationAsync()
    {
        var objectType = ObjectType();
        _metaverseRepo.Setup(r => r.GetMetaverseObjectOfTypeCountAsync(5)).ReturnsAsync(0);
        _metaverseRepo.Setup(r => r.GetMetaverseObjectTypeReferencesAsync(5)).ReturnsAsync([]);
        _metaverseRepo.Setup(r => r.DeleteMetaverseObjectTypeAsync(5)).Returns(Task.CompletedTask);

        var impact = await _jim.Metaverse.DeleteMetaverseObjectTypeAsync(objectType, TestUtilities.GetInitiatedBy());

        Assert.That(impact.Deleted, Is.True);
        Assert.That(impact.RequiresConfirmation, Is.False);
        Assert.That(impact.CascadeReferences, Is.Empty);
        _metaverseRepo.Verify(r => r.DeleteMetaverseObjectTypeAsync(5), Times.Once);
    }

    #endregion

    #region evaluate (preview)

    [Test]
    public async Task EvaluateObjectTypeDeletionAsync_CategorisesReferencesAndCountsAsync()
    {
        var objectType = ObjectType();
        _metaverseRepo.Setup(r => r.GetMetaverseObjectOfTypeCountAsync(5)).ReturnsAsync(3);
        _metaverseRepo.Setup(r => r.GetMetaverseObjectTypeReferencesAsync(5)).ReturnsAsync(
        [
            Reference(ObjectTypeReferenceKind.SynchronisationRule, "Import Devices"),
            Reference(ObjectTypeReferenceKind.PredefinedSearch, "All Devices"),
            Reference(ObjectTypeReferenceKind.AttributeBinding, "serialNumber")
        ]);

        var impact = await _jim.Metaverse.EvaluateObjectTypeDeletionAsync(objectType);

        Assert.That(impact.MetaverseObjectCount, Is.EqualTo(3));
        Assert.That(impact.SynchronisationRules, Has.Count.EqualTo(1));
        Assert.That(impact.CascadeReferences, Has.Count.EqualTo(2));
        // Preview makes no change.
        _metaverseRepo.Verify(r => r.DeleteMetaverseObjectTypeAsync(It.IsAny<int>()), Times.Never);
    }

    #endregion
}
