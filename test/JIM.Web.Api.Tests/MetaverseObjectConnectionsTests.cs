// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Logic;
using JIM.Models.Logic.DTOs;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for <see cref="JIM.Application.Servers.MetaverseServer.GetMetaverseObjectConnectionsAsync"/>: one
/// row per joined Connected System Object, role flags derived from enabled Synchronisation Rules, and the
/// connection state derived via <see cref="JIM.Application.Utilities.ConnectedSystemObjectConnectionStateResolver"/>.
/// </summary>
[TestFixture]
public class MetaverseObjectConnectionsTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
    private Mock<IMetaverseRepository> _mockMetaverseRepo = null!;
    private JimApplication _application = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockConnectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _mockMetaverseRepo = new Mock<IMetaverseRepository>();
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);
        _mockRepository.Setup(r => r.Metaverse).Returns(_mockMetaverseRepo.Object);
        _application = new JimApplication(_mockRepository.Object);
    }

    [Test]
    public async Task GetMetaverseObjectConnectionsAsync_NoJoinedObjects_ReturnsEmptyListAsync()
    {
        var mvoId = Guid.NewGuid();
        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemObjectsCoreByMetaverseObjectIdAsync(mvoId))
            .ReturnsAsync([]);

        var result = await _application.Metaverse.GetMetaverseObjectConnectionsAsync(mvoId);

        Assert.That(result, Is.Empty);
        _mockMetaverseRepo.Verify(r => r.GetMetaverseObjectHeaderAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Test]
    public async Task GetMetaverseObjectConnectionsAsync_JoinedInSyncObject_ReturnsRoleFlagsAndInSyncStateAsync()
    {
        var mvoId = Guid.NewGuid();
        const int connectedSystemId = 5;
        const int objectTypeId = 12;
        const int mvoTypeId = 3;

        var cso = BuildCso(connectedSystemId, objectTypeId, ConnectedSystemObjectStatus.Normal, ConnectedSystemObjectJoinType.Joined);

        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemObjectsCoreByMetaverseObjectIdAsync(mvoId))
            .ReturnsAsync([cso]);
        _mockMetaverseRepo
            .Setup(r => r.GetMetaverseObjectHeaderAsync(mvoId))
            .ReturnsAsync(new MetaverseObjectHeader { Id = mvoId, TypeId = mvoTypeId, TypeName = "Person", TypePluralName = "People" });
        _mockConnectedSystemRepo
            .Setup(r => r.GetSyncRuleHeadersAsync(mvoTypeId, null))
            .ReturnsAsync((IList<SyncRuleHeader>)
            [
                BuildRuleHeader(connectedSystemId, objectTypeId, mvoTypeId, SyncRuleDirection.Import, enabled: true),
                BuildRuleHeader(connectedSystemId, objectTypeId, mvoTypeId, SyncRuleDirection.Export, enabled: true)
            ]);
        _mockConnectedSystemRepo
            .Setup(r => r.GetPendingExportsLightweightByConnectedSystemObjectIdsAsync(It.Is<IEnumerable<Guid>>(ids => ids.Contains(cso.Id))))
            .ReturnsAsync(new Dictionary<Guid, PendingExport>());

        var result = await _application.Metaverse.GetMetaverseObjectConnectionsAsync(mvoId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Has.Count.EqualTo(1));
            var row = result[0];
            Assert.That(row.ConnectedSystemObjectId, Is.EqualTo(cso.Id));
            Assert.That(row.ConnectedSystemId, Is.EqualTo(connectedSystemId));
            Assert.That(row.ConnectedSystemName, Is.EqualTo("Target System"));
            Assert.That(row.ObjectTypeName, Is.EqualTo("person"));
            Assert.That(row.JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.Joined));
            Assert.That(row.IsSource, Is.True);
            Assert.That(row.IsTarget, Is.True);
            Assert.That(row.State, Is.EqualTo(ConnectedSystemObjectConnectionState.InSync));
            Assert.That(row.PendingAttributeChangeCount, Is.Null);
        }
    }

    [Test]
    public async Task GetMetaverseObjectConnectionsAsync_DisabledRulesOnly_ReturnsRoleFlagsFalseAsync()
    {
        var mvoId = Guid.NewGuid();
        const int connectedSystemId = 5;
        const int objectTypeId = 12;
        const int mvoTypeId = 3;

        var cso = BuildCso(connectedSystemId, objectTypeId, ConnectedSystemObjectStatus.Normal, ConnectedSystemObjectJoinType.Joined);

        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemObjectsCoreByMetaverseObjectIdAsync(mvoId))
            .ReturnsAsync([cso]);
        _mockMetaverseRepo
            .Setup(r => r.GetMetaverseObjectHeaderAsync(mvoId))
            .ReturnsAsync(new MetaverseObjectHeader { Id = mvoId, TypeId = mvoTypeId, TypeName = "Person", TypePluralName = "People" });
        _mockConnectedSystemRepo
            .Setup(r => r.GetSyncRuleHeadersAsync(mvoTypeId, null))
            .ReturnsAsync((IList<SyncRuleHeader>)
            [
                BuildRuleHeader(connectedSystemId, objectTypeId, mvoTypeId, SyncRuleDirection.Import, enabled: false)
            ]);
        _mockConnectedSystemRepo
            .Setup(r => r.GetPendingExportsLightweightByConnectedSystemObjectIdsAsync(It.IsAny<IEnumerable<Guid>>()))
            .ReturnsAsync(new Dictionary<Guid, PendingExport>());

        var result = await _application.Metaverse.GetMetaverseObjectConnectionsAsync(mvoId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result[0].IsSource, Is.False);
            Assert.That(result[0].IsTarget, Is.False);
        }
    }

    [Test]
    public async Task GetMetaverseObjectConnectionsAsync_UpdatePendingExport_ReturnsPendingAttributeChangeCountAsync()
    {
        var mvoId = Guid.NewGuid();
        const int connectedSystemId = 5;
        const int objectTypeId = 12;

        var cso = BuildCso(connectedSystemId, objectTypeId, ConnectedSystemObjectStatus.Normal, ConnectedSystemObjectJoinType.Joined);
        var pendingExport = new PendingExport
        {
            ChangeType = PendingExportChangeType.Update,
            Status = PendingExportStatus.Pending,
            AttributeValueChanges =
            [
                new PendingExportAttributeValueChange(),
                new PendingExportAttributeValueChange()
            ]
        };

        _mockConnectedSystemRepo
            .Setup(r => r.GetConnectedSystemObjectsCoreByMetaverseObjectIdAsync(mvoId))
            .ReturnsAsync([cso]);
        _mockMetaverseRepo
            .Setup(r => r.GetMetaverseObjectHeaderAsync(mvoId))
            .ReturnsAsync((MetaverseObjectHeader?)null);
        _mockConnectedSystemRepo
            .Setup(r => r.GetPendingExportsLightweightByConnectedSystemObjectIdsAsync(It.IsAny<IEnumerable<Guid>>()))
            .ReturnsAsync(new Dictionary<Guid, PendingExport> { [cso.Id] = pendingExport });

        var result = await _application.Metaverse.GetMetaverseObjectConnectionsAsync(mvoId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result[0].State, Is.EqualTo(ConnectedSystemObjectConnectionState.UpdatePending));
            Assert.That(result[0].PendingAttributeChangeCount, Is.EqualTo(2));
        }
    }

    private static ConnectedSystemObject BuildCso(
        int connectedSystemId,
        int objectTypeId,
        ConnectedSystemObjectStatus status,
        ConnectedSystemObjectJoinType joinType)
    {
        return new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystemId,
            ConnectedSystem = new ConnectedSystem { Id = connectedSystemId, Name = "Target System" },
            TypeId = objectTypeId,
            Type = new ConnectedSystemObjectType { Id = objectTypeId, Name = "person" },
            Status = status,
            JoinType = joinType,
            LastUpdated = DateTime.UtcNow,
            AttributeValues = []
        };
    }

    private static SyncRuleHeader BuildRuleHeader(
        int connectedSystemId,
        int connectedSystemObjectTypeId,
        int metaverseObjectTypeId,
        SyncRuleDirection direction,
        bool enabled)
    {
        return new SyncRuleHeader
        {
            Id = 1,
            Name = "Test Rule",
            ConnectedSystemId = connectedSystemId,
            ConnectedSystemName = "Target System",
            ConnectedSystemObjectTypeId = connectedSystemObjectTypeId,
            ConnectedSystemObjectTypeName = "person",
            MetaverseObjectTypeId = metaverseObjectTypeId,
            MetaverseObjectTypeName = "Person",
            Direction = direction,
            Enabled = enabled
        };
    }
}
