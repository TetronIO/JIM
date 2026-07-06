// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Application.Services;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Core;
using JIM.Models.Search;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for the Predefined Search configuration change-history REST endpoints on
/// <see cref="PredefinedSearchesController"/>: the paged history list, the single-version detail, and the compare
/// endpoint, including the not-found cases. Predefined Searches are int-keyed, exercising the int-keyed read path
/// at the controller layer (mirroring <see cref="RoleChangeHistoryApiTests"/>). Also covers that a mutation's
/// optional ChangeReason (supplied in the request body for the search update and criteria group/criterion
/// create/update, and as a query parameter for criteria group/criterion delete) reaches the recorded audit
/// Activity, per <see cref="ApiKeyChangeHistoryApiTests"/>.
/// </summary>
[TestFixture]
public class PredefinedSearchChangeHistoryApiTests
{
    private const int SearchId = 4;
    private const int GroupId = 10;

    private Mock<IRepository> _repo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<ISearchRepository> _searchRepo = null!;
    private JimApplication _application = null!;
    private PredefinedSearchesController _controller = null!;
    private static readonly byte[] HashKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly DateTime When = new(2026, 7, 5, 12, 0, 0, DateTimeKind.Utc);

    [SetUp]
    public void SetUp()
    {
        _repo = new Mock<IRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _searchRepo = new Mock<ISearchRepository>();
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _repo.Setup(r => r.Search).Returns(_searchRepo.Object);
        _application = new JimApplication(_repo.Object);
        _controller = new PredefinedSearchesController(new Mock<ILogger<PredefinedSearchesController>>().Object, _application)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    [TearDown]
    public void TearDown() => _application?.Dispose();

    [Test]
    public async Task GetPredefinedSearchChangeHistoryAsync_ReturnsPaginatedItemsAsync()
    {
        var rows = new List<ConfigurationChangeActivityData>
        {
            Data(2, ActivityTargetOperationType.Update, PredefinedSearchSnapJson(isEnabled: false)),
            Data(1, ActivityTargetOperationType.Create, PredefinedSearchSnapJson(isEnabled: true))
        };
        _activityRepo.Setup(r => r.GetConfigurationChangeCountAsync(ActivityTargetType.PredefinedSearch, SearchId)).ReturnsAsync(2);
        _activityRepo.Setup(r => r.GetConfigurationChangeActivitiesAsync(ActivityTargetType.PredefinedSearch, SearchId, 0, It.IsAny<int>())).ReturnsAsync(rows);

        var payload = await OkPayload<PaginatedResponse<ConfigurationChangeHistoryItem>>(
            _controller.GetPredefinedSearchChangeHistoryAsync(SearchId, new PaginationRequest()));

        Assert.That(payload.TotalCount, Is.EqualTo(2));
        Assert.That(payload.Items.Count(), Is.EqualTo(2));
        Assert.That(payload.Items.First().Summary, Is.EqualTo("Enabled"), "v2 changed the enabled flag versus v1");
        Assert.That(payload.Items.Last().Summary, Is.EqualTo("Created"), "v1 is the creation");
    }

    [Test]
    public async Task GetPredefinedSearchChangeHistoryAsync_ReturnsEmptyWhenNoHistoryAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeCountAsync(ActivityTargetType.PredefinedSearch, SearchId)).ReturnsAsync(0);
        _activityRepo.Setup(r => r.GetConfigurationChangeActivitiesAsync(ActivityTargetType.PredefinedSearch, SearchId, 0, It.IsAny<int>()))
            .ReturnsAsync(new List<ConfigurationChangeActivityData>());

        var payload = await OkPayload<PaginatedResponse<ConfigurationChangeHistoryItem>>(
            _controller.GetPredefinedSearchChangeHistoryAsync(SearchId, new PaginationRequest()));

        Assert.That(payload.TotalCount, Is.EqualTo(0));
        Assert.That(payload.Items, Is.Empty);
    }

    [Test]
    public async Task GetPredefinedSearchChangeAsync_ReturnsDetailWithDiffAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(ActivityTargetType.PredefinedSearch, SearchId, 2))
            .ReturnsAsync(Data(2, ActivityTargetOperationType.Update, PredefinedSearchSnapJson(isEnabled: false)));
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityBeforeVersionAsync(ActivityTargetType.PredefinedSearch, SearchId, 2))
            .ReturnsAsync(Data(1, ActivityTargetOperationType.Create, PredefinedSearchSnapJson(isEnabled: true)));

        var detail = await OkPayload<ConfigurationChangeDetail>(_controller.GetPredefinedSearchChangeAsync(SearchId, 2));

        Assert.That(detail.Version, Is.EqualTo(2));
        Assert.That(detail.Snapshot.ObjectId, Is.EqualTo(SearchId));
        Assert.That(detail.Snapshot.ObjectName, Is.EqualTo("Active Employees"));
        Assert.That(detail.Diff.ModifiedCount, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public async Task GetPredefinedSearchChangeAsync_ReturnsNotFoundWhenVersionAbsentAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(It.IsAny<ActivityTargetType>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((ConfigurationChangeActivityData?)null);

        var result = await _controller.GetPredefinedSearchChangeAsync(SearchId, 99);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task ComparePredefinedSearchChangesAsync_ReturnsDiffAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(ActivityTargetType.PredefinedSearch, SearchId, 1))
            .ReturnsAsync(Data(1, ActivityTargetOperationType.Create, PredefinedSearchSnapJson(isEnabled: true)));
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(ActivityTargetType.PredefinedSearch, SearchId, 3))
            .ReturnsAsync(Data(3, ActivityTargetOperationType.Update, PredefinedSearchSnapJson(isEnabled: false)));

        var diff = await OkPayload<ConfigurationDiff>(_controller.ComparePredefinedSearchChangesAsync(SearchId, 1, 3));

        Assert.That(diff.OldVersion, Is.EqualTo(1));
        Assert.That(diff.NewVersion, Is.EqualTo(3));
        Assert.That(diff.ModifiedCount, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public async Task ComparePredefinedSearchChangesAsync_ReturnsNotFoundWhenVersionAbsentAsync()
    {
        _activityRepo.Setup(r => r.GetConfigurationChangeActivityByVersionAsync(It.IsAny<ActivityTargetType>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((ConfigurationChangeActivityData?)null);

        var result = await _controller.ComparePredefinedSearchChangesAsync(SearchId, 1, 3);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task UpdateAsync_WithChangeReason_RecordsReasonOnAuditActivityAsync()
    {
        var existing = NewSearch(isEnabled: true);
        _searchRepo.Setup(r => r.GetPredefinedSearchCoreAsync(SearchId)).ReturnsAsync(existing);
        _searchRepo.Setup(r => r.UpdatePredefinedSearchAsync(It.IsAny<PredefinedSearch>())).Returns(Task.CompletedTask);

        Activity? capturedActivity = null;
        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => capturedActivity = a)
            .Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Returns(Task.CompletedTask);

        var request = new UpdatePredefinedSearchRequest
        {
            IsEnabled = false,
            ChangeReason = "Retiring in favour of new search (CHG0128)"
        };

        var result = await _controller.UpdateAsync(SearchId, request);

        Assert.That(result, Is.InstanceOf<NoContentResult>());
        Assert.That(capturedActivity, Is.Not.Null);
        Assert.That(capturedActivity!.ChangeReason, Is.EqualTo("Retiring in favour of new search (CHG0128)"));
        Assert.That(capturedActivity.TargetType, Is.EqualTo(ActivityTargetType.PredefinedSearch));
        Assert.That(capturedActivity.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Update));
    }

    [Test]
    public async Task DeleteCriteriaGroupAsync_WithChangeReasonQueryParameter_RecordsReasonOnAuditActivityAsync()
    {
        var search = NewSearch(isEnabled: true);
        search.CriteriaGroups.Add(new PredefinedSearchCriteriaGroup { Id = GroupId, Type = SearchGroupType.All, Position = 0 });

        _searchRepo.Setup(r => r.GetPredefinedSearchAsync(SearchId)).ReturnsAsync(search);
        _searchRepo.Setup(r => r.GetPredefinedSearchCoreAsync(SearchId)).ReturnsAsync(search);
        _searchRepo.Setup(r => r.GetOwningPredefinedSearchIdForGroupAsync(GroupId)).ReturnsAsync(SearchId);
        _searchRepo.Setup(r => r.DeletePredefinedSearchCriteriaGroupAsync(GroupId)).ReturnsAsync(true);

        Activity? capturedActivity = null;
        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => capturedActivity = a)
            .Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Returns(Task.CompletedTask);

        var result = await _controller.DeleteCriteriaGroupAsync(SearchId, GroupId, "Consolidating filters (CHG0129)");

        Assert.That(result, Is.InstanceOf<NoContentResult>());
        Assert.That(capturedActivity, Is.Not.Null);
        Assert.That(capturedActivity!.ChangeReason, Is.EqualTo("Consolidating filters (CHG0129)"));
        Assert.That(capturedActivity.TargetType, Is.EqualTo(ActivityTargetType.PredefinedSearch));
        Assert.That(capturedActivity.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Update));
    }

    // -- helpers -------------------------------------------------------------------------------------------------------

    private static async Task<T> OkPayload<T>(Task<IActionResult> action) where T : class
    {
        var result = await action;
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        var payload = ((OkObjectResult)result).Value as T;
        Assert.That(payload, Is.Not.Null);
        return payload!;
    }

    private static PredefinedSearch NewSearch(bool isEnabled) => new()
    {
        Id = SearchId,
        Name = "Active Employees",
        Uri = "active-employees",
        IsEnabled = isEnabled,
        MetaverseObjectType = new MetaverseObjectType { Id = 1, Name = "Person", PluralName = "People" }
    };

    private string PredefinedSearchSnapJson(bool isEnabled) =>
        ConfigurationSnapshotService.Serialise(_application.ConfigurationSnapshots.CreateSnapshot(NewSearch(isEnabled), HashKey));

    private static ConfigurationChangeActivityData Data(int version, ActivityTargetOperationType operation, string snapshotJson) => new()
    {
        ActivityId = Guid.NewGuid(),
        Version = version,
        Operation = operation,
        InitiatedByType = ActivityInitiatorType.User,
        InitiatedByName = "Tester",
        When = When,
        SnapshotJson = snapshotJson
    };
}
