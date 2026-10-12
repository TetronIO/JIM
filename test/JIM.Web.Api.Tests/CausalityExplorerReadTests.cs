// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Application.Servers;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.TestSupport;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The Causality Explorer's application reads (#2100): an object's timeline, a Pending Export's queueing runs, the
/// nearest causes each of their rows words, and the labels a trail names its foci by.
///
/// What these pin is agreement and cost. A row's nearest causes must be the walk's own first level, grouped by the
/// walk's own rule, so the line a row reads can never disagree with the Lineage it opens. And a page of rows must cost
/// a fixed handful of reads however many rows it holds: a read per row is exactly what the batched repository methods
/// exist to avoid.
/// </summary>
[TestFixture]
public class CausalityExplorerReadTests
{
    private static readonly DateTime SyncExecuted = new(2026, 10, 12, 11, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ImportExecuted = new(2026, 10, 12, 6, 30, 0, DateTimeKind.Utc);

    private Mock<IRepository> _mockRepository = null!;
    private Mock<IActivityRepository> _mockActivityRepository = null!;
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepository = null!;
    private Mock<IMetaverseRepository> _mockMetaverseRepository = null!;
    private InMemoryServiceSettingsRepository _serviceSettings = null!;
    private JimApplication _application = null!;

    private readonly Dictionary<Guid, List<CausalEdge>> _edgesByEffectItemId = new();
    private readonly Dictionary<Guid, CausalChainItemSummary> _summariesById = new();
    private readonly Dictionary<Guid, CausalSourceImportEvent> _importEventsByItemId = new();
    private readonly Dictionary<Guid, Guid> _exportItemIdsByPendingExportId = new();
    private readonly Dictionary<Guid, CausalTimelineEntry> _entriesById = new();
    private readonly Dictionary<Guid, PendingExport> _pendingExportsById = new();
    private readonly Dictionary<Guid, ConnectedSystemObject> _liveCsosById = new();
    private readonly Dictionary<Guid, ConnectedSystemObjectChange> _deletedCsoRecordsById = new();
    private readonly Dictionary<Guid, MetaverseObject> _liveMvosById = new();
    private readonly Dictionary<Guid, MetaverseObjectChange> _deletedMvoRecordsById = new();
    private List<CausalTimelineEntry> _timelinePage = [];
    private int _timelineTotal;

    [SetUp]
    public void SetUp()
    {
        _edgesByEffectItemId.Clear();
        _summariesById.Clear();
        _importEventsByItemId.Clear();
        _exportItemIdsByPendingExportId.Clear();
        _entriesById.Clear();
        _pendingExportsById.Clear();
        _liveCsosById.Clear();
        _deletedCsoRecordsById.Clear();
        _liveMvosById.Clear();
        _deletedMvoRecordsById.Clear();
        _timelinePage = [];
        _timelineTotal = 0;

        _mockRepository = new Mock<IRepository>();
        _mockActivityRepository = new Mock<IActivityRepository>();
        _mockConnectedSystemRepository = new Mock<IConnectedSystemRepository>();
        _mockMetaverseRepository = new Mock<IMetaverseRepository>();
        _serviceSettings = new InMemoryServiceSettingsRepository();
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepository.Object);
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepository.Object);
        _mockRepository.Setup(r => r.Metaverse).Returns(_mockMetaverseRepository.Object);
        _mockRepository.Setup(r => r.ServiceSettings).Returns(_serviceSettings);

        _mockActivityRepository
            .Setup(r => r.GetCausalEdgesByEffectRunProfileExecutionItemIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>()))
            .ReturnsAsync((IReadOnlyCollection<Guid> ids) =>
                ids.SelectMany(id => _edgesByEffectItemId.TryGetValue(id, out var edges) ? edges : []).ToList());

        _mockActivityRepository
            .Setup(r => r.GetRunProfileExecutionItemCausalSummariesAsync(It.IsAny<IReadOnlyCollection<Guid>>()))
            .ReturnsAsync((IReadOnlyCollection<Guid> ids) => ids
                .Where(_summariesById.ContainsKey)
                .ToDictionary(id => id, id => _summariesById[id]));

        _mockActivityRepository
            .Setup(r => r.GetExportExecutionItemIdsByPendingExportIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>()))
            .ReturnsAsync((IReadOnlyCollection<Guid> ids) => ids
                .Where(_exportItemIdsByPendingExportId.ContainsKey)
                .ToDictionary(id => id, id => _exportItemIdsByPendingExportId[id]));

        _mockActivityRepository
            .Setup(r => r.GetLatestImportEventsForItemsAsync(It.IsAny<IReadOnlyCollection<Guid>>()))
            .ReturnsAsync((IReadOnlyCollection<Guid> ids) => ids
                .Where(_importEventsByItemId.ContainsKey)
                .ToDictionary(id => id, id => _importEventsByItemId[id]));

        _mockActivityRepository
            .Setup(r => r.GetCausalTimelineEntriesAsync(It.IsAny<IReadOnlyCollection<Guid>>()))
            .ReturnsAsync((IReadOnlyCollection<Guid> ids) => ids
                .Where(_entriesById.ContainsKey)
                .ToDictionary(id => id, id => _entriesById[id]));

        _mockActivityRepository
            .Setup(r => r.GetCausalTimelinePageAsync(It.IsAny<CausalTimelineSubject>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(() => (_timelinePage, _timelineTotal));

        // The walk's per-item reads, which the explorer must never fall back to: a page of rows asks once, in a batch.
        _mockActivityRepository
            .Setup(r => r.GetLatestImportItemForCsoAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<Guid>()))
            .ReturnsAsync((CausalSourceImportEvent?)null);
        _mockActivityRepository
            .Setup(r => r.GetLatestImportItemForExternalIdAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<Guid>()))
            .ReturnsAsync((CausalSourceImportEvent?)null);

        _mockConnectedSystemRepository
            .Setup(r => r.GetPendingExportCoreAsync(It.IsAny<Guid>()))
            .ReturnsAsync((Guid id) => _pendingExportsById.GetValueOrDefault(id));

        _mockConnectedSystemRepository
            .Setup(r => r.GetConnectedSystemObjectsCoreByIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>()))
            .ReturnsAsync((IReadOnlyCollection<Guid> ids) => ids.Where(_liveCsosById.ContainsKey).Select(id => _liveCsosById[id]).ToList());

        _mockConnectedSystemRepository
            .Setup(r => r.GetDeletedCsoChangesByObjectIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>()))
            .ReturnsAsync((IReadOnlyCollection<Guid> ids) =>
                ids.Where(_deletedCsoRecordsById.ContainsKey).Select(id => _deletedCsoRecordsById[id]).ToList());

        _mockConnectedSystemRepository
            .Setup(r => r.GetConnectedSystemNamesAsync())
            .ReturnsAsync(new Dictionary<int, string> { [3] = "Yellowstone APAC" });

        _mockMetaverseRepository
            .Setup(r => r.GetMetaverseObjectsCoreByIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>()))
            .ReturnsAsync((IReadOnlyCollection<Guid> ids) => ids.Where(_liveMvosById.ContainsKey).Select(id => _liveMvosById[id]).ToList());

        _mockMetaverseRepository
            .Setup(r => r.GetDeletedMvoChangesByObjectIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>()))
            .ReturnsAsync((IReadOnlyCollection<Guid> ids) =>
                ids.Where(_deletedMvoRecordsById.ContainsKey).Select(id => _deletedMvoRecordsById[id]).ToList());

        _application = new JimApplication(_mockRepository.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _application?.Dispose();
    }

    #region Nearest causes

    /// <summary>
    /// The agreement the explorer depends on: the causes a row words are the walk's own first level, grouped by its own
    /// rule, so opening the row's Lineage never shows a different story from the line that led there.
    /// </summary>
    [Test]
    public async Task GetNearestCauses_ItemWithEdges_GroupsThemAsTheWalksFirstLevelAsync()
    {
        var itemId = SeedExportSideItem();
        var outcomeId = Guid.NewGuid();
        SeedEdges(itemId,
            NewEdge(itemId, outcomeId, reasonCode: CausalReasonCode.AllAuthoritativeSourcesDisconnected),
            NewEdge(itemId, outcomeId, reasonCode: CausalReasonCode.AllAuthoritativeSourcesDisconnected),
            NewEdge(itemId, outcomeId, reasonCode: CausalReasonCode.AllAuthoritativeSourcesDisconnected),
            NewEdge(itemId, outcomeId, reasonCode: CausalReasonCode.AuthoritativeSourceDisconnected));

        var nearest = await _application.Activities.GetNearestCausesAsync([itemId]);
        var chain = await _application.Activities.GetCausalChainAsync(itemId);

        var causes = nearest[itemId];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(causes.Cohorts.Select(c => (c.ReasonCode, c.MemberCount)),
                Is.EqualTo(chain.Cohorts.Select(c => (c.ReasonCode, c.MemberCount))));
            Assert.That(causes.Cohorts.Select(c => c.MemberCount), Is.EqualTo(new[] { 3, 1 }));
            Assert.That(causes.TotalCohortCount, Is.EqualTo(2));
            Assert.That(causes.Resolution, Is.EqualTo(CausalChainResolution.Resolved));
        }
    }

    /// <summary>
    /// A synchronisation's causes include the import that fed it, exactly as its Lineage shows it: the source data's
    /// arrival is a cause in its own right, and the hop that reaches the true root.
    /// </summary>
    [Test]
    public async Task GetNearestCauses_SynchronisationItem_IncludesTheImportThatFedItAsync()
    {
        var (itemId, csoId) = SeedSynchronisationItem();
        var importItemId = SeedImportEvent(itemId);

        var causes = (await _application.Activities.GetNearestCausesAsync([itemId]))[itemId];

        Assert.That(causes.Cohorts, Has.Count.EqualTo(1));
        var hop = causes.Cohorts[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(hop.SourceImportChangeType, Is.EqualTo(ObjectChangeType.Added));
            Assert.That(hop.ConnectedSystemName, Is.EqualTo("Yellowstone APAC"));
            Assert.That(hop.Members[0].RunProfileExecutionItemId, Is.EqualTo(importItemId));
            Assert.That(hop.Members[0].ConnectedSystemObjectId, Is.EqualTo(csoId));
            Assert.That(hop.Members[0].Occurred, Is.EqualTo(ImportExecuted));
            Assert.That(causes.Resolution, Is.EqualTo(CausalChainResolution.Resolved));
        }
    }

    /// <summary>
    /// A row has room for a few causes; the rest are counted, never silently dropped, and the ones shown are the first
    /// the Lineage shows.
    /// </summary>
    [Test]
    public async Task GetNearestCauses_MoreCohortsThanARowHolds_CarriesTheFirstAndTheTotalAsync()
    {
        var itemId = SeedExportSideItem();
        var outcomeId = Guid.NewGuid();
        var reasons = new[]
        {
            CausalReasonCode.AllAuthoritativeSourcesDisconnected,
            CausalReasonCode.AuthoritativeSourceDisconnected,
            CausalReasonCode.LastConnectorDisconnected,
            CausalReasonCode.LastConnectorDisconnectedNoSourcesConfigured,
            CausalReasonCode.NoConnectorRemainsStateConvergence
        };
        SeedEdges(itemId, reasons.Select(r => NewEdge(itemId, outcomeId, reasonCode: r)).ToArray());

        var causes = (await _application.Activities.GetNearestCausesAsync([itemId]))[itemId];
        var chain = await _application.Activities.GetCausalChainAsync(itemId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(causes.Cohorts, Has.Count.EqualTo(CausalNearestCauses.MaxCohorts));
            Assert.That(causes.TotalCohortCount, Is.EqualTo(5));
            Assert.That(causes.Cohorts.Select(c => c.ReasonCode),
                Is.EqualTo(chain.Cohorts.Take(CausalNearestCauses.MaxCohorts).Select(c => c.ReasonCode)));
        }
    }

    /// <summary>
    /// A confirming import names the Pending Export it confirms rather than an item; its cause is the export that ran
    /// it, resolved as the walk resolves it (#1528), so the row can say which export it confirmed.
    /// </summary>
    [Test]
    public async Task GetNearestCauses_ExportConfirmation_ReachesTheExportExecutionAsync()
    {
        var itemId = SeedExportSideItem(ObjectChangeType.Updated);
        var pendingExportId = Guid.NewGuid();
        var exportItemId = Guid.NewGuid();
        _exportItemIdsByPendingExportId[pendingExportId] = exportItemId;
        SeedEdges(itemId, NewConfirmationEdge(itemId, pendingExportId));

        var causes = (await _application.Activities.GetNearestCausesAsync([itemId]))[itemId];

        Assert.That(causes.Cohorts.Single().Members.Single().RunProfileExecutionItemId, Is.EqualTo(exportItemId));
    }

    /// <summary>
    /// A synchronisation item was fed by an import by definition, so where its record's deletion has nulled the id and
    /// no import can be found, the story was cut short rather than complete, exactly as the walk reports it (#1495).
    /// </summary>
    [Test]
    public async Task GetNearestCauses_SeveredSynchronisationItemWithNoImport_ReportsCauseNotRetainedAsync()
    {
        var itemId = Guid.NewGuid();
        _summariesById[itemId] = new CausalChainItemSummary
        {
            Id = itemId,
            ObjectChangeType = ObjectChangeType.Disconnected,
            ConnectedSystemObjectId = null,
            ConnectedSystemId = 3,
            ExternalIdSnapshot = "S8-352",
            ActivityExecuted = SyncExecuted
        };

        var causes = (await _application.Activities.GetNearestCausesAsync([itemId]))[itemId];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(causes.Cohorts, Is.Empty);
            Assert.That(causes.TotalCohortCount, Is.Zero);
            Assert.That(causes.Resolution, Is.EqualTo(CausalChainResolution.CauseNotRetained));
        }
    }

    /// <summary>
    /// The ordinary case: most items describe a change with a local explanation, and say so as a complete ending.
    /// </summary>
    [Test]
    public async Task GetNearestCauses_ItemWithNothingRecorded_ReportsNoFurtherCausesAsync()
    {
        var itemId = SeedExportSideItem();

        var causes = (await _application.Activities.GetNearestCausesAsync([itemId]))[itemId];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(causes.Cohorts, Is.Empty);
            Assert.That(causes.TotalCohortCount, Is.Zero);
            Assert.That(causes.Resolution, Is.EqualTo(CausalChainResolution.NoFurtherCauses));
        }
    }

    /// <summary>
    /// An item history retention has removed has no causes to state; it is absent, for the caller to say so, rather
    /// than reported as having none.
    /// </summary>
    [Test]
    public async Task GetNearestCauses_ItemNoLongerRetained_IsAbsentAsync()
    {
        var retainedId = SeedExportSideItem();
        var purgedId = Guid.NewGuid();

        var nearest = await _application.Activities.GetNearestCausesAsync([retainedId, purgedId]);

        Assert.That(nearest.Keys, Is.EquivalentTo(new[] { retainedId }));
    }

    /// <summary>
    /// The cost the explorer is built around: a page of rows reads each source once, and asks for source imports only
    /// for the items that have one, never per row.
    /// </summary>
    [Test]
    public async Task GetNearestCauses_PageOfItems_ReadsEachSourceOnceAsync()
    {
        var syncItemIds = new List<Guid>();
        var ids = new List<Guid>();
        for (var i = 0; i < 25; i++)
        {
            var (syncItemId, _) = SeedSynchronisationItem();
            SeedImportEvent(syncItemId);
            syncItemIds.Add(syncItemId);
            var exportItemId = SeedExportSideItem();
            SeedEdges(exportItemId, NewEdge(exportItemId, causeItemId: syncItemId));
            ids.AddRange([syncItemId, exportItemId]);
        }

        var nearest = await _application.Activities.GetNearestCausesAsync(ids);

        Assert.That(nearest, Has.Count.EqualTo(50));
        _mockActivityRepository.Verify(r => r.GetRunProfileExecutionItemCausalSummariesAsync(It.IsAny<IReadOnlyCollection<Guid>>()), Times.Once);
        _mockActivityRepository.Verify(r => r.GetCausalEdgesByEffectRunProfileExecutionItemIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>()), Times.Once);
        _mockActivityRepository.Verify(r => r.GetLatestImportEventsForItemsAsync(
            It.Is<IReadOnlyCollection<Guid>>(asked => asked.Count == 25 && asked.All(syncItemIds.Contains))), Times.Once);
        _mockActivityRepository.Verify(r => r.GetLatestImportItemForCsoAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<Guid>()), Times.Never);
        _mockActivityRepository.Verify(r => r.GetLatestImportItemForExternalIdAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<Guid>()), Times.Never);
    }

    #endregion

    #region Timeline

    /// <summary>
    /// A page of an object's timeline: the repository's rows in its order, each with its nearest causes, worked out
    /// from the rows themselves rather than by reading each item again.
    /// </summary>
    [Test]
    public async Task GetCausalTimeline_Page_ReturnsTheRowsWithTheirNearestCausesAsync()
    {
        var csoId = Guid.NewGuid();
        var syncItemId = Guid.NewGuid();
        var importItemId = SeedImportEvent(syncItemId);
        var exportItemId = Guid.NewGuid();
        SeedEdges(exportItemId, NewEdge(exportItemId, causeItemId: syncItemId));
        _timelinePage =
        [
            NewEntry(exportItemId, ObjectChangeType.Exported, csoId),
            NewEntry(syncItemId, ObjectChangeType.AttributeFlow, csoId)
        ];
        _timelineTotal = 7;

        var timeline = await _application.Activities.GetCausalTimelineAsync(
            new CausalTimelineSubject(CausalTimelineSubjectKind.ConnectedSystemObject, csoId));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(timeline.Rows.Select(r => r.Entry.RunProfileExecutionItemId), Is.EqualTo(new[] { exportItemId, syncItemId }));
            Assert.That(timeline.Rows[0].NearestCauses.Cohorts.Single().Members.Single().RunProfileExecutionItemId, Is.EqualTo(syncItemId));
            Assert.That(timeline.Rows[1].NearestCauses.Cohorts.Single().Members.Single().RunProfileExecutionItemId, Is.EqualTo(importItemId));
            Assert.That(timeline.TotalCount, Is.EqualTo(7));
            Assert.That(timeline.Page, Is.EqualTo(1));
            Assert.That(timeline.PageSize, Is.EqualTo(ActivityServer.CausalTimelineDefaultPageSize));
        }

        _mockActivityRepository.Verify(r => r.GetRunProfileExecutionItemCausalSummariesAsync(It.IsAny<IReadOnlyCollection<Guid>>()), Times.Never);
    }

    [Test]
    public async Task GetCausalTimeline_PageAndPageSize_TranslateToSkipAndTakeAsync()
    {
        var subject = new CausalTimelineSubject(CausalTimelineSubjectKind.ConnectedSystemObject, Guid.NewGuid());

        await _application.Activities.GetCausalTimelineAsync(subject, page: 3, pageSize: 20);

        _mockActivityRepository.Verify(r => r.GetCausalTimelinePageAsync(subject, 40, 20), Times.Once);
    }

    /// <summary>
    /// Paging values arrive from addresses and API callers; out-of-range ones are brought into range rather than
    /// failing the read or asking the database for an unbounded page.
    /// </summary>
    [TestCase(0, 1000, 1, 100)]
    [TestCase(-5, 0, 1, 1)]
    [TestCase(2, -1, 2, 1)]
    public async Task GetCausalTimeline_OutOfRangePaging_IsClampedAsync(int page, int pageSize, int expectedPage, int expectedPageSize)
    {
        var subject = new CausalTimelineSubject(CausalTimelineSubjectKind.ConnectedSystemObject, Guid.NewGuid());

        var timeline = await _application.Activities.GetCausalTimelineAsync(subject, page, pageSize);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(timeline.Page, Is.EqualTo(expectedPage));
            Assert.That(timeline.PageSize, Is.EqualTo(expectedPageSize));
        }

        _mockActivityRepository.Verify(r => r.GetCausalTimelinePageAsync(
            subject, (expectedPage - 1) * expectedPageSize, expectedPageSize), Times.Once);
    }

    /// <summary>
    /// A page number far past the end must not overflow into a negative skip.
    /// </summary>
    [Test]
    public async Task GetCausalTimeline_HugePageNumber_DoesNotOverflowAsync()
    {
        var subject = new CausalTimelineSubject(CausalTimelineSubjectKind.ConnectedSystemObject, Guid.NewGuid());

        await _application.Activities.GetCausalTimelineAsync(subject, int.MaxValue, 100);

        _mockActivityRepository.Verify(r => r.GetCausalTimelinePageAsync(subject, It.Is<int>(skip => skip >= 0), 100), Times.Once);
    }

    /// <summary>
    /// Without Metaverse Object change tracking, a Metaverse Object's timeline holds only what its joined objects
    /// recorded, and must say so rather than imply it is the whole story (PRD Scenario 5).
    /// </summary>
    [Test]
    public async Task GetCausalTimeline_MetaverseObjectWithChangeTrackingOff_SaysChangesAreNotRecordedAsync()
    {
        await DisableMetaverseObjectChangeTrackingAsync();

        var timeline = await _application.Activities.GetCausalTimelineAsync(
            new CausalTimelineSubject(CausalTimelineSubjectKind.MetaverseObject, Guid.NewGuid()));

        Assert.That(timeline.MetaverseObjectChangesNotRecorded, Is.True);
    }

    [Test]
    public async Task GetCausalTimeline_MetaverseObjectWithChangeTrackingOn_DoesNotSayChangesAreMissingAsync()
    {
        var timeline = await _application.Activities.GetCausalTimelineAsync(
            new CausalTimelineSubject(CausalTimelineSubjectKind.MetaverseObject, Guid.NewGuid()));

        Assert.That(timeline.MetaverseObjectChangesNotRecorded, Is.False);
    }

    /// <summary>
    /// A Connected System Object's timeline is its own items, which change tracking does not govern.
    /// </summary>
    [Test]
    public async Task GetCausalTimeline_ConnectedSystemObjectWithChangeTrackingOff_DoesNotSayChangesAreMissingAsync()
    {
        await DisableMetaverseObjectChangeTrackingAsync();

        var timeline = await _application.Activities.GetCausalTimelineAsync(
            new CausalTimelineSubject(CausalTimelineSubjectKind.ConnectedSystemObject, Guid.NewGuid()));

        Assert.That(timeline.MetaverseObjectChangesNotRecorded, Is.False);
    }

    [Test]
    public async Task GetCausalTimeline_KnownObject_CarriesItsHeaderAsync()
    {
        var cso = SeedLiveCso();

        var timeline = await _application.Activities.GetCausalTimelineAsync(
            new CausalTimelineSubject(CausalTimelineSubjectKind.ConnectedSystemObject, cso.Id));

        Assert.That(timeline.Header?.DisplayName, Is.EqualTo("Mia Young"));
    }

    [Test]
    public async Task GetCausalTimeline_UnknownObject_HasNoHeaderAsync()
    {
        var timeline = await _application.Activities.GetCausalTimelineAsync(
            new CausalTimelineSubject(CausalTimelineSubjectKind.MetaverseObject, Guid.NewGuid()));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(timeline.Header, Is.Null);
            Assert.That(timeline.Rows, Is.Empty);
        }
    }

    #endregion

    #region Object headers

    [Test]
    public async Task GetCausalObjectHeaders_LiveConnectedSystemObject_DescribesTheObjectAsync()
    {
        var cso = SeedLiveCso();
        var subject = new CausalTimelineSubject(CausalTimelineSubjectKind.ConnectedSystemObject, cso.Id);

        var header = (await _application.Activities.GetCausalObjectHeadersAsync([subject]))[subject];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(header.Subject, Is.EqualTo(subject));
            Assert.That(header.IsDeleted, Is.False);
            Assert.That(header.DeletedAt, Is.Null);
            Assert.That(header.DisplayName, Is.EqualTo("Mia Young"));
            Assert.That(header.ExternalId, Is.EqualTo("S8-352"));
            Assert.That(header.ObjectTypeName, Is.EqualTo("user"));
            Assert.That(header.ConnectedSystemId, Is.EqualTo(3));
            Assert.That(header.ConnectedSystemName, Is.EqualTo("Yellowstone APAC"));
        }
    }

    /// <summary>
    /// A deleted object is still one the explorer can focus on; what is known of it comes from its deletion record.
    /// </summary>
    [Test]
    public async Task GetCausalObjectHeaders_DeletedConnectedSystemObject_DescribesItFromItsDeletionRecordAsync()
    {
        var csoId = Guid.NewGuid();
        var deletedAt = new DateTime(2026, 10, 1, 9, 15, 0, DateTimeKind.Utc);
        _deletedCsoRecordsById[csoId] = new ConnectedSystemObjectChange
        {
            Id = Guid.NewGuid(),
            ChangeType = ObjectChangeType.Deleted,
            ChangeTime = deletedAt,
            ConnectedSystemId = 3,
            DeletedConnectedSystemObjectId = csoId,
            DeletedObjectDisplayName = "Mia Young",
            DeletedObjectExternalId = "S8-352",
            DeletedObjectType = new ConnectedSystemObjectType { Name = "user" }
        };
        var subject = new CausalTimelineSubject(CausalTimelineSubjectKind.ConnectedSystemObject, csoId);

        var header = (await _application.Activities.GetCausalObjectHeadersAsync([subject]))[subject];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(header.IsDeleted, Is.True);
            Assert.That(header.DeletedAt, Is.EqualTo(deletedAt));
            Assert.That(header.DisplayName, Is.EqualTo("Mia Young"));
            Assert.That(header.ExternalId, Is.EqualTo("S8-352"));
            Assert.That(header.ObjectTypeName, Is.EqualTo("user"));
            Assert.That(header.ConnectedSystemId, Is.EqualTo(3));
            Assert.That(header.ConnectedSystemName, Is.EqualTo("Yellowstone APAC"));
        }
    }

    [Test]
    public async Task GetCausalObjectHeaders_LiveMetaverseObject_DescribesTheObjectAsync()
    {
        var mvo = SeedLiveMvo();
        var subject = new CausalTimelineSubject(CausalTimelineSubjectKind.MetaverseObject, mvo.Id);

        var header = (await _application.Activities.GetCausalObjectHeadersAsync([subject]))[subject];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(header.IsDeleted, Is.False);
            Assert.That(header.DisplayName, Is.EqualTo("Mia Young"));
            Assert.That(header.ObjectTypeName, Is.EqualTo("User"));
            Assert.That(header.ObjectTypePluralName, Is.EqualTo("Users"));
            Assert.That(header.ConnectedSystemId, Is.Null);
        }
    }

    [Test]
    public async Task GetCausalObjectHeaders_DeletedMetaverseObject_DescribesItFromItsDeletionRecordAsync()
    {
        var mvoId = Guid.NewGuid();
        var deletedAt = new DateTime(2026, 10, 2, 14, 0, 0, DateTimeKind.Utc);
        _deletedMvoRecordsById[mvoId] = new MetaverseObjectChange
        {
            Id = Guid.NewGuid(),
            ChangeType = ObjectChangeType.Deleted,
            ChangeTime = deletedAt,
            DeletedMetaverseObjectId = mvoId,
            DeletedObjectDisplayName = "Mia Young",
            DeletedObjectType = new MetaverseObjectType { Name = "User", PluralName = "Users" }
        };
        var subject = new CausalTimelineSubject(CausalTimelineSubjectKind.MetaverseObject, mvoId);

        var header = (await _application.Activities.GetCausalObjectHeadersAsync([subject]))[subject];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(header.IsDeleted, Is.True);
            Assert.That(header.DeletedAt, Is.EqualTo(deletedAt));
            Assert.That(header.DisplayName, Is.EqualTo("Mia Young"));
            Assert.That(header.ObjectTypeName, Is.EqualTo("User"));
            Assert.That(header.ObjectTypePluralName, Is.EqualTo("Users"));
        }
    }

    [Test]
    public async Task GetCausalObjectHeaders_ObjectNeitherLiveNorDeleted_IsAbsentAsync()
    {
        var subjects = new[]
        {
            new CausalTimelineSubject(CausalTimelineSubjectKind.ConnectedSystemObject, Guid.NewGuid()),
            new CausalTimelineSubject(CausalTimelineSubjectKind.MetaverseObject, Guid.NewGuid())
        };

        var headers = await _application.Activities.GetCausalObjectHeadersAsync(subjects);

        Assert.That(headers, Is.Empty);
    }

    /// <summary>
    /// Deletion records are read only for the objects that no longer exist, and the Connected System names only where
    /// a deleted object needs one: a trail of live objects costs one read per kind.
    /// </summary>
    [Test]
    public async Task GetCausalObjectHeaders_MixedLiveAndDeleted_ReadsDeletionRecordsOnlyForTheMissingAsync()
    {
        var live = SeedLiveCso();
        var deletedId = Guid.NewGuid();
        var subjects = new[]
        {
            new CausalTimelineSubject(CausalTimelineSubjectKind.ConnectedSystemObject, live.Id),
            new CausalTimelineSubject(CausalTimelineSubjectKind.ConnectedSystemObject, deletedId),
            new CausalTimelineSubject(CausalTimelineSubjectKind.MetaverseObject, SeedLiveMvo().Id)
        };

        await _application.Activities.GetCausalObjectHeadersAsync(subjects);

        _mockConnectedSystemRepository.Verify(r => r.GetDeletedCsoChangesByObjectIdsAsync(
            It.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(new[] { deletedId }))), Times.Once);
        _mockMetaverseRepository.Verify(r => r.GetDeletedMvoChangesByObjectIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>()), Times.Never);
        _mockConnectedSystemRepository.Verify(r => r.GetConnectedSystemNamesAsync(), Times.Never);
    }

    #endregion

    #region Queued by

    [Test]
    public async Task GetPendingExportQueuedBy_UnknownPendingExport_ReturnsNullAsync()
    {
        var queuedBy = await _application.Activities.GetPendingExportQueuedByAsync(Guid.NewGuid());

        Assert.That(queuedBy, Is.Null);
    }

    /// <summary>
    /// Every run that staged into the export, newest first, each with what caused it: the panel's answer to "which
    /// runs put this here, and why".
    /// </summary>
    [Test]
    public async Task GetPendingExportQueuedBy_SeveralStagers_ListsThemNewestFirstWithTheirCausesAsync()
    {
        var oldest = SeedStagerEntry(SyncExecuted.AddHours(-2));
        var middle = SeedStagerEntry(SyncExecuted.AddHours(-1));
        var newest = SeedStagerEntry(SyncExecuted);
        var importItemId = SeedImportEvent(newest);
        var pendingExport = SeedPendingExport(oldest, middle, newest);

        var queuedBy = await _application.Activities.GetPendingExportQueuedByAsync(pendingExport.Id);

        Assert.That(queuedBy, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(queuedBy!.PendingExportId, Is.EqualTo(pendingExport.Id));
            Assert.That(queuedBy!.Rows.Select(r => r.Entry.RunProfileExecutionItemId), Is.EqualTo(new[] { newest, middle, oldest }));
            Assert.That(queuedBy!.Rows[0].NearestCauses.Cohorts.Single().Members.Single().RunProfileExecutionItemId, Is.EqualTo(importItemId));
            Assert.That(queuedBy!.NotRetainedCount, Is.Zero);
            Assert.That(queuedBy!.EarlierRunsMayBeOmitted, Is.False);
        }
    }

    /// <summary>
    /// Two items of one run share their Activity's time, so their order comes from the order the export recorded them
    /// in, which is the order they staged, rather than from anything arbitrary such as their ids.
    /// </summary>
    [Test]
    public async Task GetPendingExportQueuedBy_StagersFromOneRun_OrdersThemByWhenTheyStagedAsync()
    {
        var first = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var second = Guid.Parse("00000000-0000-0000-0000-000000000001");
        SeedStagerEntry(SyncExecuted, first);
        SeedStagerEntry(SyncExecuted, second);
        var pendingExport = SeedPendingExport(first, second);

        var queuedBy = await _application.Activities.GetPendingExportQueuedByAsync(pendingExport.Id);

        Assert.That(queuedBy!.Rows.Select(r => r.Entry.RunProfileExecutionItemId), Is.EqualTo(new[] { second, first }));
    }

    /// <summary>
    /// A run whose item history retention has removed still staged the export; it is counted rather than dropped, so
    /// the panel can say runs are missing instead of implying the list is complete.
    /// </summary>
    [Test]
    public async Task GetPendingExportQueuedBy_StagerPurgedByRetention_CountsItAsNotRetainedAsync()
    {
        var retained = SeedStagerEntry(SyncExecuted);
        var pendingExport = SeedPendingExport(Guid.NewGuid(), retained);

        var queuedBy = await _application.Activities.GetPendingExportQueuedByAsync(pendingExport.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(queuedBy!.Rows.Select(r => r.Entry.RunProfileExecutionItemId), Is.EqualTo(new[] { retained }));
            Assert.That(queuedBy!.NotRetainedCount, Is.EqualTo(1));
        }
    }

    /// <summary>
    /// An export staged before runs were recorded on it names none; the panel says so, and nothing is read for it.
    /// </summary>
    [Test]
    public async Task GetPendingExportQueuedBy_NoRecordedStager_ReturnsAnEmptyListAsync()
    {
        var pendingExport = SeedPendingExport();

        var queuedBy = await _application.Activities.GetPendingExportQueuedByAsync(pendingExport.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(queuedBy!.Rows, Is.Empty);
            Assert.That(queuedBy!.NotRetainedCount, Is.Zero);
            Assert.That(queuedBy!.EarlierRunsMayBeOmitted, Is.False);
        }

        _mockActivityRepository.Verify(r => r.GetCausalTimelineEntriesAsync(It.IsAny<IReadOnlyCollection<Guid>>()), Times.Never);
    }

    /// <summary>
    /// The export keeps its most recent stagers; once it holds as many as it keeps, earlier ones may have been dropped
    /// to make room, and the panel must not present the list as every run.
    /// </summary>
    [TestCase(PendingExport.MaxRecordedStagers - 1, false)]
    [TestCase(PendingExport.MaxRecordedStagers, true)]
    [TestCase(PendingExport.MaxRecordedStagers + 5, true)]
    public async Task GetPendingExportQueuedBy_StagerListFull_SaysEarlierRunsMayBeOmittedAsync(int stagersRecorded, bool expected)
    {
        var stagers = Enumerable.Range(0, stagersRecorded).Select(i => SeedStagerEntry(SyncExecuted.AddMinutes(i))).ToArray();
        var pendingExport = SeedPendingExport(stagers);

        var queuedBy = await _application.Activities.GetPendingExportQueuedByAsync(pendingExport.Id);

        Assert.That(queuedBy!.EarlierRunsMayBeOmitted, Is.EqualTo(expected));
    }

    #endregion

    #region Focus labels

    /// <summary>
    /// A trail's labels resolve in one read per source, whatever its length; a focus that resolves to nothing is simply
    /// absent, for the trail to read "No longer retained".
    /// </summary>
    [Test]
    public async Task GetCausalFocusLabels_ItemsAndObjects_ResolveInABatchAsync()
    {
        var retainedItem = SeedStagerEntry(SyncExecuted);
        var purgedItem = Guid.NewGuid();
        var cso = SeedLiveCso();
        var csoSubject = new CausalTimelineSubject(CausalTimelineSubjectKind.ConnectedSystemObject, cso.Id);
        var goneSubject = new CausalTimelineSubject(CausalTimelineSubjectKind.MetaverseObject, Guid.NewGuid());

        var labels = await _application.Activities.GetCausalFocusLabelsAsync([retainedItem, purgedItem], [csoSubject, goneSubject]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(labels.Items.Keys, Is.EquivalentTo(new[] { retainedItem }));
            Assert.That(labels.Objects.Keys, Is.EquivalentTo(new[] { csoSubject }));
        }

        _mockActivityRepository.Verify(r => r.GetCausalTimelineEntriesAsync(It.IsAny<IReadOnlyCollection<Guid>>()), Times.Once);
    }

    [Test]
    public async Task GetCausalFocusLabels_NoItems_DoesNotReadItemsAsync()
    {
        var labels = await _application.Activities.GetCausalFocusLabelsAsync([], []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(labels.Items, Is.Empty);
            Assert.That(labels.Objects, Is.Empty);
        }

        _mockActivityRepository.Verify(r => r.GetCausalTimelineEntriesAsync(It.IsAny<IReadOnlyCollection<Guid>>()), Times.Never);
    }

    #endregion

    #region Helpers

    private Guid SeedExportSideItem(ObjectChangeType changeType = ObjectChangeType.Exported)
    {
        var itemId = Guid.NewGuid();
        _summariesById[itemId] = new CausalChainItemSummary
        {
            Id = itemId,
            ObjectChangeType = changeType,
            ConnectedSystemObjectId = Guid.NewGuid(),
            ConnectedSystemId = 3,
            ActivityExecuted = SyncExecuted
        };
        return itemId;
    }

    private (Guid ItemId, Guid CsoId) SeedSynchronisationItem()
    {
        var itemId = Guid.NewGuid();
        var csoId = Guid.NewGuid();
        _summariesById[itemId] = new CausalChainItemSummary
        {
            Id = itemId,
            ObjectChangeType = ObjectChangeType.AttributeFlow,
            ConnectedSystemObjectId = csoId,
            ConnectedSystemId = 3,
            ActivityExecuted = SyncExecuted
        };
        return (itemId, csoId);
    }

    private Guid SeedImportEvent(Guid forItemId)
    {
        var importItemId = Guid.NewGuid();
        _importEventsByItemId[forItemId] = new CausalSourceImportEvent
        {
            RunProfileExecutionItemId = importItemId,
            ChangeType = ObjectChangeType.Added,
            DisplayName = "Mia Young (S8-352)",
            ConnectedSystemId = 3,
            ConnectedSystemName = "Yellowstone APAC",
            Occurred = ImportExecuted
        };
        return importItemId;
    }

    private void SeedEdges(Guid effectItemId, params CausalEdge[] edges)
    {
        if (!_edgesByEffectItemId.TryGetValue(effectItemId, out var list))
        {
            list = [];
            _edgesByEffectItemId[effectItemId] = list;
        }

        list.AddRange(edges);
    }

    private static CausalEdge NewEdge(
        Guid effectItemId,
        Guid? effectOutcomeId = null,
        Guid? causeItemId = null,
        CausalReasonCode reasonCode = CausalReasonCode.AllAuthoritativeSourcesDisconnected)
    {
        return new CausalEdge
        {
            Id = Guid.NewGuid(),
            EffectRunProfileExecutionItemId = effectItemId,
            EffectSyncOutcomeId = effectOutcomeId,
            CauseRunProfileExecutionItemId = causeItemId,
            CauseMetaverseObjectId = Guid.NewGuid(),
            CauseDisplayName = "Mia Young",
            EdgeType = CausalEdgeType.MetaverseObjectDeletionCausedReferenceRemoval,
            ReasonCode = reasonCode,
            ConnectedSystemId = 3,
            ConnectedSystemName = "Yellowstone APAC"
        };
    }

    private static CausalEdge NewConfirmationEdge(Guid effectItemId, Guid pendingExportId)
    {
        return new CausalEdge
        {
            Id = Guid.NewGuid(),
            EffectRunProfileExecutionItemId = effectItemId,
            EffectSyncOutcomeId = Guid.NewGuid(),
            CausePendingExportId = pendingExportId,
            CauseConnectedSystemObjectId = Guid.NewGuid(),
            CauseDisplayName = "Mia Young",
            EdgeType = CausalEdgeType.ExportCausedImportConfirmation,
            ConnectedSystemId = 3,
            ConnectedSystemName = "Yellowstone APAC"
        };
    }

    private static CausalTimelineEntry NewEntry(Guid itemId, ObjectChangeType changeType, Guid? csoId, DateTime? executed = null)
    {
        return new CausalTimelineEntry
        {
            RunProfileExecutionItemId = itemId,
            ActivityId = Guid.NewGuid(),
            Executed = executed ?? SyncExecuted,
            ActivityTargetType = ActivityTargetType.ConnectedSystemRunProfile,
            ActivityTargetName = "Delta Synchronisation",
            ActivityTargetContext = "Yellowstone APAC",
            ConnectedSystemId = 3,
            ObjectChangeType = changeType,
            DisplayName = "Mia Young",
            ConnectedSystemObjectId = csoId,
            ExternalId = "S8-352"
        };
    }

    /// <summary>
    /// A staging synchronisation's item, retained and readable as a timeline entry.
    /// </summary>
    private Guid SeedStagerEntry(DateTime executed, Guid? itemId = null)
    {
        var id = itemId ?? Guid.NewGuid();
        _entriesById[id] = NewEntry(id, ObjectChangeType.AttributeFlow, Guid.NewGuid(), executed);
        return id;
    }

    private PendingExport SeedPendingExport(params Guid[] stagersOldestFirst)
    {
        var pendingExport = new PendingExport { Id = Guid.NewGuid() };
        foreach (var stager in stagersOldestFirst)
            pendingExport.RecordStager(stager);

        _pendingExportsById[pendingExport.Id] = pendingExport;
        return pendingExport;
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

    private async Task DisableMetaverseObjectChangeTrackingAsync()
    {
        await _serviceSettings.CreateSettingAsync(new ServiceSetting
        {
            Key = Constants.SettingKeys.ChangeTrackingMvoChangesEnabled,
            DisplayName = "Metaverse Object change tracking",
            Category = ServiceSettingCategory.History,
            ValueType = ServiceSettingValueType.Boolean,
            DefaultValue = "true",
            Value = "false"
        });
    }

    #endregion
}
