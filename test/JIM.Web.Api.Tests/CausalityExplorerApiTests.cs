// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Application.Services;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.TestSupport;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The Causality Explorer's reads over REST (#2100): an object's causal timeline and a Pending Export's queueing runs,
/// each row carrying its nearest cause as data and as the sentence the portal reads. The rows are execution items, so
/// each carries the item's own id, which a script hands straight to the causes and consequences endpoints.
/// </summary>
[TestFixture]
public class CausalityExplorerApiTests
{
    private static readonly DateTime Executed = new(2026, 10, 12, 11, 0, 0, DateTimeKind.Utc);

    private Mock<IRepository> _repository = null!;
    private Mock<IActivityRepository> _activityRepository = null!;
    private Mock<IConnectedSystemRepository> _connectedSystemRepository = null!;
    private Mock<IMetaverseRepository> _metaverseRepository = null!;
    private JimApplication _application = null!;
    private SynchronisationController _synchronisation = null!;
    private MetaverseController _metaverse = null!;

    private readonly Dictionary<Guid, List<CausalEdge>> _edgesByEffectItemId = new();
    private readonly Dictionary<Guid, CausalTimelineEntry> _entriesById = new();
    private readonly Dictionary<Guid, PendingExport> _pendingExportsById = new();
    private readonly Dictionary<Guid, ConnectedSystemObject> _liveCsosById = new();
    private readonly Dictionary<Guid, MetaverseObject> _liveMvosById = new();
    private List<CausalTimelineEntry> _timelinePage = [];
    private int _timelineTotal;

    [SetUp]
    public void SetUp()
    {
        _edgesByEffectItemId.Clear();
        _entriesById.Clear();
        _pendingExportsById.Clear();
        _liveCsosById.Clear();
        _liveMvosById.Clear();
        _timelinePage = [];
        _timelineTotal = 0;

        _repository = new Mock<IRepository>();
        _activityRepository = new Mock<IActivityRepository>();
        _connectedSystemRepository = new Mock<IConnectedSystemRepository>();
        _metaverseRepository = new Mock<IMetaverseRepository>();
        _repository.Setup(r => r.Activity).Returns(_activityRepository.Object);
        _repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepository.Object);
        _repository.Setup(r => r.Metaverse).Returns(_metaverseRepository.Object);
        _repository.Setup(r => r.ServiceSettings).Returns(new InMemoryServiceSettingsRepository());

        _activityRepository
            .Setup(r => r.GetCausalEdgesByEffectRunProfileExecutionItemIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>()))
            .ReturnsAsync((IReadOnlyCollection<Guid> ids) =>
                ids.SelectMany(id => _edgesByEffectItemId.TryGetValue(id, out var edges) ? edges : []).ToList());
        _activityRepository
            .Setup(r => r.GetRunProfileExecutionItemCausalSummariesAsync(It.IsAny<IReadOnlyCollection<Guid>>()))
            .ReturnsAsync([]);
        _activityRepository
            .Setup(r => r.GetExportExecutionItemIdsByPendingExportIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>()))
            .ReturnsAsync([]);
        _activityRepository
            .Setup(r => r.GetLatestImportEventsForItemsAsync(It.IsAny<IReadOnlyCollection<Guid>>()))
            .ReturnsAsync([]);
        _activityRepository
            .Setup(r => r.GetCausalTimelineEntriesAsync(It.IsAny<IReadOnlyCollection<Guid>>()))
            .ReturnsAsync((IReadOnlyCollection<Guid> ids) => ids
                .Where(_entriesById.ContainsKey)
                .ToDictionary(id => id, id => _entriesById[id]));
        _activityRepository
            .Setup(r => r.GetCausalTimelinePageAsync(It.IsAny<CausalTimelineSubject>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(() => (_timelinePage, _timelineTotal));

        _connectedSystemRepository
            .Setup(r => r.GetPendingExportCoreAsync(It.IsAny<Guid>()))
            .ReturnsAsync((Guid id) => _pendingExportsById.GetValueOrDefault(id));
        _connectedSystemRepository
            .Setup(r => r.GetConnectedSystemObjectsCoreByIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>()))
            .ReturnsAsync((IReadOnlyCollection<Guid> ids) => ids.Where(_liveCsosById.ContainsKey).Select(id => _liveCsosById[id]).ToList());
        _connectedSystemRepository
            .Setup(r => r.GetDeletedCsoChangesByObjectIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>()))
            .ReturnsAsync([]);
        _connectedSystemRepository
            .Setup(r => r.GetConnectedSystemNamesAsync())
            .ReturnsAsync(new Dictionary<int, string> { [3] = "Yellowstone APAC" });
        _metaverseRepository
            .Setup(r => r.GetMetaverseObjectsCoreByIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>()))
            .ReturnsAsync((IReadOnlyCollection<Guid> ids) => ids.Where(_liveMvosById.ContainsKey).Select(id => _liveMvosById[id]).ToList());
        _metaverseRepository
            .Setup(r => r.GetDeletedMvoChangesByObjectIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>()))
            .ReturnsAsync([]);

        _application = new JimApplication(_repository.Object);
        _synchronisation = new SynchronisationController(new Mock<ILogger<SynchronisationController>>().Object, _application,
            new DynamicExpressoEvaluator(), new Mock<ICredentialProtectionService>().Object);
        _metaverse = new MetaverseController(new Mock<ILogger<MetaverseController>>().Object, _application);
    }

    [TearDown]
    public void TearDown() => _application?.Dispose();

    // ─── Connected System Object timeline ───

    [Test]
    public async Task GetConnectedSystemObjectCausalTimelineAsync_LiveObject_ReturnsAPageOfItemsEachWithItsNearestCauseAsync()
    {
        var cso = SeedLiveCso();
        var itemId = Guid.NewGuid();
        _timelinePage = [NewEntry(itemId, cso.Id)];
        _timelineTotal = 3;
        SeedRemovalEdge(itemId, "Tina Adams");

        var result = await _synchronisation.GetConnectedSystemObjectCausalTimelineAsync(3, cso.Id,
            new PaginationRequest { Page = 1, PageSize = 1 });

        var timeline = (CausalTimelineDto)((OkObjectResult)result).Value!;
        var row = timeline.Items.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(timeline.Object.Kind, Is.EqualTo(CausalTimelineSubjectKind.ConnectedSystemObject));
            Assert.That(timeline.Object.Id, Is.EqualTo(cso.Id));
            Assert.That(timeline.Object.DisplayName, Is.EqualTo("Mia Young"));
            Assert.That(timeline.Object.IsDeleted, Is.False);
            Assert.That(timeline.TotalCount, Is.EqualTo(3));
            Assert.That(timeline.HasNextPage, Is.True, "three items at one a page");
            Assert.That(row.Id, Is.EqualTo(itemId), "the item's own id, which pipes into its causes");
            Assert.That(row.TargetName, Is.EqualTo("Delta Synchronisation"));
            Assert.That(row.TargetContext, Is.EqualTo("Yellowstone APAC"));
            Assert.That(row.ObjectChangeType, Is.EqualTo(ObjectChangeType.AttributeFlow));
            Assert.That(row.NearestCauses.Single().EdgeType, Is.EqualTo(CausalEdgeType.MetaverseObjectDeletionCausedReferenceRemoval));
            Assert.That(row.NearestCauseCount, Is.EqualTo(1));
            Assert.That(row.Because, Does.StartWith("Tina Adams was deleted"));
        }
    }

    [Test]
    public async Task GetConnectedSystemObjectCausalTimelineAsync_UnknownObject_ReturnsNotFoundAsync()
    {
        var result = await _synchronisation.GetConnectedSystemObjectCausalTimelineAsync(3, Guid.NewGuid(), new PaginationRequest());

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task GetConnectedSystemObjectCausalTimelineAsync_ObjectInAnotherConnectedSystem_ReturnsNotFoundAsync()
    {
        var cso = SeedLiveCso();

        var result = await _synchronisation.GetConnectedSystemObjectCausalTimelineAsync(4, cso.Id, new PaginationRequest());

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task GetConnectedSystemObjectCausalTimelineAsync_ItemWithNoRecordedCause_SaysSoAsync()
    {
        var cso = SeedLiveCso();
        _timelinePage = [NewEntry(Guid.NewGuid(), cso.Id)];
        _timelineTotal = 1;

        var result = await _synchronisation.GetConnectedSystemObjectCausalTimelineAsync(3, cso.Id, new PaginationRequest());

        var row = ((CausalTimelineDto)((OkObjectResult)result).Value!).Items.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(row.NearestCauses, Is.Empty);
            Assert.That(row.Because, Is.EqualTo("No earlier causes recorded"));
        }
    }

    // ─── Metaverse Object timeline ───

    [Test]
    public async Task GetObjectCausalTimelineAsync_LiveObject_ReturnsItsTimelineAsync()
    {
        var mvo = SeedLiveMvo();
        var itemId = Guid.NewGuid();
        _timelinePage = [NewEntry(itemId, Guid.NewGuid())];
        _timelineTotal = 1;

        var result = await _metaverse.GetObjectCausalTimelineAsync(mvo.Id, new PaginationRequest());

        var timeline = (CausalTimelineDto)((OkObjectResult)result).Value!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(timeline.Object.Kind, Is.EqualTo(CausalTimelineSubjectKind.MetaverseObject));
            Assert.That(timeline.Object.ObjectTypeName, Is.EqualTo("User"));
            Assert.That(timeline.Items.Single().Id, Is.EqualTo(itemId));
            Assert.That(timeline.MetaverseObjectChangesNotRecorded, Is.False);
        }
    }

    [Test]
    public async Task GetObjectCausalTimelineAsync_UnknownObject_ReturnsNotFoundAsync()
    {
        var result = await _metaverse.GetObjectCausalTimelineAsync(Guid.NewGuid(), new PaginationRequest());

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    // ─── Queued by ───

    [Test]
    public async Task GetPendingExportQueuedByAsync_StagedByTwoRunsAndOnePurged_ListsTheTwoNewestFirstAndCountsTheThirdAsync()
    {
        var older = SeedStager(Executed.AddHours(-2));
        var newer = SeedStager(Executed);
        var pendingExport = new PendingExport { Id = Guid.NewGuid() };
        pendingExport.RecordStager(Guid.NewGuid());
        pendingExport.RecordStager(older);
        pendingExport.RecordStager(newer);
        _pendingExportsById[pendingExport.Id] = pendingExport;

        var result = await _synchronisation.GetPendingExportQueuedByAsync(pendingExport.Id);

        var queuedBy = (CausalQueuedByDto)((OkObjectResult)result).Value!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(queuedBy.PendingExportId, Is.EqualTo(pendingExport.Id));
            Assert.That(queuedBy.Runs.Select(r => r.Id), Is.EqualTo(new[] { newer, older }));
            Assert.That(queuedBy.NotRetainedCount, Is.EqualTo(1));
            Assert.That(queuedBy.EarlierRunsMayBeOmitted, Is.False);
        }
    }

    [Test]
    public async Task GetPendingExportQueuedByAsync_UnknownPendingExport_ReturnsNotFoundAsync()
    {
        var result = await _synchronisation.GetPendingExportQueuedByAsync(Guid.NewGuid());

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    // ─── Helpers ───

    private static CausalTimelineEntry NewEntry(Guid itemId, Guid? csoId, DateTime? executed = null)
    {
        return new CausalTimelineEntry
        {
            RunProfileExecutionItemId = itemId,
            ActivityId = Guid.NewGuid(),
            Executed = executed ?? Executed,
            ActivityTargetType = ActivityTargetType.ConnectedSystemRunProfile,
            ActivityTargetName = "Delta Synchronisation",
            ActivityTargetContext = "Yellowstone APAC",
            ConnectedSystemId = 3,
            RunType = ConnectedSystemRunType.DeltaSynchronisation,
            ObjectChangeType = ObjectChangeType.AttributeFlow,
            DisplayName = "Project Diamond",
            ObjectType = "group",
            ConnectedSystemObjectId = csoId,
            ExternalId = "G-1"
        };
    }

    private Guid SeedStager(DateTime executed)
    {
        var id = Guid.NewGuid();
        _entriesById[id] = NewEntry(id, Guid.NewGuid(), executed);
        return id;
    }

    private void SeedRemovalEdge(Guid effectItemId, string causeName)
    {
        _edgesByEffectItemId[effectItemId] =
        [
            new CausalEdge
            {
                Id = Guid.NewGuid(),
                EffectRunProfileExecutionItemId = effectItemId,
                CauseMetaverseObjectId = Guid.NewGuid(),
                CauseDisplayName = causeName,
                CauseObjectTypeName = "User",
                EdgeType = CausalEdgeType.MetaverseObjectDeletionCausedReferenceRemoval,
                ReasonCode = CausalReasonCode.AllAuthoritativeSourcesDisconnected,
                EffectAttributeName = "Static Members",
                ConnectedSystemId = 3,
                ConnectedSystemName = "Yellowstone APAC"
            }
        ];
    }

    private ConnectedSystemObject SeedLiveCso()
    {
        var externalIdAttribute = new ConnectedSystemObjectTypeAttribute { Id = 11, Name = "employeeID" };
        var displayNameAttribute = new ConnectedSystemObjectTypeAttribute { Id = 12, Name = "displayName" };
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = 3,
            ConnectedSystem = new ConnectedSystem { Id = 3, Name = "Yellowstone APAC" },
            Type = new ConnectedSystemObjectType { Name = "user" },
            ExternalIdAttributeId = externalIdAttribute.Id,
            AttributeValues =
            [
                new ConnectedSystemObjectAttributeValue { Attribute = externalIdAttribute, AttributeId = externalIdAttribute.Id, StringValue = "S8-352" },
                new ConnectedSystemObjectAttributeValue { Attribute = displayNameAttribute, AttributeId = displayNameAttribute.Id, StringValue = "Mia Young" }
            ]
        };
        _liveCsosById[cso.Id] = cso;
        return cso;
    }

    private MetaverseObject SeedLiveMvo()
    {
        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = new MetaverseObjectType { Name = "User", PluralName = "Users" },
            AttributeValues =
            [
                new MetaverseObjectAttributeValue { Attribute = new MetaverseAttribute { Name = "Display Name" }, StringValue = "Mia Young" }
            ]
        };
        _liveMvosById[mvo.Id] = mvo;
        return mvo;
    }
}
