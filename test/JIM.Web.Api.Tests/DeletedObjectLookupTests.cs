// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The deleted-object lookups that back the Deleted Objects page's deep links. The page is reached from a
/// causality view, which knows the deleted object's own id and nothing about the change record that
/// survived it, so the lookup has to go the other way round from the browsing case: object id in, deletion
/// record out.
/// </summary>
[TestFixture]
public class DeletedObjectLookupTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IMetaverseRepository> _mockMetaverseRepo = null!;
    private Mock<IConnectedSystemRepository> _mockCsRepo = null!;
    private JimApplication _application = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockMetaverseRepo = new Mock<IMetaverseRepository>();
        _mockCsRepo = new Mock<IConnectedSystemRepository>();
        _mockRepository.Setup(r => r.Metaverse).Returns(_mockMetaverseRepo.Object);
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockCsRepo.Object);
        _application = new JimApplication(_mockRepository.Object);
    }

    [Test]
    public async Task GetDeletedMvoChangeAsync_WithADeletedObjectId_ReturnsItsDeletionRecordAsync()
    {
        var deletedMvoId = Guid.NewGuid();
        var expected = new MetaverseObjectChange
        {
            Id = Guid.NewGuid(),
            DeletedMetaverseObjectId = deletedMvoId,
            DeletedObjectDisplayName = "Erin Byrne"
        };
        _mockMetaverseRepo.Setup(r => r.GetDeletedMvoChangeAsync(deletedMvoId)).ReturnsAsync(expected);

        var result = await _application.Metaverse.GetDeletedMvoChangeAsync(deletedMvoId);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Id, Is.EqualTo(expected.Id));
        Assert.That(result.DeletedObjectDisplayName, Is.EqualTo("Erin Byrne"));
    }

    [Test]
    public async Task GetDeletedMvoChangeAsync_WhenNoDeletionRecordExists_ReturnsNullAsync()
    {
        // A causality view can name an Identity whose deletion predates change tracking being switched on,
        // so the page must be able to say "no record" rather than throw on the way in.
        var deletedMvoId = Guid.NewGuid();
        _mockMetaverseRepo.Setup(r => r.GetDeletedMvoChangeAsync(deletedMvoId))
            .ReturnsAsync((MetaverseObjectChange?)null);

        var result = await _application.Metaverse.GetDeletedMvoChangeAsync(deletedMvoId);

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetDeletedCsoChangeAsync_WithADeletedObjectId_ReturnsItsDeletionRecordAsync()
    {
        var deletedCsoId = Guid.NewGuid();
        var expected = new ConnectedSystemObjectChange
        {
            Id = Guid.NewGuid(),
            DeletedConnectedSystemObjectId = deletedCsoId,
            DeletedObjectExternalId = "38a42756-1f18-1041-9b5b-b9495fac6887",
            DeletedObjectDisplayName = "Project-Catalyst"
        };
        _mockCsRepo.Setup(r => r.GetDeletedCsoChangeAsync(deletedCsoId)).ReturnsAsync(expected);

        var result = await _application.ConnectedSystems.GetDeletedCsoChangeAsync(deletedCsoId);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Id, Is.EqualTo(expected.Id));
        Assert.That(result.DeletedObjectDisplayName, Is.EqualTo("Project-Catalyst"));
    }

    [Test]
    public async Task GetDeletedCsoChangeAsync_WhenNoDeletionRecordExists_ReturnsNullAsync()
    {
        var deletedCsoId = Guid.NewGuid();
        _mockCsRepo.Setup(r => r.GetDeletedCsoChangeAsync(deletedCsoId))
            .ReturnsAsync((ConnectedSystemObjectChange?)null);

        var result = await _application.ConnectedSystems.GetDeletedCsoChangeAsync(deletedCsoId);

        Assert.That(result, Is.Null);
    }
}
