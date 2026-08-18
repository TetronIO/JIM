// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Issue #1398: the Pending Export detail (portal, REST, PowerShell all read this one result) explains each
/// reference that has not been written yet, computed against the target's current state: the referenced object
/// has an object with an anchor (will resolve next run), an object without one (waiting), or no object at all
/// (cannot resolve as things stand).
/// </summary>
[TestFixture]
public class PendingExportUnresolvedReferenceDetailTests
{
    private const int ConnectedSystemId = 3;

    private Mock<IConnectedSystemRepository> _connectedSystems = null!;
    private Mock<IMetaverseRepository> _metaverse = null!;
    private SynchronisationController _controller = null!;

    private readonly ConnectedSystemObjectTypeAttribute _managerAttr = new() { Id = 20, Name = "MANAGER_ID", Type = AttributeDataType.Reference };
    private readonly ConnectedSystemObjectTypeAttribute _anchorAttr = new() { Id = 21, Name = "USER_ID", Type = AttributeDataType.Number, IsExternalId = true };

    [SetUp]
    public void SetUp()
    {
        var repository = new Mock<IRepository>();
        _connectedSystems = new Mock<IConnectedSystemRepository>();
        _metaverse = new Mock<IMetaverseRepository>();
        repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystems.Object);
        repository.Setup(r => r.Metaverse).Returns(_metaverse.Object);
        var application = new JimApplication(repository.Object);
        _controller = new SynchronisationController(
            new Mock<ILogger<SynchronisationController>>().Object,
            application,
            new DynamicExpressoEvaluator(),
            new Mock<ICredentialProtectionService>().Object);
    }

    private PendingExportAttributeValueChange UnresolvedChange(Guid mvoId) => new()
    {
        Id = Guid.NewGuid(),
        AttributeId = _managerAttr.Id,
        Attribute = _managerAttr,
        ChangeType = PendingExportAttributeChangeType.Update,
        UnresolvedReferenceValue = mvoId.ToString(),
        Status = PendingExportAttributeChangeStatus.Pending
    };

    private ConnectedSystemObject TargetCso(bool withAnchor)
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), ConnectedSystemId = ConnectedSystemId, ExternalIdAttributeId = _anchorAttr.Id };
        if (withAnchor)
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Attribute = _anchorAttr, AttributeId = _anchorAttr.Id, IntValue = 42 });
        return cso;
    }

    private PendingExport SetupDetail(params PendingExportAttributeValueChange[] changes)
    {
        var pe = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = ConnectedSystemId,
            ConnectedSystem = new ConnectedSystem { Id = ConnectedSystemId, Name = "Accounts" },
            ChangeType = PendingExportChangeType.Update,
            HasUnresolvedReferences = changes.Any(c => c.UnresolvedReferenceValue != null),
            AttributeValueChanges = changes.ToList()
        };
        _connectedSystems.Setup(r => r.GetPendingExportDetailAsync(pe.Id))
            .ReturnsAsync(new PendingExportDetailResult { PendingExport = pe });
        return pe;
    }

    private async Task<PendingExportDetailDto> GetDtoAsync(Guid id)
    {
        var response = await _controller.GetPendingExportAsync(id);
        var ok = response as OkObjectResult;
        Assert.That(ok, Is.Not.Null, "Expected 200 OK.");
        return (PendingExportDetailDto)ok!.Value!;
    }

    [Test]
    public async Task GetPendingExport_UnresolvedReferences_EachCarriesItsReasonAndTheReferencedObjectsNameAsync()
    {
        var resolvableMvo = Guid.NewGuid();
        var awaitingMvo = Guid.NewGuid();
        var missingMvo = Guid.NewGuid();
        var resolvable = UnresolvedChange(resolvableMvo);
        var awaiting = UnresolvedChange(awaitingMvo);
        var missing = UnresolvedChange(missingMvo);
        var pe = SetupDetail(resolvable, awaiting, missing);

        _connectedSystems
            .Setup(r => r.GetConnectedSystemObjectsByMetaverseObjectIdsAsync(
                It.Is<IEnumerable<Guid>>(ids => ids.OrderBy(i => i).SequenceEqual(new[] { resolvableMvo, awaitingMvo, missingMvo }.OrderBy(i => i))),
                ConnectedSystemId))
            .ReturnsAsync(new Dictionary<Guid, ConnectedSystemObject>
            {
                [resolvableMvo] = TargetCso(withAnchor: true),
                [awaitingMvo] = TargetCso(withAnchor: false)
            });
        _metaverse
            .Setup(r => r.GetMetaverseObjectDisplayNamesAsync(It.IsAny<IReadOnlyCollection<Guid>>()))
            .ReturnsAsync(new Dictionary<Guid, string?>
            {
                [resolvableMvo] = "Ada Ashcroft",
                [awaitingMvo] = "Bram Brandt",
                [missingMvo] = "Cleo Calder"
            });

        var dto = await GetDtoAsync(pe.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.UnresolvedReferences, Has.Count.EqualTo(3));
            var byChange = dto.UnresolvedReferences.ToDictionary(u => u.AttributeChangeId);
            Assert.That(byChange[resolvable.Id].Reason, Is.EqualTo(UnresolvedReferenceReason.Resolvable));
            Assert.That(byChange[awaiting.Id].Reason, Is.EqualTo(UnresolvedReferenceReason.AwaitingAnchor));
            Assert.That(byChange[missing.Id].Reason, Is.EqualTo(UnresolvedReferenceReason.NotInTargetSystem));
            Assert.That(byChange[missing.Id].ReferencedMetaverseObjectId, Is.EqualTo(missingMvo));
            Assert.That(byChange[missing.Id].ReferencedMetaverseObjectDisplayName, Is.EqualTo("Cleo Calder"));
            Assert.That(byChange[missing.Id].AttributeName, Is.EqualTo("MANAGER_ID"));
        }
    }

    [Test]
    public async Task GetPendingExport_NoUnresolvedReferences_LooksNothingUpAsync()
    {
        var pe = SetupDetail(new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = 1,
            Attribute = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "EMAIL", Type = AttributeDataType.Text },
            StringValue = "ada@example.test"
        });

        var dto = await GetDtoAsync(pe.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.UnresolvedReferences, Is.Empty);
            _connectedSystems.Verify(r => r.GetConnectedSystemObjectsByMetaverseObjectIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<int>()), Times.Never);
            _metaverse.Verify(r => r.GetMetaverseObjectDisplayNamesAsync(It.IsAny<IReadOnlyCollection<Guid>>()), Times.Never);
        }
    }
}
