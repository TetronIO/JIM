// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Application.Exceptions;
using JIM.Models.Exchange;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using JIM.TestSupport;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using SyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The REST surface over the Exchange queue (#2021, Phase 6b): what is queued and where it stands, the summary, and the
/// retry, behind the in-development <c>Features.ExchangeRecipients</c> flag.
/// </summary>
[TestFixture]
public class ExchangeControllerQueueTests
{
    private const int PanoplyId = 3;
    private const int ResurgamId = 4;
    private const int UnknownSystemId = 99;
    private static readonly DateTime At = DateTime.UtcNow;

    private SyncRepository _syncRepo = null!;
    private JimApplication _application = null!;
    private ExchangeController _controller = null!;
    private List<Activity> _createdActivities = null!;
    private Mock<IRepository> _repository = null!;
    private Guid _apiKeyId;

    [SetUp]
    public void SetUp()
    {
        _createdActivities = [];
        _repository = new Mock<IRepository>();
        var activityRepo = new Mock<IActivityRepository>();
        var apiKeyRepo = new Mock<IApiKeyRepository>();
        var connectedSystemRepo = new Mock<IConnectedSystemRepository>();

        activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a =>
            {
                if (a.Id == Guid.Empty)
                    a.Id = Guid.NewGuid();
                _createdActivities.Add(a);
            })
            .Returns(Task.CompletedTask);
        activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        connectedSystemRepo
            .Setup(r => r.GetConnectedSystemCoreAsync(It.IsAny<int>(), It.IsAny<bool>()))
            .ReturnsAsync((int id, bool _) => id == UnknownSystemId ? null : new ConnectedSystem { Id = id, Name = id == PanoplyId ? "Panoply AD" : "Resurgam AD" });

        _repository.Setup(r => r.Metaverse).Returns(new Mock<IMetaverseRepository>().Object);
        _repository.Setup(r => r.Activity).Returns(activityRepo.Object);
        _repository.Setup(r => r.ApiKeys).Returns(apiKeyRepo.Object);
        _repository.Setup(r => r.Tasking).Returns(new Mock<ITaskingRepository>().Object);
        _repository.Setup(r => r.ConnectedSystems).Returns(connectedSystemRepo.Object);
        _repository.Setup(r => r.ServiceSettings).Returns(InMemoryServiceSettingsRepository.WithAllFeatureFlagsEnabled());

        _syncRepo = new SyncRepository();
        _syncRepo.SeedConnectedSystem(new ConnectedSystem { Id = PanoplyId, Name = "Panoply AD", ManagesExchangeRecipients = true });
        _syncRepo.SeedConnectedSystem(new ConnectedSystem { Id = ResurgamId, Name = "Resurgam AD", ManagesExchangeRecipients = true });

        _application = new JimApplication(_repository.Object, syncRepository: _syncRepo);
        _controller = NewController(_application);

        _apiKeyId = Guid.NewGuid();
        apiKeyRepo.Setup(r => r.GetByIdAsync(_apiKeyId)).ReturnsAsync(new JIM.Models.Security.ApiKey
        {
            Id = _apiKeyId, Name = "TestApiKey", KeyHash = "test-hash", KeyPrefix = "test", IsEnabled = true, Created = DateTime.UtcNow
        });
        var identity = new ClaimsIdentity(
        [
            new Claim("auth_method", "api_key"),
            new Claim(ClaimTypes.NameIdentifier, _apiKeyId.ToString()),
            new Claim(ClaimTypes.Name, "TestApiKey")
        ], "ApiKey");
        _controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) } };
    }

    [TearDown]
    public void TearDown() => _application?.Dispose();

    // ---- The flag ---------------------------------------------------------------------------------------------------

    [Test]
    public void EveryEndpoint_FlagOff_IsRefusedNamingTheFeature()
    {
        _repository.Setup(r => r.ServiceSettings).Returns(new InMemoryServiceSettingsRepository());
        using var application = new JimApplication(_repository.Object, syncRepository: _syncRepo);
        var controller = NewController(application);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(async () => await controller.GetPendingExchangeChangesAsync(new PaginationRequest()),
                Throws.InstanceOf<FeatureDisabledException>().With.Message.Contain("Exchange"));
            Assert.That(async () => await controller.GetExchangeQueueSummaryAsync(), Throws.InstanceOf<FeatureDisabledException>());
            Assert.That(async () => await controller.RetryPendingExchangeChangesAsync(new ExchangeQueueActionRequest { ApplyToAllChanges = true }),
                Throws.InstanceOf<FeatureDisabledException>());
        }
    }

    // ---- The list ---------------------------------------------------------------------------------------------------

    [Test]
    public async Task GetPendingExchangeChangesAsync_ReturnsEachChangeNamedWithWhereItStandsAsync()
    {
        var parked = Seed(ResurgamId, "Alan Turing", Parked);
        var queued = Seed(PanoplyId, "Ada Lovelace", _ => { });

        var result = await _controller.GetPendingExchangeChangesAsync(new PaginationRequest { SortBy = "identity" }) as OkObjectResult;

        var page = (PaginatedResponse<PendingExchangeChangeResponse>)result!.Value!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(page.TotalCount, Is.EqualTo(2));
            Assert.That(page.Items.Select(i => i.Id), Is.EqualTo(new[] { queued.Id, parked.Id }), "Ada before Alan");
            var row = page.Items.Last();
            Assert.That(row.ConnectedSystemName, Is.EqualTo("Resurgam AD"));
            Assert.That(row.MetaverseObjectDisplayName, Is.EqualTo("Alan Turing"));
            Assert.That(row.State, Is.EqualTo(ExchangeChangeState.Parked));
            Assert.That(row.RecipientType, Is.EqualTo(ExchangeRecipientTypes.Mailbox));
            Assert.That(row.FailureKind, Is.EqualTo(ExchangeChangeFailureKind.InsufficientPermissions));
            Assert.That(row.CanRetry, Is.True);
            Assert.That(row.Due, Is.False, "parked: no attempt is coming until somebody retries it");
            Assert.That(page.Items.First().Due, Is.True, "queued: the lane will pick it up at once");
            Assert.That(page.Items.First().CanRetry, Is.False);
        }
    }

    [Test]
    public async Task GetPendingExchangeChangesAsync_StateAndSearch_NarrowTheListAsync()
    {
        var parked = Seed(ResurgamId, "Alan Turing", Parked);
        Seed(ResurgamId, "Grace Hopper", _ => { });
        Seed(PanoplyId, "Ada Lovelace", Parked);

        var result = await _controller.GetPendingExchangeChangesAsync(new PaginationRequest(), state: ExchangeChangeState.Parked, search: "resurgam") as OkObjectResult;

        Assert.That(((PaginatedResponse<PendingExchangeChangeResponse>)result!.Value!).Items.Select(i => i.Id), Is.EqualTo(new[] { parked.Id }));
    }

    [Test]
    public async Task GetPendingExchangeChangesAsync_UnknownSortColumn_IsABadRequestAsync()
    {
        var result = await _controller.GetPendingExchangeChangesAsync(new PaginationRequest { SortBy = "colour" });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task GetPendingExchangeChangesAsync_UnknownConnectedSystem_IsNotFoundAsync()
    {
        var result = await _controller.GetPendingExchangeChangesAsync(new PaginationRequest(), connectedSystemId: UnknownSystemId);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task GetExchangeQueueSummaryAsync_CountsTheQueueAsync()
    {
        Seed(ResurgamId, "Alan Turing", Parked);
        Seed(PanoplyId, "Ada Lovelace", _ => { });

        var summary = (ExchangeQueueSummary)((OkObjectResult)await _controller.GetExchangeQueueSummaryAsync()).Value!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary.ParkedCount, Is.EqualTo(1));
            Assert.That(summary.QueuedCount, Is.EqualTo(1));
            Assert.That(summary.WaitingOnAPersonCount, Is.EqualTo(1));
        }
    }

    // ---- The retry --------------------------------------------------------------------------------------------------

    [Test]
    public async Task RetryPendingExchangeChangesAsync_SearchText_RetriesOnlyTheChangesShownAndRecordsTheCallerAsync()
    {
        var shown = Seed(ResurgamId, "Alan Turing", Parked);
        var hidden = Seed(PanoplyId, "Ada Lovelace", Parked);

        var result = await _controller.RetryPendingExchangeChangesAsync(new ExchangeQueueActionRequest { SearchText = "resurgam" }) as OkObjectResult;

        var action = _createdActivities.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(((ExchangeQueueActionResponse)result!.Value!).AffectedCount, Is.EqualTo(1));
            Assert.That(_syncRepo.PendingExchangeChanges[shown.Id].Status, Is.EqualTo(PendingExchangeChangeStatus.Pending));
            Assert.That(_syncRepo.PendingExchangeChanges[hidden.Id].Status, Is.EqualTo(PendingExchangeChangeStatus.Parked));
            Assert.That(action.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.RetryExchangeRecipient));
            Assert.That(action.InitiatedByType, Is.EqualTo(ActivityInitiatorType.ApiKey));
            Assert.That(action.InitiatedById, Is.EqualTo(_apiKeyId));
            Assert.That(_syncRepo.Activities.Values.Single(a => a.ParentActivityId == shown.ActivityId).InitiatedById, Is.EqualTo(_apiKeyId));
        }
    }

    [Test]
    public async Task RetryPendingExchangeChangesAsync_UnknownConnectedSystem_IsNotFoundAndRetriesNothingAsync()
    {
        var parked = Seed(ResurgamId, "Alan Turing", Parked);

        var result = await _controller.RetryPendingExchangeChangesAsync(new ExchangeQueueActionRequest { ConnectedSystemId = UnknownSystemId });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
            Assert.That(_syncRepo.PendingExchangeChanges[parked.Id].Status, Is.EqualTo(PendingExchangeChangeStatus.Parked));
            Assert.That(_createdActivities, Is.Empty);
        }
    }

    [Test]
    public void Validate_RequestNamingNothing_MustSayItMeansTheWholeQueue()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Validate(new ExchangeQueueActionRequest()).Select(e => e.MemberNames.Single()),
                Is.EqualTo(new[] { nameof(ExchangeQueueActionRequest.ApplyToAllChanges) }));
            Assert.That(Validate(new ExchangeQueueActionRequest { ApplyToAllChanges = true }), Is.Empty);
            Assert.That(Validate(new ExchangeQueueActionRequest { SearchText = "panoply" }), Is.Empty, "search text is a criterion");
            Assert.That(Validate(new ExchangeQueueActionRequest { State = ExchangeChangeState.Parked }), Is.Empty);
        }
    }

    [Test]
    public void Validate_MoreIdsThanOneRequestMayName_IsRefused()
    {
        var request = new ExchangeQueueActionRequest { Ids = Enumerable.Range(0, ExchangeQueueActionRequest.MaximumIds + 1).Select(_ => Guid.NewGuid()).ToList() };

        Assert.That(Validate(request).Select(e => e.MemberNames.Single()), Is.EqualTo(new[] { nameof(ExchangeQueueActionRequest.Ids) }));
    }

    [Test]
    public void ToFilter_CarriesEveryCriterion()
    {
        var csoId = Guid.NewGuid();
        var mvoId = Guid.NewGuid();
        var id = Guid.NewGuid();
        var filter = new ExchangeQueueActionRequest
        {
            ConnectedSystemId = PanoplyId, State = ExchangeChangeState.Retrying, FailureKind = ExchangeChangeFailureKind.Unreachable,
            MetaverseObjectId = mvoId, ConnectedSystemObjectId = csoId, Ids = [id], SearchText = "ada"
        }.ToFilter();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(filter.ConnectedSystemId, Is.EqualTo(PanoplyId));
            Assert.That(filter.State, Is.EqualTo(ExchangeChangeState.Retrying));
            Assert.That(filter.FailureKind, Is.EqualTo(ExchangeChangeFailureKind.Unreachable));
            Assert.That(filter.MetaverseObjectId, Is.EqualTo(mvoId));
            Assert.That(filter.ConnectedSystemObjectId, Is.EqualTo(csoId));
            Assert.That(filter.Ids, Is.EqualTo(new[] { id }));
            Assert.That(filter.SearchText, Is.EqualTo("ada"));
        }
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------------

    private static ExchangeController NewController(JimApplication application) =>
        new(new Mock<ILogger<ExchangeController>>().Object, application);

    private static List<ValidationResult> Validate(ExchangeQueueActionRequest request) =>
        request.Validate(new ValidationContext(request)).ToList();

    private static void Parked(PendingExchangeChange change)
    {
        change.Status = PendingExchangeChangeStatus.Parked;
        change.AttemptCount = 1;
        change.LastAttemptedAt = At.AddMinutes(-30);
        change.FirstFailedAt = At.AddMinutes(-30);
        change.FailureKind = ExchangeChangeFailureKind.InsufficientPermissions;
        change.FailureMessage = "Access denied.";
    }

    private PendingExchangeChange Seed(int connectedSystemId, string identityName, Action<PendingExchangeChange> leftAs)
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), CachedDisplayName = identityName, Type = new MetaverseObjectType { Name = "User", PluralName = "Users" } };
        _syncRepo.SeedMetaverseObject(mvo);
        var activity = new Activity
        {
            Id = Guid.NewGuid(), TargetType = ActivityTargetType.ExchangeRecipient, TargetOperationType = ActivityTargetOperationType.SetExchangeRecipient,
            Status = ActivityStatus.Complete, Created = At.AddHours(-1)
        };
        _syncRepo.SeedActivity(activity);
        var change = new PendingExchangeChange
        {
            ConnectedSystemId = connectedSystemId, ConnectedSystemObjectId = Guid.NewGuid(), MetaverseObjectId = mvo.Id,
            RecipientType = ExchangeRecipientTypes.Mailbox, CreatedAt = At.AddHours(-1), ActivityId = activity.Id
        };
        _syncRepo.StageExchangeChangesAsync([change]).GetAwaiter().GetResult();
        var stored = _syncRepo.PendingExchangeChanges[change.Id];
        leftAs(stored);
        return stored;
    }
}
