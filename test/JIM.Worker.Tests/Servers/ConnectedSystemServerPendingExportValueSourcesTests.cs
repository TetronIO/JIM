// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Unit tests for <see cref="ConnectedSystemServer.GetPendingExportValueSourcesAsync"/> (#399's "Value from"
/// column): classifying an export mapping's source and resolving the origin of a single Metaverse attribute
/// source, without touching a database.
/// </summary>
[TestFixture]
public class ConnectedSystemServerPendingExportValueSourcesTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IConnectedSystemRepository> _mockCsRepo = null!;
    private Mock<IMetaverseRepository> _mockMvRepo = null!;
    private JimApplication _jim = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockCsRepo = new Mock<IConnectedSystemRepository>();
        _mockMvRepo = new Mock<IMetaverseRepository>();
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockCsRepo.Object);
        _mockRepository.Setup(r => r.Metaverse).Returns(_mockMvRepo.Object);
        _jim = new JimApplication(_mockRepository.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _jim.Dispose();
    }

    private static PendingExport BuildPendingExport(Guid sourceMvoId, int syncRuleId, int connectedSystemAttributeId)
    {
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            SourceMetaverseObjectId = sourceMvoId,
            ChangeType = PendingExportChangeType.Update
        };
        pendingExport.AttributeValueChanges.Add(new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = connectedSystemAttributeId,
            SyncRuleId = syncRuleId,
            SyncRuleName = "HR to AD - Users",
            ChangeType = PendingExportAttributeChangeType.Update,
            StringValue = "jsmith@example.com"
        });
        return pendingExport;
    }

    [Test]
    public async Task GetPendingExportValueSourcesAsync_NoSourceMetaverseObject_ReturnsEmptyAsync()
    {
        var pendingExport = new PendingExport { Id = Guid.NewGuid(), SourceMetaverseObjectId = null, ChangeType = PendingExportChangeType.Delete };

        var result = await _jim.ConnectedSystems.GetPendingExportValueSourcesAsync(pendingExport);

        Assert.That(result, Is.Empty);
        _mockCsRepo.Verify(r => r.GetExportSyncRuleMappingsForTargetsAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<IReadOnlyCollection<int>>()), Times.Never);
    }

    [Test]
    public async Task GetPendingExportValueSourcesAsync_MappingHasSingleExpressionSource_ReturnsComputedWithExpressionAsync()
    {
        var mvoId = Guid.NewGuid();
        var pendingExport = BuildPendingExport(mvoId, syncRuleId: 7, connectedSystemAttributeId: 42);

        var mapping = new SyncRuleMapping { Id = 1, SyncRuleId = 7, TargetConnectedSystemAttributeId = 42 };
        mapping.Sources.Add(new SyncRuleMappingSource { Order = 0, Expression = "mv[\"mail\"] ?? mv[\"upn\"]" });

        _mockCsRepo
            .Setup(r => r.GetExportSyncRuleMappingsForTargetsAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<IReadOnlyCollection<int>>()))
            .ReturnsAsync(new List<SyncRuleMapping> { mapping });
        _mockMvRepo
            .Setup(r => r.GetMetaverseObjectProvenanceAsync(mvoId))
            .ReturnsAsync(new MetaverseObjectProvenance { MetaverseObjectId = mvoId });

        var result = await _jim.ConnectedSystems.GetPendingExportValueSourcesAsync(pendingExport);

        var source = result.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.ConnectedSystemAttributeId, Is.EqualTo(42));
            Assert.That(source.IsComputed, Is.True);
            Assert.That(source.Expression, Is.EqualTo("mv[\"mail\"] ?? mv[\"upn\"]"));
            Assert.That(source.SourceMetaverseAttributeId, Is.Null);
        }
    }

    [Test]
    public async Task GetPendingExportValueSourcesAsync_MappingHasGeneration_ReturnsComputedNamingJimAsync()
    {
        var mvoId = Guid.NewGuid();
        var pendingExport = BuildPendingExport(mvoId, syncRuleId: 7, connectedSystemAttributeId: 42);

        var mapping = new SyncRuleMapping { Id = 1, SyncRuleId = 7, TargetConnectedSystemAttributeId = 42 };
        mapping.Sources.Add(new SyncRuleMappingSource { Order = 0, Expression = "\"user\" + Sequence()" });
        mapping.Generation = new SyncRuleMappingGeneration { SyncRuleMapping = mapping };

        _mockCsRepo
            .Setup(r => r.GetExportSyncRuleMappingsForTargetsAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<IReadOnlyCollection<int>>()))
            .ReturnsAsync(new List<SyncRuleMapping> { mapping });
        _mockMvRepo
            .Setup(r => r.GetMetaverseObjectProvenanceAsync(mvoId))
            .ReturnsAsync(new MetaverseObjectProvenance { MetaverseObjectId = mvoId });

        var result = await _jim.ConnectedSystems.GetPendingExportValueSourcesAsync(pendingExport);

        var source = result.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.IsComputed, Is.True);
            Assert.That(source.Expression, Is.EqualTo("Generated by JIM"));
        }
    }

    [Test]
    public async Task GetPendingExportValueSourcesAsync_SingleMetaverseAttributeSource_ResolvesSynchronisationRuleOriginAsync()
    {
        var mvoId = Guid.NewGuid();
        var pendingExport = BuildPendingExport(mvoId, syncRuleId: 7, connectedSystemAttributeId: 42);

        var mvAttribute = new MetaverseAttribute { Id = 5, Name = "mail", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        var mapping = new SyncRuleMapping { Id = 1, SyncRuleId = 7, TargetConnectedSystemAttributeId = 42 };
        mapping.Sources.Add(new SyncRuleMappingSource { Order = 0, MetaverseAttribute = mvAttribute, MetaverseAttributeId = 5 });

        var origin = new ValueOrigin
        {
            Kind = ValueOriginKind.SynchronisationRule,
            ConnectedSystemId = 3,
            ConnectedSystemName = "HR",
            SyncRuleId = 9,
            SyncRuleName = "HR Import"
        };
        var provenance = new MetaverseObjectProvenance { MetaverseObjectId = mvoId };
        provenance.Attributes.Add(new MetaverseAttributeOriginSummary { AttributeId = 5, AttributeName = "mail", Origins = { origin } });

        _mockCsRepo
            .Setup(r => r.GetExportSyncRuleMappingsForTargetsAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<IReadOnlyCollection<int>>()))
            .ReturnsAsync(new List<SyncRuleMapping> { mapping });
        _mockMvRepo
            .Setup(r => r.GetMetaverseObjectProvenanceAsync(mvoId))
            .ReturnsAsync(provenance);

        var result = await _jim.ConnectedSystems.GetPendingExportValueSourcesAsync(pendingExport);

        var source = result.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.IsComputed, Is.False);
            Assert.That(source.SourceMetaverseAttributeId, Is.EqualTo(5));
            Assert.That(source.SourceMetaverseAttributeName, Is.EqualTo("mail"));
            Assert.That(source.Origin.Kind, Is.EqualTo(ValueOriginKind.SynchronisationRule));
            Assert.That(source.Origin.ConnectedSystemName, Is.EqualTo("HR"));
            Assert.That(source.HasSeveralOrigins, Is.False);
        }
    }

    [Test]
    public async Task GetPendingExportValueSourcesAsync_SingleMetaverseAttributeSourceWithNoCurrentValue_OriginIsNotRecordedAsync()
    {
        var mvoId = Guid.NewGuid();
        var pendingExport = BuildPendingExport(mvoId, syncRuleId: 7, connectedSystemAttributeId: 42);

        var mvAttribute = new MetaverseAttribute { Id = 5, Name = "mail", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        var mapping = new SyncRuleMapping { Id = 1, SyncRuleId = 7, TargetConnectedSystemAttributeId = 42 };
        mapping.Sources.Add(new SyncRuleMappingSource { Order = 0, MetaverseAttribute = mvAttribute, MetaverseAttributeId = 5 });

        // The provenance summary carries no entry for attribute 5 (no current value on the source object).
        var provenance = new MetaverseObjectProvenance { MetaverseObjectId = mvoId };

        _mockCsRepo
            .Setup(r => r.GetExportSyncRuleMappingsForTargetsAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<IReadOnlyCollection<int>>()))
            .ReturnsAsync(new List<SyncRuleMapping> { mapping });
        _mockMvRepo
            .Setup(r => r.GetMetaverseObjectProvenanceAsync(mvoId))
            .ReturnsAsync(provenance);

        var result = await _jim.ConnectedSystems.GetPendingExportValueSourcesAsync(pendingExport);

        var source = result.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.IsComputed, Is.False);
            Assert.That(source.Origin.Kind, Is.EqualTo(ValueOriginKind.NotRecorded));
            Assert.That(source.HasSeveralOrigins, Is.False);
        }
    }

    [Test]
    public async Task GetPendingExportValueSourcesAsync_SingleMetaverseAttributeSourceWithSeveralOrigins_FlagsHasSeveralOriginsAsync()
    {
        var mvoId = Guid.NewGuid();
        var pendingExport = BuildPendingExport(mvoId, syncRuleId: 7, connectedSystemAttributeId: 42);

        var mvAttribute = new MetaverseAttribute { Id = 5, Name = "members", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued };
        var mapping = new SyncRuleMapping { Id = 1, SyncRuleId = 7, TargetConnectedSystemAttributeId = 42 };
        mapping.Sources.Add(new SyncRuleMappingSource { Order = 0, MetaverseAttribute = mvAttribute, MetaverseAttributeId = 5 });

        var provenance = new MetaverseObjectProvenance { MetaverseObjectId = mvoId };
        provenance.Attributes.Add(new MetaverseAttributeOriginSummary
        {
            AttributeId = 5,
            AttributeName = "members",
            Origins =
            {
                new ValueOrigin { Kind = ValueOriginKind.SynchronisationRule, ConnectedSystemId = 3, ConnectedSystemName = "HR" },
                new ValueOrigin { Kind = ValueOriginKind.SynchronisationRule, ConnectedSystemId = 4, ConnectedSystemName = "Facilities" }
            }
        });

        _mockCsRepo
            .Setup(r => r.GetExportSyncRuleMappingsForTargetsAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<IReadOnlyCollection<int>>()))
            .ReturnsAsync(new List<SyncRuleMapping> { mapping });
        _mockMvRepo
            .Setup(r => r.GetMetaverseObjectProvenanceAsync(mvoId))
            .ReturnsAsync(provenance);

        var result = await _jim.ConnectedSystems.GetPendingExportValueSourcesAsync(pendingExport);

        Assert.That(result.Single().HasSeveralOrigins, Is.True);
    }

    [Test]
    public async Task GetPendingExportValueSourcesAsync_MappingCannotBeResolvedRuleDeleted_SkipsAttributeAsync()
    {
        var mvoId = Guid.NewGuid();
        var pendingExport = BuildPendingExport(mvoId, syncRuleId: 7, connectedSystemAttributeId: 42);

        // The staging rule (and so its mapping) has since been deleted: the repository finds nothing for it.
        _mockCsRepo
            .Setup(r => r.GetExportSyncRuleMappingsForTargetsAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<IReadOnlyCollection<int>>()))
            .ReturnsAsync(new List<SyncRuleMapping>());
        _mockMvRepo
            .Setup(r => r.GetMetaverseObjectProvenanceAsync(mvoId))
            .ReturnsAsync(new MetaverseObjectProvenance { MetaverseObjectId = mvoId });

        var result = await _jim.ConnectedSystems.GetPendingExportValueSourcesAsync(pendingExport);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetPendingExportValueSourcesAsync_NoAttributeValueChangesCarryASyncRule_ReturnsEmptyWithoutQueryingAsync()
    {
        var pendingExport = new PendingExport { Id = Guid.NewGuid(), SourceMetaverseObjectId = Guid.NewGuid(), ChangeType = PendingExportChangeType.Update };
        pendingExport.AttributeValueChanges.Add(new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = 42,
            SyncRuleId = null,
            ChangeType = PendingExportAttributeChangeType.Update
        });

        var result = await _jim.ConnectedSystems.GetPendingExportValueSourcesAsync(pendingExport);

        Assert.That(result, Is.Empty);
        _mockCsRepo.Verify(r => r.GetExportSyncRuleMappingsForTargetsAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<IReadOnlyCollection<int>>()), Times.Never);
    }
}
