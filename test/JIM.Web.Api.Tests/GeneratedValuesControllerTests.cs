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
using JIM.Models.Logic;
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
using InMemorySyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The generated value decision endpoints (Unique Value Generation, #242, release 4, Phase 9): the list, the summary,
/// the single read, and the Allow the rename and Try again actions, run through the real
/// <see cref="Application.Servers.GeneratedValueDecisionServer"/> over an in-memory repository.
/// </summary>
[TestFixture]
public class GeneratedValuesControllerTests
{
    private const int HrSystemId = 1;
    private const int CorporateAdId = 2;
    private const int ContractorLdapId = 3;
    private const int UnknownId = 99;
    private const int HrImportRuleId = 10;
    private const int GenerationId = 7;

    private InMemorySyncRepository _syncRepo = null!;
    private JimApplication _application = null!;
    private GeneratedValuesController _controller = null!;
    private List<Activity> _createdActivities = null!;
    private Guid _apiKeyId;
    private GeneratedValueAssignment _rita = null!;
    private GeneratedValueAssignment _ana = null!;
    private Guid _ritaObjectId;

    [SetUp]
    public void SetUp()
    {
        _createdActivities = [];
        var repository = new Mock<IRepository>();
        var activityRepo = new Mock<IActivityRepository>();
        var apiKeyRepo = new Mock<IApiKeyRepository>();
        var connectedSystemRepo = new Mock<IConnectedSystemRepository>();

        activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Callback<Activity>(a => _createdActivities.Add(a)).Returns(Task.CompletedTask);
        activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        connectedSystemRepo.Setup(r => r.GetConnectedSystemCoreAsync(It.IsAny<int>(), It.IsAny<bool>()))
            .ReturnsAsync((int id, bool _) => id == UnknownId ? null : new ConnectedSystem { Id = id, Name = $"System {id}" });
        connectedSystemRepo.Setup(r => r.GetSyncRuleAsync(It.IsAny<int>()))
            .ReturnsAsync((int id) => id == UnknownId ? null : new SyncRule { Id = id, Name = $"Rule {id}" });

        repository.Setup(r => r.Activity).Returns(activityRepo.Object);
        repository.Setup(r => r.ApiKeys).Returns(apiKeyRepo.Object);
        repository.Setup(r => r.ConnectedSystems).Returns(connectedSystemRepo.Object);
        repository.Setup(r => r.ServiceSettings).Returns(new InMemoryServiceSettingsRepository());

        _syncRepo = new InMemorySyncRepository();
        SeedEstate();
        repository.Setup(r => r.Sync).Returns(_syncRepo);

        _application = new JimApplication(repository.Object, syncRepository: _syncRepo);
        _controller = new GeneratedValuesController(new Mock<ILogger<GeneratedValuesController>>().Object, _application);

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

    // ---- List ----

    [Test]
    public async Task GetDecisionsAsync_NoFilter_ReturnsHeldValuesAndAllowedRenamesAsync()
    {
        var page = Page(await _controller.GetDecisionsAsync(new PaginationRequest()));

        var rita = page.Items.Single(i => i.Id == _rita.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(page.TotalCount, Is.EqualTo(2));
            Assert.That(page.Items.Select(i => i.Id), Is.EqualTo(new[] { _rita.Id, _ana.Id }), "newest wait first");
            Assert.That(rita.Status, Is.EqualTo(GeneratedValueDecisionStatus.NeedsDecision));
            Assert.That(rita.Reason, Is.EqualTo(GeneratedValueNeedsDecisionReason.AnchoredElsewhere));
            Assert.That(rita.MetaverseObjectId, Is.EqualTo(_ritaObjectId));
            Assert.That(rita.MetaverseObjectDisplayName, Is.EqualTo("Rita Okafor"));
            Assert.That(rita.AttributeName, Is.EqualTo("Account Name"));
            Assert.That(rita.Value, Is.EqualTo("r.okafor"));
            Assert.That(rita.RejectedByConnectedSystemId, Is.EqualTo(ContractorLdapId));
            Assert.That(rita.RejectedByConnectedSystemName, Is.EqualTo("Contractor LDAP"));
            Assert.That(rita.AnchoredByConnectedSystemName, Is.EqualTo("Corporate AD"));
            Assert.That(rita.SyncRuleId, Is.EqualTo(HrImportRuleId));
            Assert.That(rita.SyncRuleName, Is.EqualTo("HR Import"));
            Assert.That(rita.Since, Is.Not.Null);
        }
    }

    [Test]
    public async Task GetDecisionsAsync_FilteredByStatus_ReturnsOnlyThatStatusAsync()
    {
        var page = Page(await _controller.GetDecisionsAsync(new PaginationRequest(), status: GeneratedValueDecisionStatus.RenameAllowed));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(page.Items.Select(i => i.Id), Is.EqualTo(new[] { _ana.Id }));
            Assert.That(page.Items.Single().RenameAllowedBy, Is.EqualTo("Jay"));
        }
    }

    [Test]
    public async Task GetDecisionsAsync_FilteredByParticipatingSystem_ReturnsItsValuesAsync()
    {
        var page = Page(await _controller.GetDecisionsAsync(new PaginationRequest(), connectedSystemId: CorporateAdId, syncRuleId: HrImportRuleId));

        Assert.That(page.TotalCount, Is.EqualTo(2));
    }

    [Test]
    public async Task GetDecisionsAsync_StatusReleased_ReturnsBadRequestAsync()
    {
        var result = await _controller.GetDecisionsAsync(new PaginationRequest(), status: GeneratedValueDecisionStatus.Released);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task GetDecisionsAsync_UnknownConnectedSystemOrRule_ReturnsNotFoundAsync()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await _controller.GetDecisionsAsync(new PaginationRequest(), connectedSystemId: UnknownId), Is.InstanceOf<NotFoundObjectResult>());
            Assert.That(await _controller.GetDecisionsAsync(new PaginationRequest(), syncRuleId: UnknownId), Is.InstanceOf<NotFoundObjectResult>());
        }
    }

    // ---- Single and summary ----

    [Test]
    public async Task GetDecisionAsync_KnownAndUnknownId_ReturnsTheRowOrNotFoundAsync()
    {
        var known = await _controller.GetDecisionAsync(_rita.Id);
        var unknown = await _controller.GetDecisionAsync(Guid.NewGuid());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(((GeneratedValueDecisionDto)((OkObjectResult)known).Value!).Id, Is.EqualTo(_rita.Id));
            Assert.That(unknown, Is.InstanceOf<NotFoundObjectResult>());
        }
    }

    [Test]
    public async Task GetDecisionSummaryAsync_ReturnsTheCountsAsync()
    {
        var result = await _controller.GetDecisionSummaryAsync();

        var summary = (GeneratedValueDecisionSummary)((OkObjectResult)result).Value!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary.NeedsDecisionCount, Is.EqualTo(1));
            Assert.That(summary.RenameAllowedCount, Is.EqualTo(1));
            Assert.That(summary.CorrectedRecentlyCount, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task GetDecisionSummaryAsync_UnknownConnectedSystem_ReturnsNotFoundAsync()
    {
        Assert.That(await _controller.GetDecisionSummaryAsync(connectedSystemId: UnknownId), Is.InstanceOf<NotFoundObjectResult>());
    }

    // ---- Allow the rename ----

    [Test]
    public async Task AllowRenameAsync_NeedsDecision_ReturnsTheUpdatedRowAndRecordsTheKeyAsync()
    {
        var result = await _controller.AllowRenameAsync(_rita.Id);

        var dto = (GeneratedValueDecisionDto)((OkObjectResult)result).Value!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.Status, Is.EqualTo(GeneratedValueDecisionStatus.RenameAllowed));
            Assert.That(dto.RenameAllowedBy, Is.EqualTo("TestApiKey"));
            Assert.That(_createdActivities.Single().TargetOperationType, Is.EqualTo(ActivityTargetOperationType.AllowGeneratedValueRename));
            Assert.That(_createdActivities.Single().InitiatedById, Is.EqualTo(_apiKeyId));
        }
    }

    [Test]
    public async Task AllowRenameAsync_NotWaitingOrUnknown_ReturnsConflictOrNotFoundAsync()
    {
        var notWaiting = await _controller.AllowRenameAsync(_ana.Id);
        var unknown = await _controller.AllowRenameAsync(Guid.NewGuid());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(notWaiting, Is.InstanceOf<ConflictObjectResult>());
            Assert.That(unknown, Is.InstanceOf<NotFoundObjectResult>());
            Assert.That(_createdActivities, Is.Empty);
        }
    }

    // ---- Try again ----

    [Test]
    public async Task TryAgainAsync_NeedsDecision_ReturnsTheReleasedRowAsync()
    {
        var result = await _controller.TryAgainAsync(_rita.Id);

        var dto = (GeneratedValueDecisionDto)((OkObjectResult)result).Value!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.Status, Is.EqualTo(GeneratedValueDecisionStatus.Released));
            Assert.That(_createdActivities.Single().TargetOperationType, Is.EqualTo(ActivityTargetOperationType.RetryGeneratedValue));
        }
    }

    [Test]
    public async Task TryAgainAsync_AllowedRename_ReturnsConflictAsync()
    {
        Assert.That(await _controller.TryAgainAsync(_ana.Id), Is.InstanceOf<ConflictObjectResult>());
    }

    [Test]
    public async Task TryAgainForDecisionsAsync_Filter_ReleasesMatchingValuesAsync()
    {
        var result = await _controller.TryAgainForDecisionsAsync(new GeneratedValueDecisionActionRequest { SyncRuleId = HrImportRuleId });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(((GeneratedValueDecisionActionResponse)((OkObjectResult)result).Value!).AffectedCount, Is.EqualTo(1));
            Assert.That(_syncRepo.GeneratedValueAssignments[_rita.Id].State, Is.EqualTo(GeneratedValueAssignmentState.Committed));
        }
    }

    [Test]
    public async Task TryAgainForDecisionsAsync_UnknownConnectedSystem_ReturnsNotFoundAsync()
    {
        var result = await _controller.TryAgainForDecisionsAsync(new GeneratedValueDecisionActionRequest { ConnectedSystemId = UnknownId });

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public void GeneratedValueDecisionActionRequest_NoCriteria_IsRefusedUnlessEveryDecisionIsMeant()
    {
        var empty = new GeneratedValueDecisionActionRequest();
        var everything = new GeneratedValueDecisionActionRequest { ApplyToAllDecisions = true };
        var tooMany = new GeneratedValueDecisionActionRequest { Ids = Enumerable.Range(0, GeneratedValueDecisionActionRequest.MaximumIds + 1).Select(_ => Guid.NewGuid()).ToList() };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(empty.Validate(new ValidationContext(empty)), Is.Not.Empty, "an empty body must never act on every held value");
            Assert.That(everything.Validate(new ValidationContext(everything)), Is.Empty);
            Assert.That(tooMany.Validate(new ValidationContext(tooMany)), Is.Not.Empty);
        }
    }

    // ---- Estate ----

    private static PaginatedResponse<GeneratedValueDecisionDto> Page(IActionResult result)
    {
        Assert.That(result, Is.InstanceOf<OkObjectResult>(), (result as ObjectResult)?.Value is ApiErrorResponse error ? error.Message : null);
        return (PaginatedResponse<GeneratedValueDecisionDto>)((OkObjectResult)result).Value!;
    }

    private void SeedEstate()
    {
        var accountName = new MetaverseAttribute { Id = 40, Name = "Account Name", Type = AttributeDataType.Text };
        var userType = new MetaverseObjectType { Id = 1, Name = "User", PluralName = "Users" };
        var hr = new ConnectedSystem { Id = HrSystemId, Name = "HR CSV" };
        var corporate = new ConnectedSystem { Id = CorporateAdId, Name = "Corporate AD" };
        var contractor = new ConnectedSystem { Id = ContractorLdapId, Name = "Contractor LDAP" };
        foreach (var system in new[] { hr, corporate, contractor })
            _syncRepo.SeedConnectedSystem(system);

        var import = new SyncRule { Id = HrImportRuleId, Name = "HR Import", Direction = SyncRuleDirection.Import, Enabled = true, ConnectedSystemId = HrSystemId, ConnectedSystem = hr };
        import.AttributeFlowRules.Add(new SyncRuleMapping
        {
            Id = 100, SyncRule = import, SyncRuleId = import.Id, Enabled = true, TargetMetaverseAttribute = accountName, TargetMetaverseAttributeId = accountName.Id,
            Generation = new SyncRuleMappingGeneration { Id = GenerationId, SyncRuleMappingId = 100 }
        });
        var export = new SyncRule { Id = 20, Name = "Corporate AD Export", Direction = SyncRuleDirection.Export, Enabled = true, ConnectedSystemId = CorporateAdId, ConnectedSystem = corporate };
        export.AttributeFlowRules.Add(new SyncRuleMapping
        {
            Id = 200, SyncRule = export, SyncRuleId = export.Id, Enabled = true, TargetConnectedSystemAttributeId = 201,
            TargetConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Id = 201, Name = "sAMAccountName", Type = AttributeDataType.Text },
            Sources = { new SyncRuleMappingSource { Order = 0, MetaverseAttributeId = accountName.Id, MetaverseAttribute = accountName } }
        });
        _syncRepo.SeedSyncRule(import);
        _syncRepo.SeedSyncRule(export);

        MetaverseObject Person(string name)
        {
            var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = userType, CachedDisplayName = name };
            _syncRepo.SeedMetaverseObject(mvo);
            return mvo;
        }

        var rita = Person("Rita Okafor");
        _ritaObjectId = rita.Id;
        _rita = Assignment(rita, "r.okafor", a =>
        {
            a.State = GeneratedValueAssignmentState.NeedsDecision;
            a.NeedsDecisionEnteredAt = DateTime.UtcNow.AddHours(-2);
            a.NeedsDecisionReason = GeneratedValueNeedsDecisionReason.AnchoredElsewhere;
            a.RejectedByConnectedSystemId = ContractorLdapId;
            a.AnchoredByConnectedSystemId = CorporateAdId;
        });
        _ana = Assignment(Person("Ana Ruiz"), "a.ruiz", a =>
        {
            a.NeedsDecisionEnteredAt = DateTime.UtcNow.AddDays(-4);
            a.RejectedByConnectedSystemId = CorporateAdId;
            a.RenameAuthorised = true;
            a.RenameAuthorisedAt = DateTime.UtcNow.AddMinutes(-10);
            a.RenameAuthorisedByName = "Jay";
        });
        Assignment(Person("Joe Bloggs"), "joe.bloggs1", a =>
        {
            a.State = GeneratedValueAssignmentState.Remediated;
            a.RemediatedAt = DateTime.UtcNow.AddDays(-1);
            a.RejectedByConnectedSystemId = CorporateAdId;
        });

        GeneratedValueAssignment Assignment(MetaverseObject mvo, string value, Action<GeneratedValueAssignment> shape)
        {
            var assignment = new GeneratedValueAssignment
            {
                Id = Guid.NewGuid(), MetaverseObjectId = mvo.Id, MetaverseAttributeId = accountName.Id, Value = value, NormalisedValue = value,
                SyncRuleMappingGenerationId = GenerationId, State = GeneratedValueAssignmentState.Committed
            };
            shape(assignment);
            _syncRepo.SeedGeneratedValueAssignment(assignment);
            return assignment;
        }
    }
}
