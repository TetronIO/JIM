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
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Enums;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using JIM.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The causality endpoints on <see cref="ActivitiesController"/> (#1811): an execution item's causes (Phase 1's upward
/// walk, which until now had no API) and its consequences, with one cohort's members paged by its key.
/// </summary>
[TestFixture]
public class ActivitiesControllerCausalityTests
{
    private readonly Guid _itemId = Guid.NewGuid();
    private Mock<IRepository> _repository = null!;
    private Mock<IActivityRepository> _activityRepository = null!;
    private JimApplication _application = null!;
    private ActivitiesController _controller = null!;

    private static readonly CausalConsequenceCohortTuple RemovalTuple = new(CausalConsequenceHopKind.Edge,
        CausalEdgeType.MetaverseObjectDeletionCausedReferenceRemoval, CausalReasonCode.AuthoritativeSourceDisconnected,
        1, null, "Group", "Static Members", ObjectChangeType.PendingExport, null, null);

    [SetUp]
    public void SetUp()
    {
        _repository = new Mock<IRepository>();
        _activityRepository = new Mock<IActivityRepository>();
        _repository.Setup(r => r.Activity).Returns(_activityRepository.Object);
        _activityRepository.Setup(r => r.GetActivityRunProfileExecutionItemCoreAsync(_itemId))
            .ReturnsAsync(new ActivityRunProfileExecutionItem { Id = _itemId });
        _activityRepository.Setup(r => r.GetCausalEdgesByEffectRunProfileExecutionItemIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>()))
            .ReturnsAsync([]);
        _activityRepository.Setup(r => r.GetExportExecutionItemIdsByPendingExportIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>()))
            .ReturnsAsync([]);
        _activityRepository.Setup(r => r.GetRunProfileExecutionItemCausalSummariesAsync(It.IsAny<IReadOnlyCollection<Guid>>()))
            .ReturnsAsync([]);
        _activityRepository.Setup(r => r.GetCausalConsequenceRepresentativesAsync(It.IsAny<IReadOnlyCollection<Guid>>()))
            .ReturnsAsync([]);
        _application = new JimApplication(_repository.Object);
        _controller = new ActivitiesController(new Mock<ILogger<ActivitiesController>>().Object, _application,
            new Mock<IActivityEtaTracker>().Object);
    }

    [TearDown]
    public void TearDown() => _application?.Dispose();

    // ─── Causes ───

    [Test]
    public async Task GetActivityExecutionItemCausesAsync_UnknownItem_ReturnsNotFoundAsync()
    {
        var result = await _controller.GetActivityExecutionItemCausesAsync(Guid.NewGuid());

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task GetActivityExecutionItemCausesAsync_ItemWithNoCauses_ReturnsAnEmptyChainAsync()
    {
        var result = await _controller.GetActivityExecutionItemCausesAsync(_itemId);

        var chain = (CausalChainDto)((OkObjectResult)result).Value!;
        Assert.That(chain.RunProfileExecutionItemId, Is.EqualTo(_itemId));
        Assert.That(chain.Cohorts, Is.Empty);
    }

    // ─── Consequences ───

    [Test]
    public async Task GetActivityExecutionItemConsequencesAsync_UnknownItem_ReturnsNotFoundAsync()
    {
        var result = await _controller.GetActivityExecutionItemConsequencesAsync(Guid.NewGuid());

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task GetActivityExecutionItemConsequencesAsync_KnownItem_ReturnsTheWalkWithItsCohortKeysAsync()
    {
        SetUpOneRemovalCohort();

        var result = await _controller.GetActivityExecutionItemConsequencesAsync(_itemId);

        var consequences = (CausalConsequencesDto)((OkObjectResult)result).Value!;
        var cohort = consequences.Cohorts.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(consequences.RunProfileExecutionItemId, Is.EqualTo(_itemId));
            Assert.That(cohort.HopKind, Is.EqualTo(CausalConsequenceHopKind.Edge));
            Assert.That(cohort.AttributeName, Is.EqualTo("Static Members"));
            Assert.That(cohort.MemberCount, Is.EqualTo(3));
            Assert.That(cohort.CohortKey, Is.Not.Empty);
            Assert.That(cohort.Endings.Single().Ending, Is.EqualTo(CausalConsequenceEnding.NoFurtherConsequences));
        }
    }

    // ─── Cohort members ───

    [Test]
    public async Task GetActivityExecutionItemConsequenceCohortMembersAsync_CohortKeyFromTheWalk_ReturnsAPaginatedPageAsync()
    {
        SetUpOneRemovalCohort();
        var walk = (CausalConsequencesDto)((OkObjectResult)await _controller.GetActivityExecutionItemConsequencesAsync(_itemId)).Value!;
        _activityRepository
            .Setup(r => r.GetCausalConsequenceCohortMembersAsync(It.IsAny<IReadOnlyList<Guid>>(), RemovalTuple, 2, 2, "project"))
            .ReturnsAsync(([new CausalConsequenceMember { RunProfileExecutionItemId = Guid.NewGuid(), DisplayName = "Project Pulse" }], 3));

        var result = await _controller.GetActivityExecutionItemConsequenceCohortMembersAsync(
            _itemId, walk.Cohorts.Single().CohortKey, new PaginationRequest { Page = 2, PageSize = 2 }, "project");

        var page = (PaginatedResponse<CausalConsequenceMemberDto>)((OkObjectResult)result).Value!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(page.TotalCount, Is.EqualTo(3));
            Assert.That(page.Page, Is.EqualTo(2));
            Assert.That(page.PageSize, Is.EqualTo(2));
            Assert.That(page.Items.Single().DisplayName, Is.EqualTo("Project Pulse"));
        }
    }

    [Test]
    public async Task GetActivityExecutionItemConsequenceCohortMembersAsync_KeyThatIsNotOne_ReturnsBadRequestAsync()
    {
        var result = await _controller.GetActivityExecutionItemConsequenceCohortMembersAsync(
            _itemId, "not-a-cohort-key", new PaginationRequest(), search: null);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task GetActivityExecutionItemConsequenceCohortMembersAsync_UnknownItem_ReturnsNotFoundAsync()
    {
        var result = await _controller.GetActivityExecutionItemConsequenceCohortMembersAsync(
            Guid.NewGuid(), "a2V5", new PaginationRequest(), search: null);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    // ─── Routes ───

    [TestCase(nameof(ActivitiesController.GetActivityExecutionItemCausesAsync), "items/{id:guid}/causes", "GetActivityExecutionItemCauses")]
    [TestCase(nameof(ActivitiesController.GetActivityExecutionItemConsequencesAsync), "items/{id:guid}/consequences", "GetActivityExecutionItemConsequences")]
    [TestCase(nameof(ActivitiesController.GetActivityExecutionItemConsequenceCohortMembersAsync),
        "items/{id:guid}/consequences/cohorts/{cohortKey}/members", "GetActivityExecutionItemConsequenceCohortMembers")]
    public void CausalityEndpoint_IsAdministratorOnlyAtItsRoute(string actionName, string template, string routeName)
    {
        var controllerAuthorise = typeof(ActivitiesController).GetCustomAttribute<AuthorizeAttribute>();
        var action = typeof(ActivitiesController).GetMethod(actionName)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(controllerAuthorise?.Roles, Is.EqualTo("Administrator"));
            Assert.That(action.GetCustomAttribute<AllowAnonymousAttribute>(), Is.Null);
            Assert.That(action.GetCustomAttribute<HttpGetAttribute>()?.Template, Is.EqualTo(template));
            Assert.That(action.GetCustomAttribute<HttpGetAttribute>()?.Name, Is.EqualTo(routeName));
        }
    }

    private void SetUpOneRemovalCohort()
    {
        var members = Enumerable.Range(0, 3)
            .Select(i => new CausalConsequenceMember { RunProfileExecutionItemId = Guid.NewGuid(), DisplayName = $"Project {i}" })
            .ToList();
        _activityRepository
            .SetupSequence(r => r.GetCausalConsequenceLevelAsync(It.IsAny<IReadOnlyList<CausalConsequenceFrontierMember>>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(new CausalConsequenceLevel
            {
                Cohorts =
                [
                    new CausalConsequenceCohortRow
                    {
                        Ordinal = 0, ParentCohortOrdinal = 0, Tuple = RemovalTuple, MemberCount = 3, Members = members,
                        ContinuingMemberCount = 3, ContinuingItemIds = members.Select(m => m.RunProfileExecutionItemId!.Value).ToList()
                    }
                ]
            })
            .ReturnsAsync(new CausalConsequenceLevel
            {
                Leaves = [new CausalConsequenceLeafRow { ParentCohortOrdinal = 0, Ending = CausalConsequenceEnding.NoFurtherConsequences, MemberCount = 3 }]
            });
    }
}
