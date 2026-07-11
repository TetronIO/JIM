// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Exceptions;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Orchestration tests for the custom Metaverse Attribute lifecycle added in #377 (Phase 1): case-insensitive
/// uniqueness, audited rename, bind/unassign, the values-only hard block, and the references-cascade delete with
/// parent + child Activity audit. These exercise the <see cref="JIM.Application.Servers.MetaverseServer"/> logic over
/// a mocked repository; the repository queries themselves (per-type counts, reference discovery, cascade ordering,
/// case-insensitive SQL) are verified against real PostgreSQL in <c>MetaverseAttributeCustomDatabaseTests</c>.
/// </summary>
[TestFixture]
public class MetaverseServerCustomAttributeTests
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

    private static MetaverseAttribute Attribute(int id = 42, string name = "costCentre", bool builtIn = false) =>
        new() { Id = id, Name = name, BuiltIn = builtIn };

    #region case-insensitive uniqueness

    [Test]
    public async Task IsMetaverseAttributeNameUniqueAsync_DelegatesToRepositoryResultAsync()
    {
        _metaverseRepo.Setup(r => r.IsMetaverseAttributeNameUniqueAsync("costCentre", null)).ReturnsAsync(false);

        var result = await _jim.Metaverse.IsMetaverseAttributeNameUniqueAsync("costCentre");

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task IsMetaverseAttributeNameUniqueAsync_WithWhitespaceName_ReturnsFalseWithoutQueryingAsync()
    {
        var result = await _jim.Metaverse.IsMetaverseAttributeNameUniqueAsync("   ");

        Assert.That(result, Is.False);
        _metaverseRepo.Verify(r => r.IsMetaverseAttributeNameUniqueAsync(It.IsAny<string>(), It.IsAny<int?>()), Times.Never);
    }

    #endregion

    #region rename

    [Test]
    public async Task RenameMetaverseAttributeAsync_WithCaseInsensitiveClash_ThrowsConflictAsync()
    {
        // 'CostCentre' already exists; renaming another attribute to 'costCentre' must clash case-insensitively.
        var attribute = Attribute(id: 7, name: "buildingCode");
        _metaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(7, true)).ReturnsAsync(attribute);
        _metaverseRepo.Setup(r => r.IsMetaverseAttributeNameUniqueAsync("costCentre", 7)).ReturnsAsync(false);

        var ex = Assert.ThrowsAsync<MetaverseAttributeNameConflictException>(
            () => _jim.Metaverse.RenameMetaverseAttributeAsync(7, "costCentre", TestUtilities.GetInitiatedBy()));

        Assert.That(ex!.ConflictingName, Is.EqualTo("costCentre"));
        _metaverseRepo.Verify(r => r.UpdateMetaverseAttributeAsync(It.IsAny<MetaverseAttribute>()), Times.Never);
    }

    [Test]
    public async Task RenameMetaverseAttributeAsync_WithBuiltIn_ThrowsAsync()
    {
        var attribute = Attribute(id: 1, name: "Display Name", builtIn: true);
        _metaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(1, true)).ReturnsAsync(attribute);

        Assert.ThrowsAsync<InvalidOperationException>(
            () => _jim.Metaverse.RenameMetaverseAttributeAsync(1, "somethingElse", TestUtilities.GetInitiatedBy()));

        _metaverseRepo.Verify(r => r.IsMetaverseAttributeNameUniqueAsync(It.IsAny<string>(), It.IsAny<int?>()), Times.Never);
        _metaverseRepo.Verify(r => r.UpdateMetaverseAttributeAsync(It.IsAny<MetaverseAttribute>()), Times.Never);
    }

    [Test]
    public async Task RenameMetaverseAttributeAsync_WithUniqueName_UpdatesThroughAuditedPathAsync()
    {
        var attribute = Attribute(id: 7, name: "oldName");
        _metaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(7, true)).ReturnsAsync(attribute);
        _metaverseRepo.Setup(r => r.IsMetaverseAttributeNameUniqueAsync("newName", 7)).ReturnsAsync(true);
        _metaverseRepo.Setup(r => r.UpdateMetaverseAttributeAsync(attribute)).Returns(Task.CompletedTask);

        await _jim.Metaverse.RenameMetaverseAttributeAsync(7, "newName", TestUtilities.GetInitiatedBy());

        Assert.That(attribute.Name, Is.EqualTo("newName"));
        _metaverseRepo.Verify(r => r.UpdateMetaverseAttributeAsync(attribute), Times.Once);
        // The audited path records an Activity for the rename.
        _activityRepo.Verify(r => r.CreateActivityAsync(It.IsAny<Activity>()), Times.AtLeastOnce);
    }

    #endregion

    #region bind

    [Test]
    public async Task BindAttributeToObjectTypeAsync_BindsAndRecordsActivityAsync()
    {
        var attribute = Attribute(id: 7);
        _metaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(7, false)).ReturnsAsync(attribute);
        _metaverseRepo.Setup(r => r.AddAttributeObjectTypeBindingAsync(7, 3)).Returns(Task.CompletedTask);

        await _jim.Metaverse.BindAttributeToObjectTypeAsync(7, 3, TestUtilities.GetInitiatedBy());

        _metaverseRepo.Verify(r => r.AddAttributeObjectTypeBindingAsync(7, 3), Times.Once);
        Assert.That(_createdActivities, Has.Count.EqualTo(1));
        Assert.That(_createdActivities[0].TargetType, Is.EqualTo(ActivityTargetType.MetaverseAttribute));
    }

    [Test]
    public async Task BindAttributeToObjectTypeAsync_WithBuiltIn_ThrowsAsync()
    {
        var attribute = Attribute(id: 1, name: "Display Name", builtIn: true);
        _metaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(1, false)).ReturnsAsync(attribute);

        Assert.ThrowsAsync<InvalidOperationException>(
            () => _jim.Metaverse.BindAttributeToObjectTypeAsync(1, 3, TestUtilities.GetInitiatedBy()));

        _metaverseRepo.Verify(r => r.AddAttributeObjectTypeBindingAsync(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    #endregion

    #region delete: values-only hard block

    [Test]
    public async Task DeleteMetaverseAttributeWithCascadeAsync_WithStoredValues_RefusesAndReportsPerTypeCountsAsync()
    {
        var attribute = Attribute();
        _metaverseRepo.Setup(r => r.GetAttributeValueObjectCountsByTypeAsync(42)).ReturnsAsync(
        [
            new AttributeObjectTypeValueCount { MetaverseObjectTypeId = 1, MetaverseObjectTypeName = "User", ObjectCount = 1200 },
            new AttributeObjectTypeValueCount { MetaverseObjectTypeId = 2, MetaverseObjectTypeName = "Group", ObjectCount = 323 }
        ]);
        _metaverseRepo.Setup(r => r.GetAttributeReferencesAsync(42)).ReturnsAsync([]);

        var impact = await _jim.Metaverse.DeleteMetaverseAttributeWithCascadeAsync(attribute, TestUtilities.GetInitiatedBy());

        Assert.That(impact.BlockedByValues, Is.True);
        Assert.That(impact.Deleted, Is.False);
        Assert.That(impact.TotalObjectsWithValues, Is.EqualTo(1523));
        Assert.That(impact.ObjectTypeValueCounts, Has.Count.EqualTo(2));
        // No destructive action, and no audit Activity was opened.
        _metaverseRepo.Verify(r => r.CascadeDeleteMetaverseAttributeAsync(It.IsAny<int>()), Times.Never);
        Assert.That(_createdActivities, Is.Empty);
    }

    [Test]
    public async Task DeleteMetaverseAttributeWithCascadeAsync_WithBuiltIn_ThrowsAsync()
    {
        var attribute = Attribute(id: 1, name: "Display Name", builtIn: true);

        Assert.ThrowsAsync<InvalidOperationException>(
            () => _jim.Metaverse.DeleteMetaverseAttributeWithCascadeAsync(attribute, TestUtilities.GetInitiatedBy()));

        _metaverseRepo.Verify(r => r.CascadeDeleteMetaverseAttributeAsync(It.IsAny<int>()), Times.Never);
    }

    #endregion

    #region delete: references cascade with parent + child audit

    [Test]
    public async Task DeleteMetaverseAttributeWithCascadeAsync_WithReferencesNoValues_CascadesAndAuditsChildrenAsync()
    {
        var attribute = Attribute();
        var references = new List<AttributeReference>
        {
            new() { Kind = AttributeReferenceKind.Binding, Id = 1, Description = "Object Type binding: User" },
            new() { Kind = AttributeReferenceKind.ImportAttributeFlow, Id = 10, SyncRuleId = 5, SyncRuleName = "Import Users", Description = "Import Attribute Flow in Synchronisation Rule 'Import Users'" },
            new() { Kind = AttributeReferenceKind.ScopingCriterion, Id = 22, Description = "Scoping criterion 22" }
        };
        _metaverseRepo.Setup(r => r.GetAttributeValueObjectCountsByTypeAsync(42)).ReturnsAsync([]);
        _metaverseRepo.Setup(r => r.GetAttributeReferencesAsync(42)).ReturnsAsync(references);
        _metaverseRepo.Setup(r => r.CascadeDeleteMetaverseAttributeAsync(42)).Returns(Task.CompletedTask);

        var impact = await _jim.Metaverse.DeleteMetaverseAttributeWithCascadeAsync(attribute, TestUtilities.GetInitiatedBy());

        Assert.That(impact.Deleted, Is.True);
        Assert.That(impact.BlockedByValues, Is.False);
        Assert.That(impact.RequiresConfirmation, Is.True);
        _metaverseRepo.Verify(r => r.CascadeDeleteMetaverseAttributeAsync(42), Times.Once);

        // Audit shape: exactly one parent (no ParentActivityId), and a child per reference plus one for the
        // attribute removal itself.
        var parents = _createdActivities.Where(a => a.ParentActivityId == null).ToList();
        Assert.That(parents, Has.Count.EqualTo(1), "there must be exactly one parent delete Activity");
        var parent = parents[0];
        Assert.That(parent.TargetType, Is.EqualTo(ActivityTargetType.MetaverseAttribute));
        Assert.That(parent.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Delete));

        var children = _createdActivities.Where(a => a.ParentActivityId == parent.Id && !ReferenceEquals(a, parent)).ToList();
        Assert.That(children, Has.Count.EqualTo(references.Count + 1),
            "one child Activity per removed reference, plus one for the attribute removal");
        Assert.That(children.Count(c => c.TargetType == ActivityTargetType.MetaverseAttribute && c.Message!.Contains("Removed Metaverse Attribute")), Is.EqualTo(1));
    }

    [Test]
    public async Task DeleteMetaverseAttributeWithCascadeAsync_WithNoReferencesOrValues_DeletesWithoutConfirmationAsync()
    {
        var attribute = Attribute();
        _metaverseRepo.Setup(r => r.GetAttributeValueObjectCountsByTypeAsync(42)).ReturnsAsync([]);
        _metaverseRepo.Setup(r => r.GetAttributeReferencesAsync(42)).ReturnsAsync([]);
        _metaverseRepo.Setup(r => r.CascadeDeleteMetaverseAttributeAsync(42)).Returns(Task.CompletedTask);

        var impact = await _jim.Metaverse.DeleteMetaverseAttributeWithCascadeAsync(attribute, TestUtilities.GetInitiatedBy());

        Assert.That(impact.Deleted, Is.True);
        Assert.That(impact.RequiresConfirmation, Is.False);
        _metaverseRepo.Verify(r => r.CascadeDeleteMetaverseAttributeAsync(42), Times.Once);
    }

    #endregion

    #region unassign

    [Test]
    public async Task UnassignAttributeFromObjectTypeAsync_WithValuesOfThatType_RefusesAsync()
    {
        var attribute = new MetaverseAttribute
        {
            Id = 42, Name = "costCentre",
            MetaverseObjectTypes = [new MetaverseObjectType { Id = 1, Name = "User" }]
        };
        _metaverseRepo.Setup(r => r.GetMetaverseAttributeWithObjectTypesAsync(42, false)).ReturnsAsync(attribute);
        _metaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(1, false)).ReturnsAsync(new MetaverseObjectType { Id = 1, Name = "User" });
        _metaverseRepo.Setup(r => r.GetAttributeValueObjectCountByTypeAsync(42, 1)).ReturnsAsync(450);

        var impact = await _jim.Metaverse.UnassignAttributeFromObjectTypeAsync(42, 1, TestUtilities.GetInitiatedBy());

        Assert.That(impact.BlockedByValues, Is.True);
        Assert.That(impact.Unassigned, Is.False);
        Assert.That(impact.ObjectsWithValues, Is.EqualTo(450));
        _metaverseRepo.Verify(r => r.RemoveAttributeObjectTypeBindingAsync(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Test]
    public async Task UnassignAttributeFromObjectTypeAsync_NoValues_RemovesBindingAndAuditsAsync()
    {
        var attribute = new MetaverseAttribute
        {
            Id = 42, Name = "costCentre",
            MetaverseObjectTypes = [new MetaverseObjectType { Id = 1, Name = "User" }]
        };
        _metaverseRepo.Setup(r => r.GetMetaverseAttributeWithObjectTypesAsync(42, false)).ReturnsAsync(attribute);
        _metaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(1, false)).ReturnsAsync(new MetaverseObjectType { Id = 1, Name = "User" });
        _metaverseRepo.Setup(r => r.GetAttributeValueObjectCountByTypeAsync(42, 1)).ReturnsAsync(0);
        _metaverseRepo.Setup(r => r.RemoveAttributeObjectTypeBindingAsync(42, 1)).Returns(Task.CompletedTask);

        var impact = await _jim.Metaverse.UnassignAttributeFromObjectTypeAsync(42, 1, TestUtilities.GetInitiatedBy());

        Assert.That(impact.Unassigned, Is.True);
        _metaverseRepo.Verify(r => r.RemoveAttributeObjectTypeBindingAsync(42, 1), Times.Once);
        Assert.That(_createdActivities, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task UnassignAttributeFromObjectTypeAsync_WithBuiltIn_ThrowsAsync()
    {
        var attribute = new MetaverseAttribute
        {
            Id = 1, Name = "Display Name", BuiltIn = true,
            MetaverseObjectTypes = [new MetaverseObjectType { Id = 1, Name = "User" }]
        };
        _metaverseRepo.Setup(r => r.GetMetaverseAttributeWithObjectTypesAsync(1, false)).ReturnsAsync(attribute);
        _metaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(1, false)).ReturnsAsync(new MetaverseObjectType { Id = 1, Name = "User" });
        _metaverseRepo.Setup(r => r.GetAttributeValueObjectCountByTypeAsync(1, 1)).ReturnsAsync(0);

        Assert.ThrowsAsync<InvalidOperationException>(
            () => _jim.Metaverse.UnassignAttributeFromObjectTypeAsync(1, 1, TestUtilities.GetInitiatedBy()));

        _metaverseRepo.Verify(r => r.RemoveAttributeObjectTypeBindingAsync(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    #endregion

    #region type / plurality change

    [Test]
    public async Task ChangeMetaverseAttributeSchemaAsync_WithStoredValues_RefusesAsync()
    {
        var attribute = Attribute();
        _metaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(42, true)).ReturnsAsync(attribute);
        _metaverseRepo.Setup(r => r.GetAttributeValueObjectCountAsync(42)).ReturnsAsync(77);

        var impact = await _jim.Metaverse.ChangeMetaverseAttributeSchemaAsync(
            42, AttributeDataType.Number, AttributePlurality.MultiValued, TestUtilities.GetInitiatedBy());

        Assert.That(impact.BlockedByValues, Is.True);
        Assert.That(impact.Applied, Is.False);
        _metaverseRepo.Verify(r => r.UpdateMetaverseAttributeAsync(It.IsAny<MetaverseAttribute>()), Times.Never);
    }

    [Test]
    public async Task ChangeMetaverseAttributeSchemaAsync_NoValues_AppliesThroughAuditedPathAsync()
    {
        var attribute = new MetaverseAttribute { Id = 42, Name = "costCentre", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        _metaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(42, true)).ReturnsAsync(attribute);
        _metaverseRepo.Setup(r => r.GetAttributeValueObjectCountAsync(42)).ReturnsAsync(0);
        _metaverseRepo.Setup(r => r.UpdateMetaverseAttributeAsync(attribute)).Returns(Task.CompletedTask);

        var impact = await _jim.Metaverse.ChangeMetaverseAttributeSchemaAsync(
            42, AttributeDataType.Number, AttributePlurality.MultiValued, TestUtilities.GetInitiatedBy());

        Assert.That(impact.Applied, Is.True);
        Assert.That(attribute.Type, Is.EqualTo(AttributeDataType.Number));
        Assert.That(attribute.AttributePlurality, Is.EqualTo(AttributePlurality.MultiValued));
        _metaverseRepo.Verify(r => r.UpdateMetaverseAttributeAsync(attribute), Times.Once);
    }

    [Test]
    public async Task ChangeMetaverseAttributeSchemaAsync_WithBuiltIn_ThrowsAsync()
    {
        var attribute = Attribute(id: 1, name: "Display Name", builtIn: true);
        _metaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(1, true)).ReturnsAsync(attribute);

        Assert.ThrowsAsync<InvalidOperationException>(
            () => _jim.Metaverse.ChangeMetaverseAttributeSchemaAsync(
                1, AttributeDataType.Number, AttributePlurality.SingleValued, TestUtilities.GetInitiatedBy()));

        _metaverseRepo.Verify(r => r.GetAttributeValueObjectCountAsync(It.IsAny<int>()), Times.Never);
        _metaverseRepo.Verify(r => r.UpdateMetaverseAttributeAsync(It.IsAny<MetaverseAttribute>()), Times.Never);
    }

    #endregion
}
