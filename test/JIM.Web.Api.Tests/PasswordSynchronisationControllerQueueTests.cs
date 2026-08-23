// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
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
/// The REST surface over the Password Synchronisation queue (#1119, requirement 33): what is waiting, what needs
/// a person, and the retry and cancel actions an administrator runs over it.
/// <para>
/// The load-bearing assertion in this fixture is the one that serialises a response and looks for the password.
/// Everything else here is ordinary list-and-act behaviour; that one is the reason the queue has a Header type
/// with nowhere to put a password rather than returning the entity.
/// </para>
/// </summary>
[TestFixture]
public class PasswordSynchronisationControllerQueueTests
{
    private const int CorporateAdId = 3;
    private const int HrPortalId = 4;
    private const int UnknownSystemId = 99;
    private const string ThePassword = "Correct-Horse-42";

    private SyncRepository _syncRepo = null!;
    private JimApplication _application = null!;
    private PasswordSynchronisationController _controller = null!;
    private List<Activity> _createdActivities = null!;
    private Guid _adaId;
    private Guid _graceId;

    [SetUp]
    public void SetUp()
    {
        _adaId = Guid.NewGuid();
        _graceId = Guid.NewGuid();
        _createdActivities = [];

        var repository = new Mock<IRepository>();
        var metaverseRepo = new Mock<IMetaverseRepository>();
        var activityRepo = new Mock<IActivityRepository>();
        var apiKeyRepo = new Mock<IApiKeyRepository>();
        var taskingRepo = new Mock<ITaskingRepository>();
        var serviceSettingsRepo = new Mock<IServiceSettingsRepository>();
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

        // A delivery pass is already queued, so a retry does not go on to create a worker task; this fixture is
        // about the endpoints, and the fan-out has its own tests.
        taskingRepo.Setup(r => r.HasQueuedPasswordDeliveryTaskAsync(It.IsAny<int?>())).ReturnsAsync(true);

        connectedSystemRepo
            .Setup(r => r.GetConnectedSystemCoreAsync(It.IsAny<int>(), It.IsAny<bool>()))
            .ReturnsAsync((int id, bool _) => id == UnknownSystemId
                ? null
                : new ConnectedSystem { Id = id, Name = id == CorporateAdId ? "Corporate AD" : "HR Portal" });

        repository.Setup(r => r.Metaverse).Returns(metaverseRepo.Object);
        repository.Setup(r => r.Activity).Returns(activityRepo.Object);
        repository.Setup(r => r.ApiKeys).Returns(apiKeyRepo.Object);
        repository.Setup(r => r.Tasking).Returns(taskingRepo.Object);
        repository.Setup(r => r.ConnectedSystems).Returns(connectedSystemRepo.Object);
        repository.Setup(r => r.ServiceSettings).Returns(serviceSettingsRepo.Object);

        _syncRepo = new SyncRepository();
        _syncRepo.SeedConnectedSystem(new ConnectedSystem { Id = CorporateAdId, Name = "Corporate AD" });
        _syncRepo.SeedConnectedSystem(new ConnectedSystem { Id = HrPortalId, Name = "HR Portal" });
        var userType = new MetaverseObjectType { Id = 1, Name = "User", PluralName = "Users" };
        _syncRepo.SeedMetaverseObject(new MetaverseObject { Id = _adaId, CachedDisplayName = "Ada Lovelace", Type = userType });
        _syncRepo.SeedMetaverseObject(new MetaverseObject { Id = _graceId, CachedDisplayName = "Grace Hopper", Type = userType });

        _application = new JimApplication(repository.Object, syncRepository: _syncRepo);
        _controller = new PasswordSynchronisationController(
            new Mock<ILogger<PasswordSynchronisationController>>().Object, _application);

        var apiKeyId = Guid.NewGuid();
        apiKeyRepo.Setup(r => r.GetByIdAsync(apiKeyId)).ReturnsAsync(new JIM.Models.Security.ApiKey
        {
            Id = apiKeyId,
            Name = "TestApiKey",
            KeyHash = "test-hash",
            KeyPrefix = "test",
            IsEnabled = true,
            Created = DateTime.UtcNow
        });

        var identity = new ClaimsIdentity(
        [
            new Claim("auth_method", "api_key"),
            new Claim(ClaimTypes.NameIdentifier, apiKeyId.ToString()),
            new Claim(ClaimTypes.Name, "TestApiKey")
        ], "ApiKey");

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
    }

    [TearDown]
    public void TearDown() => _application?.Dispose();

    /// <summary>
    /// Puts one change on the queue. The encrypted password is a stand-in for a real ciphertext; nothing on
    /// these paths decrypts it, and the point of seeding one at all is to prove it never comes back out.
    /// </summary>
    private async Task<Guid> SeedChangeAsync(
        Guid metaverseObjectId,
        int connectedSystemId,
        PendingPasswordChangeStatus status = PendingPasswordChangeStatus.Pending,
        PasswordSetFailureReason? failureReason = null,
        DateTime? nextRetryAt = null)
    {
        var change = new PendingPasswordChange
        {
            Id = Guid.NewGuid(),
            MetaverseObjectId = metaverseObjectId,
            ConnectedSystemId = connectedSystemId,
            EncryptedPassword = ThePassword,
            Status = status,
            FailureReason = failureReason,
            TargetMessage = failureReason == null ? null : "The directory refused it.",
            AttemptCount = failureReason == null ? 0 : 3,
            NextRetryAt = nextRetryAt,
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            ActivityId = Guid.NewGuid()
        };

        await _syncRepo.QueuePasswordChangesAsync([change]);
        return change.Id;
    }

    private async Task<PaginatedResponse<PendingPasswordChangeResponse>> ListAsync(
        PaginationRequest? pagination = null,
        int? connectedSystemId = null,
        PendingPasswordChangeStatus? status = null,
        string? search = null)
    {
        var result = await _controller.GetPendingPasswordChangesAsync(
            pagination ?? new PaginationRequest(), connectedSystemId, status, null, null, search);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        return (PaginatedResponse<PendingPasswordChangeResponse>)((OkObjectResult)result).Value!;
    }

    #region Reading the queue

    [Test]
    public async Task Queue_NamesTheIdentityAndTheSystemAsync()
    {
        await SeedChangeAsync(_adaId, CorporateAdId);

        var body = await ListAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(body.TotalCount, Is.EqualTo(1));
            var row = body.Items.Single();
            Assert.That(row.MetaverseObjectDisplayName, Is.EqualTo("Ada Lovelace"),
                "A queue an administrator reads must name a person, not a Guid.");
            Assert.That(row.ConnectedSystemName, Is.EqualTo("Corporate AD"));
        }
    }

    [Test]
    public async Task Queue_CarriesWhatALinkToTheIdentityNeedsAsync()
    {
        // The identity's page is addressed by its Object Type's plural name, so without this a list would need a
        // read per row to link a row, or would not link at all.
        await SeedChangeAsync(_adaId, CorporateAdId);

        var body = await ListAsync();

        Assert.That(body.Items.Single().MetaverseObjectTypePluralName, Is.EqualTo("Users"));
    }

    [Test]
    public async Task Queue_NeverReturnsThePasswordAsync()
    {
        await SeedChangeAsync(_adaId, CorporateAdId);

        var body = await ListAsync();

        Assert.That(JsonSerializer.Serialize(body), Does.Not.Contain(ThePassword),
            "The queued password must have no representation on any surface, in any form.");
    }

    [Test]
    public async Task Queue_SeparatesAChangeWaitingOutABackoffFromOneThatIsDueAsync()
    {
        // Both are Pending, so status alone cannot tell an administrator which of these the next delivery pass
        // will pick up. That is the whole reason the row carries a due flag.
        await SeedChangeAsync(_adaId, CorporateAdId);
        await SeedChangeAsync(_graceId, CorporateAdId, nextRetryAt: DateTime.UtcNow.AddHours(1));

        var body = await ListAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(body.Items.Single(i => i.MetaverseObjectId == _adaId).Due, Is.True);
            Assert.That(body.Items.Single(i => i.MetaverseObjectId == _graceId).Due, Is.False);
        }
    }

    [Test]
    public async Task Queue_FiltersByStatusAsync()
    {
        await SeedChangeAsync(_adaId, CorporateAdId);
        await SeedChangeAsync(_graceId, CorporateAdId, PendingPasswordChangeStatus.Parked,
            PasswordSetFailureReason.PolicyRejection);

        var body = await ListAsync(status: PendingPasswordChangeStatus.Parked);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(body.Items.Single().MetaverseObjectId, Is.EqualTo(_graceId));
            Assert.That(body.Items.Single().TargetMessage, Is.EqualTo("The directory refused it."),
                "The target's own words are what tell an administrator where the remedy lives.");
        }
    }

    [Test]
    public async Task Queue_FiltersByConnectedSystemAsync()
    {
        await SeedChangeAsync(_adaId, CorporateAdId);
        await SeedChangeAsync(_adaId, HrPortalId);

        var body = await ListAsync(connectedSystemId: HrPortalId);

        Assert.That(body.Items.Single().ConnectedSystemName, Is.EqualTo("HR Portal"));
    }

    [Test]
    public async Task Queue_UnknownSortColumn_IsRefusedRatherThanIgnoredAsync()
    {
        // Serving the default order for an unrecognised column would have a caller believe they sorted when they
        // did not, which is worse than a 400 naming the columns that exist.
        var result = await _controller.GetPendingPasswordChangesAsync(
            new PaginationRequest { SortBy = "password" }, null, null, null, null, null);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        var error = (ApiErrorResponse)((BadRequestObjectResult)result).Value!;
        Assert.That(error.Message, Does.Contain("nextattempt"), "The error should name the columns that do exist.");
    }

    [Test]
    public async Task Queue_UnknownConnectedSystem_IsNotFoundAsync()
    {
        var result = await _controller.GetPendingPasswordChangesAsync(
            new PaginationRequest(), UnknownSystemId, null, null, null, null);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task Summary_CountsEachStateAsync()
    {
        await SeedChangeAsync(_adaId, CorporateAdId);
        await SeedChangeAsync(_graceId, CorporateAdId, PendingPasswordChangeStatus.Parked,
            PasswordSetFailureReason.PolicyRejection);
        await SeedChangeAsync(_graceId, HrPortalId, PendingPasswordChangeStatus.Expired);

        var result = await _controller.GetPasswordQueueSummaryAsync();
        var summary = (PasswordQueueSummary)((OkObjectResult)result).Value!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary.WaitingCount, Is.EqualTo(1));
            Assert.That(summary.DueCount, Is.EqualTo(1));
            Assert.That(summary.ParkedCount, Is.EqualTo(1));
            Assert.That(summary.ExpiredCount, Is.EqualTo(1));
        }
    }

    #endregion

    #region Retry and cancel

    private async Task<PasswordQueueActionResponse> RetryAsync(PasswordQueueActionRequest request)
    {
        var result = await _controller.RetryPendingPasswordChangesAsync(request);
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        return (PasswordQueueActionResponse)((OkObjectResult)result).Value!;
    }

    private async Task<PasswordQueueActionResponse> CancelAsync(PasswordQueueActionRequest request)
    {
        var result = await _controller.CancelPendingPasswordChangesAsync(request);
        Assert.That(result, Is.InstanceOf<OkObjectResult>());
        return (PasswordQueueActionResponse)((OkObjectResult)result).Value!;
    }

    [Test]
    public async Task Retry_MakesParkedChangesDueAgainAsync()
    {
        var parked = await SeedChangeAsync(_adaId, CorporateAdId, PendingPasswordChangeStatus.Parked,
            PasswordSetFailureReason.Transient);

        var body = await RetryAsync(new PasswordQueueActionRequest { ConnectedSystemId = CorporateAdId });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(body.AffectedCount, Is.EqualTo(1));
            var change = _syncRepo.PendingPasswordChanges[parked];
            Assert.That(change.Status, Is.EqualTo(PendingPasswordChangeStatus.Pending));
            Assert.That(change.FailureReason, Is.Null,
                "A retried change carries no failure from the attempt that has just been abandoned.");
        }
    }

    [Test]
    public async Task Retry_LeavesAnExpiredChangeAloneAsync()
    {
        // There is no password left to send, so retrying one would queue an empty delivery.
        var expired = await SeedChangeAsync(_adaId, CorporateAdId, PendingPasswordChangeStatus.Expired);

        var body = await RetryAsync(new PasswordQueueActionRequest { ApplyToAllChanges = true });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(body.AffectedCount, Is.Zero);
            Assert.That(_syncRepo.PendingPasswordChanges[expired].Status,
                Is.EqualTo(PendingPasswordChangeStatus.Expired));
        }
    }

    [Test]
    public async Task Cancel_RecordsTheOutcomeRatherThanRemovingTheRowAsync()
    {
        var pending = await SeedChangeAsync(_adaId, CorporateAdId);

        var body = await CancelAsync(new PasswordQueueActionRequest { Ids = [pending] });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(body.AffectedCount, Is.EqualTo(1));
            Assert.That(_syncRepo.PendingPasswordChanges, Does.ContainKey(pending),
                "The identity's password is still divergent on that system; the cancelled row is what says so.");
            var change = _syncRepo.PendingPasswordChanges[pending];
            Assert.That(change.Status, Is.EqualTo(PendingPasswordChangeStatus.Cancelled));
            Assert.That(change.CancelledAt, Is.Not.Null);
        }
    }

    [Test]
    public async Task Cancel_ThenRetry_PutsTheChangeBackOnTheQueueAsync()
    {
        var pending = await SeedChangeAsync(_adaId, CorporateAdId);
        await CancelAsync(new PasswordQueueActionRequest { Ids = [pending] });

        var body = await RetryAsync(new PasswordQueueActionRequest { Ids = [pending] });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(body.AffectedCount, Is.EqualTo(1));
            var change = _syncRepo.PendingPasswordChanges[pending];
            Assert.That(change.Status, Is.EqualTo(PendingPasswordChangeStatus.Pending));
            Assert.That(change.CancelledAt, Is.Null, "A change back on the queue is not a cancelled one.");
        }
    }

    [Test]
    public async Task Cancel_MatchedNothing_StillRecordsAnActivityAsync()
    {
        // An administrator who cancelled a system and changed nothing needs to be able to find that out. An
        // Activity that only appears when work happened cannot tell them "nothing was owed" from "it never ran".
        var body = await CancelAsync(new PasswordQueueActionRequest { ConnectedSystemId = CorporateAdId });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(body.AffectedCount, Is.Zero);
            Assert.That(_createdActivities, Has.Exactly(1).Items);
            Assert.That(_createdActivities[0].TargetOperationType,
                Is.EqualTo(ActivityTargetOperationType.CancelPasswordDelivery),
                "'Update' on a Password Synchronisation target tells an administrator nothing.");
        }
    }

    [Test]
    public async Task Retry_OneActivityForTheAction_NotOnePerRowAsync()
    {
        await SeedChangeAsync(_adaId, CorporateAdId, PendingPasswordChangeStatus.Parked,
            PasswordSetFailureReason.Transient);
        await SeedChangeAsync(_graceId, CorporateAdId, PendingPasswordChangeStatus.Parked,
            PasswordSetFailureReason.Transient);

        var body = await RetryAsync(new PasswordQueueActionRequest { ConnectedSystemId = CorporateAdId });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(body.AffectedCount, Is.EqualTo(2));
            Assert.That(_createdActivities, Has.Exactly(1).Items,
                "A retry over a directory that has come back is one decision; an Activity per row would bury it.");
        }
    }

    [Test]
    public async Task Retry_UnknownConnectedSystem_IsNotFoundAsync()
    {
        // Otherwise a typo in the identifier is indistinguishable from "nothing was owed".
        var result = await _controller.RetryPendingPasswordChangesAsync(
            new PasswordQueueActionRequest { ConnectedSystemId = UnknownSystemId });

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task Cancel_AnUnidentifiableCaller_IsRefusedAsync()
    {
        // The Activity is the durable record of who stopped a password reaching somebody's account. There is no
        // acceptable way to record that anonymously.
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) }
        };

        var result = await _controller.CancelPendingPasswordChangesAsync(
            new PasswordQueueActionRequest { ApplyToAllChanges = true });

        Assert.That(result, Is.InstanceOf<UnauthorizedObjectResult>());
    }

    #endregion

    #region Request validation

    private static List<ValidationResult> Validate(PasswordQueueActionRequest request)
    {
        return request.Validate(new ValidationContext(request)).ToList();
    }

    [Test]
    public void ActionRequest_NamingNothing_IsRefused()
    {
        // An empty body would otherwise cancel every queued password change in the deployment, from a typo.
        var errors = Validate(new PasswordQueueActionRequest());

        Assert.That(errors, Has.Exactly(1).Items);
        Assert.That(errors[0].ErrorMessage, Does.Contain("applyToAllChanges"));
    }

    [Test]
    public void ActionRequest_ExplicitlyAskingForTheWholeQueue_IsAllowed()
    {
        Assert.That(Validate(new PasswordQueueActionRequest { ApplyToAllChanges = true }), Is.Empty);
    }

    [TestCase("connectedSystem")]
    [TestCase("status")]
    [TestCase("search")]
    public void ActionRequest_AnyOneCriterion_IsEnough(string criterion)
    {
        var request = criterion switch
        {
            "connectedSystem" => new PasswordQueueActionRequest { ConnectedSystemId = CorporateAdId },
            "status" => new PasswordQueueActionRequest { Status = PendingPasswordChangeStatus.Parked },
            _ => new PasswordQueueActionRequest { SearchText = "Ada" }
        };

        Assert.That(Validate(request), Is.Empty);
    }

    [Test]
    public void ActionRequest_MoreIdsThanMayBeNamed_IsRefused()
    {
        var request = new PasswordQueueActionRequest
        {
            Ids = Enumerable.Range(0, PasswordQueueActionRequest.MaximumIds + 1).Select(_ => Guid.NewGuid()).ToList()
        };

        Assert.That(Validate(request), Has.Exactly(1).Items);
    }

    [Test]
    public void ActionRequest_CombinesIdentifiersWithTheOtherCriteria()
    {
        // "These three, if they are still Parked" is the right shape for acting on what a list showed a moment
        // ago: a row that has moved on since simply does not match, rather than being acted on regardless.
        var ids = new[] { Guid.NewGuid() };
        var filter = new PasswordQueueActionRequest
        {
            Ids = ids,
            Status = PendingPasswordChangeStatus.Parked
        }.ToFilter();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(filter.Ids, Is.EquivalentTo(ids));
            Assert.That(filter.Status, Is.EqualTo(PendingPasswordChangeStatus.Parked));
            Assert.That(filter.TargetsSpecificChanges, Is.True);
        }
    }

    #endregion
}
