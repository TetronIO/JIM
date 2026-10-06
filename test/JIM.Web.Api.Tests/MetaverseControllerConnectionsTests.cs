// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Logic;
using JIM.Models.Logic.Scoping;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Transactional;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for the connections endpoint on <see cref="MetaverseController"/> (#348): why a Metaverse Object is connected
/// where it is, and, on request, why not elsewhere, with the same words the portal shows.
/// </summary>
[TestFixture]
public class MetaverseControllerConnectionsTests
{
    private const int PersonTypeId = 3;
    private static readonly MetaverseAttribute Department = new() { Id = 1, Name = "Department", Type = AttributeDataType.Text };
    private static readonly MetaverseAttribute CostCentre = new() { Id = 2, Name = "Cost Centre", Type = AttributeDataType.Text };
    private static readonly MetaverseAttribute JobTitle = new() { Id = 3, Name = "Job Title", Type = AttributeDataType.Text };

    private readonly Guid _mvoId = Guid.NewGuid();
    private Mock<IRepository> _repo = null!;
    private Mock<IMetaverseRepository> _metaverseRepo = null!;
    private Mock<IConnectedSystemRepository> _connectedSystemRepo = null!;
    private JimApplication _application = null!;
    private MetaverseController _controller = null!;
    private List<SyncRule> _rules = null!;

    [SetUp]
    public void SetUp()
    {
        _repo = new Mock<IRepository>();
        _metaverseRepo = new Mock<IMetaverseRepository>();
        _connectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _repo.Setup(r => r.Metaverse).Returns(_metaverseRepo.Object);
        _repo.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepo.Object);
        _application = new JimApplication(_repo.Object);
        _controller = new MetaverseController(new Mock<ILogger<MetaverseController>>().Object, _application)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        // Jane Smith, Engineering, with no Cost Centre, against the PRD's Scenario 1 rule:
        // Department equals Finance AND (Cost Centre starts with FIN OR Job Title contains Accountant).
        _rules =
        [
            new SyncRule
            {
                Id = 42, Name = "Finance App Users Export", Direction = SyncRuleDirection.Export, Enabled = true, ProvisionToConnectedSystem = true,
                ConnectedSystemId = 10, ConnectedSystem = new ConnectedSystem { Id = 10, Name = "Finance App" },
                ConnectedSystemObjectTypeId = 100, ConnectedSystemObjectType = new ConnectedSystemObjectType { Id = 100, Name = "account" },
                MetaverseObjectTypeId = PersonTypeId,
                ObjectScopingCriteriaGroups =
                [
                    new SyncRuleScopingCriteriaGroup
                    {
                        Id = 1, Type = SearchGroupType.All,
                        Criteria = [new SyncRuleScopingCriteria { Id = 11, MetaverseAttribute = Department, ComparisonType = SearchComparisonType.Equals, StringValue = "Finance" }],
                        ChildGroups =
                        [
                            new SyncRuleScopingCriteriaGroup
                            {
                                Id = 2, Type = SearchGroupType.Any,
                                Criteria =
                                [
                                    new SyncRuleScopingCriteria { Id = 12, MetaverseAttribute = CostCentre, ComparisonType = SearchComparisonType.StartsWith, StringValue = "FIN" },
                                    new SyncRuleScopingCriteria { Id = 13, MetaverseAttribute = JobTitle, ComparisonType = SearchComparisonType.Contains, StringValue = "Accountant" }
                                ]
                            }
                        ]
                    }
                ]
            }
        ];

        _metaverseRepo.Setup(r => r.GetMetaverseObjectHeaderAsync(_mvoId)).ReturnsAsync(new MetaverseObjectHeader
        {
            Id = _mvoId, TypeId = PersonTypeId, TypeName = "Person", TypePluralName = "People", CachedDisplayName = "Jane Smith"
        });
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemObjectsCoreByMetaverseObjectIdAsync(_mvoId)).ReturnsAsync([]);
        _connectedSystemRepo.Setup(r => r.GetSyncRulesForScopingExplanationAsync(PersonTypeId)).ReturnsAsync(() => _rules);
        _metaverseRepo.Setup(r => r.GetMetaverseObjectAttributeValuesAsync(_mvoId, It.IsAny<IReadOnlyCollection<int>>())).ReturnsAsync(
        [
            new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = Department.Id, StringValue = "Engineering" },
            new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = JobTitle.Id, StringValue = "Software Engineer" }
        ]);
    }

    [TearDown]
    public void TearDown() => _application?.Dispose();

    [Test]
    public async Task GetObjectConnectionsAsync_UnknownObject_ReturnsNotFoundAsync()
    {
        var result = await _controller.GetObjectConnectionsAsync(Guid.NewGuid());

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task GetObjectConnectionsAsync_NotConnectedNotRequested_OmitsItAsync()
    {
        var payload = await OkPayload(_controller.GetObjectConnectionsAsync(_mvoId));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(payload.MetaverseObjectId, Is.EqualTo(_mvoId));
            Assert.That(payload.DisplayName, Is.EqualTo("Jane Smith"));
            Assert.That(payload.Connections, Is.Empty);
            Assert.That(payload.NotConnected, Is.Null);
        }
    }

    [Test]
    public async Task GetObjectConnectionsAsync_NotConnectedRequested_ReturnsTheSameWordsThePortalShowsAsync()
    {
        var payload = await OkPayload(_controller.GetObjectConnectionsAsync(_mvoId, includeNotConnected: true));

        var entry = payload.NotConnected!.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entry.Reason, Is.EqualTo(NotConnectedReason.NotInScope));
            Assert.That(entry.Hint, Is.EqualTo("Fails on Department; Cost Centre or Job Title"));
            Assert.That(entry.BulletsTitle, Is.EqualTo("To come into scope"));
            Assert.That(entry.Bullets.Select(b => b.Text), Is.EqualTo(new[]
            {
                "Department must equal \"Finance\" (currently \"Engineering\")",
                "and either Cost Centre starts with \"FIN\" (currently no value), or Job Title contains \"Accountant\" (currently \"Software Engineer\")"
            }));
            Assert.That(entry.Bullets[0].Segments.Select(s => s.Kind), Does.Contain(ExplanationSegmentKind.Attribute));
            Assert.That(entry.Summary, Does.StartWith("Jane Smith is not provisioned to Finance App.\n"));
            Assert.That(entry.Scoping.Outcome, Is.EqualTo(ScopingRuleOutcome.OutOfScope));
        }
    }

    /// <summary>
    /// PRD requirement 26: criteria come flat as well as in the tree, each with a path locating it, so a client can
    /// list every failing criterion without walking the groups.
    /// </summary>
    [Test]
    public async Task GetObjectConnectionsAsync_NestedCriteria_AreAlsoReturnedFlatWithTheirPathsAsync()
    {
        var payload = await OkPayload(_controller.GetObjectConnectionsAsync(_mvoId, includeNotConnected: true));

        var scoping = payload.NotConnected!.Single().Scoping;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(scoping.Criteria.Select(c => (c.Path, c.AttributeName, c.Met, c.Outcome)), Is.EqualTo(new[]
            {
                ("1.1", "Department", false, ScopingCriterionOutcome.NotMet),
                ("1.2.1", "Cost Centre", false, ScopingCriterionOutcome.NoValue),
                ("1.2.2", "Job Title", false, ScopingCriterionOutcome.NotMet)
            }));
            Assert.That(scoping.Criteria[0].Description, Is.EqualTo("Department equals Finance"));
            Assert.That(scoping.Criteria[0].ActualDescription, Is.EqualTo("is Engineering"));
            Assert.That(scoping.Groups.Single().ChildGroups.Single().Path, Is.EqualTo("1.2"));
            Assert.That(scoping.Groups.Single().ChildGroups.Single().Criteria, Has.Count.EqualTo(2), "the tree keeps its shape too");
        }
    }

    [Test]
    public async Task GetObjectConnectionsAsync_JoinedConnection_CarriesTheJoinRecordAndScopingAsync()
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(), ConnectedSystemId = 10, ConnectedSystem = new ConnectedSystem { Id = 10, Name = "Finance App" },
            TypeId = 100, Type = new ConnectedSystemObjectType { Id = 100, Name = "account" }, MetaverseObjectId = _mvoId,
            Status = ConnectedSystemObjectStatus.Normal, AttributeValues = []
        };
        cso.RecordJoin(ConnectedSystemObjectJoinMethod.Provisioning, _rules[0], new DateTime(2026, 3, 14, 9, 12, 0, DateTimeKind.Utc));
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemObjectsCoreByMetaverseObjectIdAsync(_mvoId)).ReturnsAsync([cso]);
        _connectedSystemRepo.Setup(r => r.GetPendingExportsLightweightByConnectedSystemObjectIdsAsync(It.IsAny<IEnumerable<Guid>>()))
            .ReturnsAsync(new Dictionary<Guid, PendingExport>());
        _connectedSystemRepo.Setup(r => r.GetJoinHistoryAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<IReadOnlyDictionary<Guid, Guid>>()))
            .ReturnsAsync([]);

        var payload = await OkPayload(_controller.GetObjectConnectionsAsync(_mvoId));

        var row = payload.Connections.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(row.ConnectedSystemObjectId, Is.EqualTo(cso.Id));
            Assert.That(row.IsTarget, Is.True);
            Assert.That(row.Join.Method, Is.EqualTo(ConnectedSystemObjectJoinMethod.Provisioning));
            Assert.That(row.Join.Source, Is.EqualTo(JoinRecordSource.Recorded));
            Assert.That(row.Join.Description, Is.EqualTo("Provisioned by the Synchronisation Rule \"Finance App Users Export\""));
            Assert.That(row.Join.DateJoined, Is.EqualTo(cso.DateJoined));
            Assert.That(row.Scoping.Single().Hint, Is.EqualTo("Fails on Department; Cost Centre or Job Title"),
                "a joined connection now out of scope of its export rule says so");
        }
    }

    [Test]
    public void GetObjectConnectionsAsync_IsAdministratorOnlyAtItsRoute()
    {
        var controllerAuthorise = typeof(MetaverseController).GetCustomAttribute<AuthorizeAttribute>();
        var action = typeof(MetaverseController).GetMethod(nameof(MetaverseController.GetObjectConnectionsAsync))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(controllerAuthorise?.Roles, Is.EqualTo("Administrator"));
            Assert.That(action.GetCustomAttribute<AllowAnonymousAttribute>(), Is.Null);
            Assert.That(action.GetCustomAttribute<HttpGetAttribute>()?.Template, Is.EqualTo("objects/{id:guid}/connections"));
            Assert.That(action.GetCustomAttribute<HttpGetAttribute>()?.Name, Is.EqualTo("GetObjectConnections"));
        }
    }

    private static async Task<MetaverseObjectConnectionExplanationsDto> OkPayload(Task<IActionResult> action)
    {
        var result = await action;
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var payload = ((OkObjectResult)result).Value as MetaverseObjectConnectionExplanationsDto;
        Assert.That(payload, Is.Not.Null);
        return payload!;
    }
}
