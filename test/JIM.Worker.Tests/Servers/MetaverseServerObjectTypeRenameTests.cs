// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Orchestration tests for the audited Metaverse Object Type identity update (rename / re-icon) added in #376. This is
/// the guarded application path the UI edit dialog uses, so it must enforce built-in protection and case-insensitive
/// name / plural-name uniqueness independently of the REST controller.
/// </summary>
[TestFixture]
public class MetaverseServerObjectTypeRenameTests
{
    private Mock<IRepository> _repo = null!;
    private Mock<IMetaverseRepository> _metaverseRepo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private JimApplication _jim = null!;

    [SetUp]
    public void SetUp()
    {
        _repo = new Mock<IRepository>();
        _metaverseRepo = new Mock<IMetaverseRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _repo.Setup(r => r.Metaverse).Returns(_metaverseRepo.Object);
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _jim = new JimApplication(_repo.Object);
    }

    [TearDown]
    public void TearDown() => _jim.Dispose();

    private static MetaverseObjectType ObjectType(int id = 5, string name = "Device", bool builtIn = false) =>
        new() { Id = id, Name = name, PluralName = name + "s", BuiltIn = builtIn };

    [Test]
    public void RenameMetaverseObjectTypeAsync_WithBuiltIn_ThrowsAsync()
    {
        _metaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(1, false)).ReturnsAsync(ObjectType(id: 1, name: "User", builtIn: true));

        Assert.ThrowsAsync<InvalidOperationException>(
            () => _jim.Metaverse.RenameMetaverseObjectTypeAsync(1, "Person", "People", null, TestUtilities.GetInitiatedBy()));

        _metaverseRepo.Verify(r => r.UpdateMetaverseObjectTypeAsync(It.IsAny<MetaverseObjectType>()), Times.Never);
    }

    [Test]
    public void RenameMetaverseObjectTypeAsync_WithNameClash_ThrowsAsync()
    {
        _metaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(5, false)).ReturnsAsync(ObjectType());
        // Another type already uses the target name.
        _metaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync("Gadget", false)).ReturnsAsync(ObjectType(id: 9, name: "Gadget"));

        Assert.ThrowsAsync<InvalidOperationException>(
            () => _jim.Metaverse.RenameMetaverseObjectTypeAsync(5, "Gadget", "Gadgets", null, TestUtilities.GetInitiatedBy()));

        _metaverseRepo.Verify(r => r.UpdateMetaverseObjectTypeAsync(It.IsAny<MetaverseObjectType>()), Times.Never);
    }

    [Test]
    public void RenameMetaverseObjectTypeAsync_WithPluralNameClash_ThrowsAsync()
    {
        _metaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(5, false)).ReturnsAsync(ObjectType());
        _metaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync("Gadget", false)).ReturnsAsync((MetaverseObjectType?)null);
        _metaverseRepo.Setup(r => r.GetMetaverseObjectTypeByPluralNameAsync("Widgets", false)).ReturnsAsync(ObjectType(id: 9, name: "Widget"));

        Assert.ThrowsAsync<InvalidOperationException>(
            () => _jim.Metaverse.RenameMetaverseObjectTypeAsync(5, "Gadget", "Widgets", null, TestUtilities.GetInitiatedBy()));

        _metaverseRepo.Verify(r => r.UpdateMetaverseObjectTypeAsync(It.IsAny<MetaverseObjectType>()), Times.Never);
    }

    [Test]
    public async Task RenameMetaverseObjectTypeAsync_WithUniqueNames_UpdatesThroughAuditedPathAsync()
    {
        var objectType = ObjectType();
        _metaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(5, false)).ReturnsAsync(objectType);
        _metaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync("Gadget", false)).ReturnsAsync((MetaverseObjectType?)null);
        _metaverseRepo.Setup(r => r.GetMetaverseObjectTypeByPluralNameAsync("Gadgets", false)).ReturnsAsync((MetaverseObjectType?)null);
        _metaverseRepo.Setup(r => r.UpdateMetaverseObjectTypeAsync(objectType)).Returns(Task.CompletedTask);

        await _jim.Metaverse.RenameMetaverseObjectTypeAsync(5, "Gadget", "Gadgets", "Devices", TestUtilities.GetInitiatedBy());

        Assert.That(objectType.Name, Is.EqualTo("Gadget"));
        Assert.That(objectType.PluralName, Is.EqualTo("Gadgets"));
        Assert.That(objectType.Icon, Is.EqualTo("Devices"));
        _metaverseRepo.Verify(r => r.UpdateMetaverseObjectTypeAsync(objectType), Times.Once);
    }

    [Test]
    public async Task RenameMetaverseObjectTypeAsync_WithBlankIcon_ClearsIconAsync()
    {
        var objectType = ObjectType();
        objectType.Icon = "Devices";
        _metaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync(5, false)).ReturnsAsync(objectType);
        _metaverseRepo.Setup(r => r.GetMetaverseObjectTypeAsync("Device", false)).ReturnsAsync(objectType);
        _metaverseRepo.Setup(r => r.GetMetaverseObjectTypeByPluralNameAsync("Devices", false)).ReturnsAsync(objectType);
        _metaverseRepo.Setup(r => r.UpdateMetaverseObjectTypeAsync(objectType)).Returns(Task.CompletedTask);

        await _jim.Metaverse.RenameMetaverseObjectTypeAsync(5, "Device", "Devices", "  ", TestUtilities.GetInitiatedBy());

        Assert.That(objectType.Icon, Is.Null);
    }
}
