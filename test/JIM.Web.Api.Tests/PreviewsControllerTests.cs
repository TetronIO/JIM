// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Preview;
using JIM.Models.Utility;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Contract tests for the configuration change preview endpoints (#827).
///
/// The read endpoint's job is to be honest about a preview that is not finished, or not trustworthy. A caller
/// polling mid-run must be able to tell a stage that has not started from one that failed, and a summary group's
/// count must reach them exactly even when only a sample of its rows was kept; presenting a sample as the whole
/// group is how a change gets approved as safe on the strength of the rows that happened to fit.
/// </summary>
[TestFixture]
public class PreviewsControllerTests
{
    private Mock<IRepository> _repository = null!;
    private Mock<IActivityRepository> _activityRepository = null!;
    private Mock<IConfigurationChangePreviewRepository> _previewRepository = null!;
    private Mock<ILogger<PreviewsController>> _logger = null!;
    private JimApplication _application = null!;
    private PreviewsController _controller = null!;

    private static readonly Guid ActivityId = Guid.CreateVersion7();

    [SetUp]
    public void SetUp()
    {
        _repository = new Mock<IRepository>();
        _activityRepository = new Mock<IActivityRepository>();
        _previewRepository = new Mock<IConfigurationChangePreviewRepository>();
        _repository.Setup(r => r.Activity).Returns(_activityRepository.Object);
        _repository.Setup(r => r.ConfigurationChangePreviews).Returns(_previewRepository.Object);
        _repository.Setup(r => r.Tasking).Returns(new Mock<ITaskingRepository>().Object);

        // The real repository returns a list, never null; without this the mock's default stands in for a state the
        // type system does not permit and every read test fails for the wrong reason.
        _previewRepository.Setup(r => r.GetPreviewGroupsAsync(It.IsAny<Guid>())).ReturnsAsync([]);
        _logger = new Mock<ILogger<PreviewsController>>();
        _application = new JimApplication(_repository.Object);
        _controller = new PreviewsController(_logger.Object, _application);
    }

    [TearDown]
    public void TearDown() => _application?.Dispose();

    [Test]
    public async Task GetPreviewAsync_CompletePreview_ReturnsStagesFindingsCountsAndGroupsAsync()
    {
        GivenPreview(p =>
        {
            p.ValidationStatus = ConfigurationChangePreviewStageStatus.Complete;
            p.ImpactCountsStatus = ConfigurationChangePreviewStageStatus.Complete;
            p.SummaryStatus = ConfigurationChangePreviewStageStatus.Complete;
            p.DeltasStatus = ConfigurationChangePreviewStageStatus.Complete;
            p.ValidationFindings = """[{"Severity":1,"Message":"No trigger systems are selected.","PropertyName":"DeletionTriggers"}]""";
            p.ImpactCounts = """[{"TransitionType":22,"ObjectCount":4812,"ConnectedSystemId":null,"MetaverseObjectTypeId":11}]""";
            p.DeltaPersistence = ConfigurationChangePreviewDeltaPersistence.Capped;
        });
        GivenGroups(NewGroup(4_812, sampled: true));

        var response = await GetPreviewResponseAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.ActivityId, Is.EqualTo(ActivityId));
            Assert.That(response.IsComplete, Is.True);
            Assert.That(response.HasFailed, Is.False);
            Assert.That(response.ValidationFindings, Has.Count.EqualTo(1));
            Assert.That(response.ValidationFindings[0].Severity, Is.EqualTo(PreviewValidationSeverity.Warning));
            Assert.That(response.ImpactCounts, Has.Count.EqualTo(1));
            Assert.That(response.ImpactCounts[0].ObjectCount, Is.EqualTo(4_812));
            Assert.That(response.Groups, Has.Count.EqualTo(1));
            Assert.That(response.Groups[0].ObjectCount, Is.EqualTo(4_812),
                "A group's count is exact whether or not its rows were capped.");
            Assert.That(response.Groups[0].DeltasSampled, Is.True,
                "An unlabelled sample read as a complete list is the failure this flag exists to prevent.");
            Assert.That(response.DeltaPersistence, Is.EqualTo(ConfigurationChangePreviewDeltaPersistence.Capped));
        }
    }

    [Test]
    public async Task GetPreviewAsync_FailedPreview_SaysSoRatherThanLookingUnfinishedAsync()
    {
        GivenPreview(p =>
        {
            p.ValidationStatus = ConfigurationChangePreviewStageStatus.Complete;
            p.ImpactCountsStatus = ConfigurationChangePreviewStageStatus.Failed;
        }, activity =>
        {
            activity.Status = ActivityStatus.FailedWithError;
            activity.ErrorMessage = "the count query failed";
        });

        var response = await GetPreviewResponseAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.HasFailed, Is.True);
            Assert.That(response.IsComplete, Is.False);
            Assert.That(response.ActivityStatus, Is.EqualTo(ActivityStatus.FailedWithError));
            Assert.That(response.ErrorMessage, Is.EqualTo("the count query failed"));
            Assert.That(response.SummaryStatus, Is.EqualTo(ConfigurationChangePreviewStageStatus.NotStarted),
                "A stage that never ran is not a stage that succeeded.");
        }
    }

    [Test]
    public async Task GetPreviewAsync_MalformedStoredFindings_StillReturnsTheRestOfThePreviewAsync()
    {
        // The stage statuses say whether a stage produced anything; an unreadable document should not cost the
        // caller the counts and groups that are perfectly readable beside it.
        GivenPreview(p => p.ValidationFindings = "not json");
        GivenGroups(NewGroup(3));

        var response = await GetPreviewResponseAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.ValidationFindings, Is.Empty);
            Assert.That(response.Groups, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public async Task GetPreviewAsync_UnknownActivity_ReturnsNotFoundAsync()
    {
        var result = await _controller.GetPreviewAsync(Guid.CreateVersion7());

        Assert.That(result, Is.InstanceOf<NotFoundResult>());
    }

    [Test]
    public async Task GetPreviewDeltasAsync_RestrictedToAGroup_PassesTheGroupThroughAsync()
    {
        GivenPreview();
        var groupId = Guid.CreateVersion7();
        _previewRepository.Setup(r => r.GetPreviewDeltasAsync(ActivityId, groupId, 2, 25, null))
            .ReturnsAsync(new PagedResultSet<ConfigurationChangePreviewDelta>
            {
                Results = [new ConfigurationChangePreviewDelta
                {
                    Id = Guid.CreateVersion7(),
                    ActivityId = ActivityId,
                    GroupId = groupId,
                    TransitionType = ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallOutOfScope,
                    ObjectDisplayName = "Ada Lovelace"
                }],
                TotalResults = 40,
                CurrentPage = 2,
                PageSize = 25
            });

        var result = await _controller.GetPreviewDeltasAsync(ActivityId, new PaginationRequest { Page = 2, PageSize = 25 }, groupId)
            as OkObjectResult;
        var page = result!.Value as PaginatedResponse<ConfigurationChangePreviewDeltaResponse>;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(page!.TotalCount, Is.EqualTo(40));
            Assert.That(page.Page, Is.EqualTo(2));
            Assert.That(page.Items.Single().ObjectDisplayName, Is.EqualTo("Ada Lovelace"));
            Assert.That(page.Items.Single().GroupId, Is.EqualTo(groupId));
        }
    }

    [Test]
    public async Task GetPreviewDeltasAsync_WithASearchTerm_PassesItToTheQueryAsync()
    {
        // Filtering has to happen in the query, not in the caller: a capped group holds up to a thousand rows and a
        // full one holds however many the change touches, so a drill-down that filtered the page it had already
        // fetched would search a sample and report it as the whole answer.
        GivenPreview();
        _previewRepository.Setup(r => r.GetPreviewDeltasAsync(ActivityId, null, 1, 25, "lovelace"))
            .ReturnsAsync(new PagedResultSet<ConfigurationChangePreviewDelta>
            {
                Results = [new ConfigurationChangePreviewDelta { Id = Guid.CreateVersion7(), ObjectDisplayName = "Ada Lovelace" }],
                TotalResults = 1,
                CurrentPage = 1,
                PageSize = 25
            });

        var result = await _controller.GetPreviewDeltasAsync(ActivityId, new PaginationRequest { Page = 1, PageSize = 25 },
            search: "lovelace") as OkObjectResult;
        var page = result!.Value as PaginatedResponse<ConfigurationChangePreviewDeltaResponse>;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(page!.TotalCount, Is.EqualTo(1), "the total must count the matches, not the whole group");
            Assert.That(page.Items.Single().ObjectDisplayName, Is.EqualTo("Ada Lovelace"));
        }
    }

    [Test]
    public async Task GetPreviewDeltasAsync_UnknownActivity_ReturnsNotFoundAsync()
    {
        var result = await _controller.GetPreviewDeltasAsync(Guid.CreateVersion7(), new PaginationRequest());

        Assert.That(result, Is.InstanceOf<NotFoundResult>());
    }

    [Test]
    public async Task CancelPreviewAsync_PreviewNoLongerRunning_ReturnsConflictAsync()
    {
        // Nothing to cancel is not the same as nothing to find: the preview and its results are still readable, and
        // reporting 404 would tell the caller they had lost them.
        GivenPreview();

        var result = await _controller.CancelPreviewAsync(ActivityId);

        Assert.That(result, Is.InstanceOf<ConflictObjectResult>());
    }

    [Test]
    public async Task CancelPreviewAsync_UnknownActivity_ReturnsNotFoundAsync()
    {
        var result = await _controller.CancelPreviewAsync(Guid.CreateVersion7());

        Assert.That(result, Is.InstanceOf<NotFoundResult>());
    }

    #region Helpers

    private async Task<ConfigurationChangePreviewResponse> GetPreviewResponseAsync()
    {
        var result = await _controller.GetPreviewAsync(ActivityId) as OkObjectResult;
        Assert.That(result, Is.Not.Null);
        return (ConfigurationChangePreviewResponse)result!.Value!;
    }

    private void GivenPreview(Action<ConfigurationChangePreview>? configurePreview = null,
        Action<Activity>? configureActivity = null)
    {
        var preview = new ConfigurationChangePreview
        {
            ActivityId = ActivityId,
            Surface = ConfigurationChangePreviewSurface.MetaverseObjectType,
            EstimatedAffectedObjects = 4_812
        };
        configurePreview?.Invoke(preview);

        var activity = new Activity
        {
            Id = ActivityId,
            TargetType = ActivityTargetType.MetaverseObjectType,
            TargetOperationType = ActivityTargetOperationType.Preview,
            TargetName = "User",
            Status = ActivityStatus.Complete
        };
        configureActivity?.Invoke(activity);

        _previewRepository.Setup(r => r.GetPreviewAsync(ActivityId)).ReturnsAsync(preview);
        _activityRepository.Setup(r => r.GetActivityAsync(ActivityId)).ReturnsAsync(activity);
    }

    private void GivenGroups(params ConfigurationChangePreviewGroup[] groups) =>
        _previewRepository.Setup(r => r.GetPreviewGroupsAsync(ActivityId)).ReturnsAsync([.. groups]);

    private static ConfigurationChangePreviewGroup NewGroup(int objectCount, bool sampled = false) => new()
    {
        Id = Guid.CreateVersion7(),
        ActivityId = ActivityId,
        TransitionType = ActivityRunProfileExecutionItemSyncOutcomeType.WouldBecomeDeletionEligible,
        MetaverseObjectTypeId = 11,
        MetaverseObjectTypeName = "User",
        ObjectCount = objectCount,
        DeltasSampled = sampled
    };

    #endregion
}
