// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Connectors;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
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
/// A generated mapping's <c>exclusions</c> and <c>participants</c> over the REST API (Unique Value Generation, #242,
/// release 3): set on create, replaced, cleared or left alone on update, refused when they name a Connected System the
/// value is not exported to unchanged, and both read back on every mapping response.
/// </summary>
[TestFixture]
public class SyncRuleMappingGenerationExclusionsApiTests
{
    private const int ImportRuleId = 1;
    private const int ExportRuleId = 2;
    private const int AccountNameId = 6;
    private const int AdSystemId = 20;
    private const int MailSystemId = 30;

    private Mock<IConnectedSystemRepository> _csRepo = null!;
    private InMemorySyncRepository _syncRepository = null!;
    private SynchronisationController _controller = null!;
    private SyncRule _importRule = null!;
    private SyncRule _exportRule = null!;
    private MetaverseAttribute _accountName = null!;
    private readonly Dictionary<int, SyncRuleMapping> _mappingsById = new();
    private int _nextMappingId = 100;

    [SetUp]
    public void SetUp()
    {
        _mappingsById.Clear();
        var repository = new Mock<IRepository>();
        _csRepo = new Mock<IConnectedSystemRepository>();
        var metaverseRepo = new Mock<IMetaverseRepository>();
        var activityRepo = new Mock<IActivityRepository>();
        var apiKeyRepo = new Mock<IApiKeyRepository>();
        var syncRepository = _syncRepository = new InMemorySyncRepository();
        repository.Setup(r => r.ConnectedSystems).Returns(_csRepo.Object);
        // The stored generation settings a save compares against to release a Needs Decision (#242, release 4): none.
        _csRepo.Setup(r => r.GetSyncRuleMappingGenerationsAsync(It.IsAny<IReadOnlyCollection<int>>()))
            .ReturnsAsync(() => new Dictionary<int, SyncRuleMappingGeneration>());
        repository.Setup(r => r.Metaverse).Returns(metaverseRepo.Object);
        repository.Setup(r => r.Activity).Returns(activityRepo.Object);
        repository.Setup(r => r.ApiKeys).Returns(apiKeyRepo.Object);
        repository.Setup(r => r.Sync).Returns(syncRepository);
        repository.Setup(r => r.ServiceSettings).Returns(new InMemoryServiceSettingsRepository());
        activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);

        _accountName = new MetaverseAttribute { Id = AccountNameId, Name = "Account Name", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        metaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(AccountNameId)).ReturnsAsync(_accountName);

        _importRule = new SyncRule { Id = ImportRuleId, Name = "HR Import", Direction = SyncRuleDirection.Import, ConnectedSystemId = 10, ConnectedSystemObjectTypeId = 7 };
        _exportRule = new SyncRule { Id = ExportRuleId, Name = "AD Export", Direction = SyncRuleDirection.Export, ConnectedSystemId = AdSystemId, ConnectedSystemObjectTypeId = 8 };
        _csRepo.Setup(r => r.GetSyncRuleAsync(ImportRuleId)).ReturnsAsync(_importRule);
        _csRepo.Setup(r => r.GetSyncRuleAsync(ExportRuleId)).ReturnsAsync(_exportRule);

        // AD takes Account Name unchanged; Mail only through an expression.
        var adRule = new SyncRule { Id = 50, ConnectedSystemId = AdSystemId, Direction = SyncRuleDirection.Export, Enabled = true };
        adRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            Enabled = true,
            TargetConnectedSystemAttributeId = 201,
            TargetConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Id = 201, Name = "sAMAccountName", Type = AttributeDataType.Text },
            Sources = { new SyncRuleMappingSource { MetaverseAttributeId = AccountNameId } }
        });
        var mailRule = new SyncRule { Id = 51, ConnectedSystemId = MailSystemId, Direction = SyncRuleDirection.Export, Enabled = true };
        mailRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            Enabled = true,
            TargetConnectedSystemAttributeId = 301,
            TargetConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Id = 301, Name = "mail", Type = AttributeDataType.Text },
            Sources = { new SyncRuleMappingSource { Expression = "mv[\"Account Name\"] + \"@corp.example\"" } }
        });
        _csRepo.Setup(r => r.GetExportSyncRulesWithAttributeFlowsAsync()).ReturnsAsync(() => [adRule, mailRule]);
        _csRepo.Setup(r => r.GetConnectedSystemNamesAsync()).ReturnsAsync(new Dictionary<int, string> { [AdSystemId] = "Active Directory", [MailSystemId] = "Mail" });
        _csRepo.Setup(r => r.GetConnectedSystemsWithConnectorDefinitionsAsync()).ReturnsAsync(() =>
        [
            new ConnectedSystem { Id = AdSystemId, Name = "Active Directory", ConnectorDefinition = new ConnectorDefinition { Name = "JIM LDAP Connector", SupportsUniquenessProbe = true, SupportsUniquenessRejectionClassification = true } },
            new ConnectedSystem { Id = MailSystemId, Name = "Mail", ConnectorDefinition = new ConnectorDefinition { Name = "JIM SQL Connector" } }
        ]);

        _csRepo.Setup(r => r.CreateSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>())).Returns((SyncRuleMapping m) =>
        {
            m.Id = _nextMappingId++;
            // The reload the controller answers with includes the navigations the save path cleared.
            m.SyncRule ??= m.SyncRuleId == ExportRuleId ? _exportRule : _importRule;
            if (m.TargetMetaverseAttributeId == AccountNameId)
                m.TargetMetaverseAttribute ??= _accountName;
            _mappingsById[m.Id] = m;
            return Task.CompletedTask;
        });
        _csRepo.Setup(r => r.UpdateSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>())).Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.GetSyncRuleMappingAsync(It.IsAny<int>())).Returns((int id) => Task.FromResult(_mappingsById.GetValueOrDefault(id)));
        _csRepo.Setup(r => r.GetSyncRuleMappingForUpdateAsync(It.IsAny<int>())).Returns((int id) => Task.FromResult(_mappingsById.GetValueOrDefault(id)));
        _csRepo.Setup(r => r.GetSyncRuleMappingsAsync(It.IsAny<int>())).Returns((int ruleId) =>
            Task.FromResult(_mappingsById.Values.Where(m => m.SyncRuleId == ruleId).ToList()));
        _csRepo.Setup(r => r.GetImportSyncRuleMappingsForMetaverseAttributeAsync(It.IsAny<int>(), It.IsAny<int>())).ReturnsAsync(new List<SyncRuleMapping>());

        var application = new JimApplication(repository.Object, syncRepository: syncRepository, connectorFactory: new ProbingConnectorFactory());
        _controller = new SynchronisationController(
            new Mock<ILogger<SynchronisationController>>().Object,
            application,
            new DynamicExpressoEvaluator(),
            new Mock<ICredentialProtectionService>().Object);

        var apiKeyId = Guid.NewGuid();
        apiKeyRepo.Setup(r => r.GetByIdAsync(apiKeyId)).ReturnsAsync(new JIM.Models.Security.ApiKey
        {
            Id = apiKeyId, Name = "TestApiKey", KeyHash = "test-hash", KeyPrefix = "test", IsEnabled = true, Created = DateTime.UtcNow
        });
        var claims = new List<Claim>
        {
            new("auth_method", "api_key"),
            new(ClaimTypes.NameIdentifier, apiKeyId.ToString()),
            new(ClaimTypes.Name, "TestApiKey")
        };
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "ApiKey")) }
        };
    }

    private SyncRuleMapping SeedImportGeneratedMapping(params int[] excludedSystemIds)
    {
        var mapping = new SyncRuleMapping
        {
            Id = _nextMappingId++,
            SyncRule = _importRule,
            SyncRuleId = ImportRuleId,
            TargetMetaverseAttribute = _accountName,
            TargetMetaverseAttributeId = AccountNameId,
            Generation = new SyncRuleMappingGeneration { Id = 1, TokenKind = GeneratedValueTokenKind.Random }
        };
        foreach (var id in excludedSystemIds)
            mapping.Generation.Exclusions.Add(new SyncRuleMappingGenerationExclusion { SyncRuleMappingGenerationId = 1, ConnectedSystemId = id });
        _mappingsById[mapping.Id] = mapping;
        return mapping;
    }

    private Task<IActionResult> PatchExclusionsAsync(int mappingId, List<int>? exclusions, int? attemptLimit = null, int ruleId = ImportRuleId) =>
        _controller.UpdateSyncRuleMappingAsync(ruleId, mappingId, new UpdateSyncRuleMappingRequest
        {
            Generation = new UpdateSyncRuleMappingGenerationRequest { Exclusions = exclusions, AttemptLimit = attemptLimit }
        });

    private static SyncRuleMappingDto Ok(IActionResult result)
    {
        Assert.That(result, Is.InstanceOf<OkObjectResult>(), (result as ObjectResult)?.Value is ApiErrorResponse error ? error.Message : null);
        return (SyncRuleMappingDto)((OkObjectResult)result).Value!;
    }

    [Test]
    public async Task CreateSyncRuleMappingAsync_WithAnExclusion_SavesItAndReportsParticipantsAsync()
    {
        var request = new CreateSyncRuleMappingRequest
        {
            TargetMetaverseAttributeId = AccountNameId,
            Sources = [],
            Generation = new CreateSyncRuleMappingGenerationRequest { TokenKind = GeneratedValueTokenKind.Random, Exclusions = [AdSystemId] }
        };

        var result = await _controller.CreateSyncRuleMappingAsync(ImportRuleId, request);

        Assert.That(result, Is.InstanceOf<CreatedAtRouteResult>(), (result as ObjectResult)?.Value is ApiErrorResponse error ? error.Message : null);
        var dto = (SyncRuleMappingDto)((CreatedAtRouteResult)result).Value!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.Generation!.Exclusions, Is.EqualTo(new[] { AdSystemId }));
            Assert.That(dto.Generation.Participants!.Select(p => (p.ConnectedSystemName, p.Check, p.Reason)), Is.EqualTo(new[]
            {
                ("Active Directory", GeneratedValueParticipantCheck.NotChecked, GeneratedValueParticipantReason.Excluded),
                ("Mail", GeneratedValueParticipantCheck.NotChecked, GeneratedValueParticipantReason.ExportedThroughExpression)
            }));
        }
    }

    [Test]
    public async Task CreateSyncRuleMappingAsync_ExcludingASystemReachedOnlyThroughAnExpression_ReturnsBadRequestNamingItAsync()
    {
        var request = new CreateSyncRuleMappingRequest
        {
            TargetMetaverseAttributeId = AccountNameId,
            Sources = [],
            Generation = new CreateSyncRuleMappingGenerationRequest { TokenKind = GeneratedValueTokenKind.Random, Exclusions = [MailSystemId] }
        };

        var result = await _controller.CreateSyncRuleMappingAsync(ImportRuleId, request);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        Assert.That(((ApiErrorResponse)((BadRequestObjectResult)result).Value!).Message, Does.Contain("Mail"));
    }

    [Test]
    public async Task UpdateSyncRuleMappingAsync_ExclusionsList_ReplacesThemAsync()
    {
        var mapping = SeedImportGeneratedMapping();

        var dto = Ok(await PatchExclusionsAsync(mapping.Id, [AdSystemId]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.Generation!.Exclusions, Is.EqualTo(new[] { AdSystemId }));
            Assert.That(dto.Generation.Participants!.Single(p => p.ConnectedSystemId == AdSystemId).IsExcluded, Is.True);
        }
    }

    [Test]
    public async Task UpdateSyncRuleMappingAsync_EmptyExclusions_ClearsThemAsync()
    {
        var mapping = SeedImportGeneratedMapping(AdSystemId);

        var dto = Ok(await PatchExclusionsAsync(mapping.Id, []));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.Generation!.Exclusions, Is.Empty);
            Assert.That(dto.Generation.Participants!.Single(p => p.ConnectedSystemId == AdSystemId).Check, Is.EqualTo(GeneratedValueParticipantCheck.JimRecordsAndProbe));
        }
    }

    [Test]
    public async Task UpdateSyncRuleMappingAsync_ExclusionsOmitted_LeavesThemUnchangedAsync()
    {
        var mapping = SeedImportGeneratedMapping(AdSystemId);

        var dto = Ok(await PatchExclusionsAsync(mapping.Id, null, attemptLimit: 25));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.Generation!.AttemptLimit, Is.EqualTo(25));
            Assert.That(dto.Generation.Exclusions, Is.EqualTo(new[] { AdSystemId }));
        }
    }

    [Test]
    public async Task UpdateSyncRuleMappingAsync_DuplicateExclusion_ReturnsBadRequestAsync()
    {
        var mapping = SeedImportGeneratedMapping();

        var result = await PatchExclusionsAsync(mapping.Id, [AdSystemId, AdSystemId]);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task UpdateSyncRuleMappingAsync_ExclusionsOnAnExportModeGeneratedValue_ReturnsBadRequestAsync()
    {
        var target = new ConnectedSystemObjectTypeAttribute { Id = 202, Name = "employeeID", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        var mapping = new SyncRuleMapping
        {
            Id = _nextMappingId++,
            SyncRule = _exportRule,
            SyncRuleId = ExportRuleId,
            TargetConnectedSystemAttribute = target,
            TargetConnectedSystemAttributeId = target.Id,
            Generation = new SyncRuleMappingGeneration { Id = 2, TokenKind = GeneratedValueTokenKind.Random }
        };
        _mappingsById[mapping.Id] = mapping;

        var result = await PatchExclusionsAsync(mapping.Id, [AdSystemId], ruleId: ExportRuleId);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task GetSyncRuleMappingsAsync_GeneratedMapping_CarriesExclusionsAndParticipantsAsync()
    {
        SeedImportGeneratedMapping(AdSystemId);
        _mappingsById[999] = new SyncRuleMapping { Id = 999, SyncRuleId = ImportRuleId, SyncRule = _importRule, TargetMetaverseAttributeId = 7 };

        var result = await _controller.GetSyncRuleMappingsAsync(ImportRuleId);

        var dtos = ((IEnumerable<SyncRuleMappingDto>)((OkObjectResult)result).Value!).ToList();
        var generated = dtos.Single(d => d.Generation != null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(generated.Generation!.Exclusions, Is.EqualTo(new[] { AdSystemId }));
            Assert.That(generated.Generation.Participants, Has.Count.EqualTo(2));
            Assert.That(dtos.Single(d => d.Id == 999).Generation, Is.Null);
        }
    }

    [Test]
    public async Task GetSyncRuleMappingAsync_GeneratedMapping_CarriesParticipantsAsync()
    {
        var mapping = SeedImportGeneratedMapping();

        var result = await _controller.GetSyncRuleMappingAsync(ImportRuleId, mapping.Id);

        var dto = (SyncRuleMappingDto)((OkObjectResult)result).Value!;
        var ad = dto.Generation!.Participants!.Single(p => p.ConnectedSystemId == AdSystemId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ad.AttributeName, Is.EqualTo("sAMAccountName"));
            Assert.That(ad.ConnectorName, Is.EqualTo("JIM LDAP Connector"));
            Assert.That(ad.Check, Is.EqualTo(GeneratedValueParticipantCheck.JimRecordsAndProbe));
            Assert.That(ad.CanBeExcluded, Is.True);
        }
    }

    // ---- Collision Remediation (release 4, Phase 9) ----

    [Test]
    public async Task GetSyncRuleMappingAsync_GeneratedMapping_SaysWhichParticipantsReportCollisionsAsync()
    {
        var mapping = SeedImportGeneratedMapping();

        var dto = (SyncRuleMappingDto)((OkObjectResult)await _controller.GetSyncRuleMappingAsync(ImportRuleId, mapping.Id)).Value!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.Generation!.Participants!.Single(p => p.ConnectedSystemId == AdSystemId).ReportsCollisions, Is.True);
            Assert.That(dto.Generation.Participants!.Single(p => p.ConnectedSystemId == MailSystemId).ReportsCollisions, Is.False);
            Assert.That(dto.Generation.CollisionRemediation, Is.True, "Collision Remediation is on by default");
        }
    }

    [Test]
    public async Task CreateSyncRuleMappingAsync_CollisionRemediationOff_SavesItAsync()
    {
        var result = await _controller.CreateSyncRuleMappingAsync(ImportRuleId, new CreateSyncRuleMappingRequest
        {
            TargetMetaverseAttributeId = AccountNameId,
            Sources = [],
            Generation = new CreateSyncRuleMappingGenerationRequest { TokenKind = GeneratedValueTokenKind.Random, CollisionRemediation = false }
        });

        Assert.That(result, Is.InstanceOf<CreatedAtRouteResult>(), (result as ObjectResult)?.Value is ApiErrorResponse error ? error.Message : null);
        var dto = (SyncRuleMappingDto)((CreatedAtRouteResult)result).Value!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.Generation!.CollisionRemediation, Is.False);
            Assert.That(_mappingsById[dto.Id].Generation!.CollisionRemediation, Is.False);
        }
    }

    [Test]
    public async Task UpdateSyncRuleMappingAsync_CollisionRemediationSwitchedOff_SavesItAndReleasesHeldDecisionsAsync()
    {
        var mapping = SeedImportGeneratedMapping();
        var stored = new SyncRuleMappingGeneration { Id = 1, TokenKind = GeneratedValueTokenKind.Random, CollisionRemediation = true };
        _csRepo.Setup(r => r.GetSyncRuleMappingGenerationsAsync(It.IsAny<IReadOnlyCollection<int>>()))
            .ReturnsAsync(() => new Dictionary<int, SyncRuleMappingGeneration> { [1] = stored });
        var held = new GeneratedValueAssignment
        {
            Id = Guid.NewGuid(), MetaverseObjectId = Guid.NewGuid(), MetaverseAttributeId = AccountNameId, Value = "r.okafor", NormalisedValue = "r.okafor",
            SyncRuleMappingGenerationId = 1, State = GeneratedValueAssignmentState.NeedsDecision, NeedsDecisionEnteredAt = DateTime.UtcNow
        };
        _syncRepository.SeedGeneratedValueAssignment(held);

        var dto = Ok(await _controller.UpdateSyncRuleMappingAsync(ImportRuleId, mapping.Id, new UpdateSyncRuleMappingRequest
        {
            Generation = new UpdateSyncRuleMappingGenerationRequest { CollisionRemediation = false }
        }));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mapping.Generation!.CollisionRemediation, Is.False);
            Assert.That(dto.Generation!.CollisionRemediation, Is.False);
            Assert.That(_syncRepository.GeneratedValueAssignments[held.Id].State, Is.EqualTo(GeneratedValueAssignmentState.Committed),
                "switching Collision Remediation is the administrator's answer, so the next export tries again");
        }
    }

    [Test]
    public async Task UpdateSyncRuleMappingAsync_CollisionRemediationOmitted_LeavesItUnchangedAsync()
    {
        var mapping = SeedImportGeneratedMapping();
        mapping.Generation!.CollisionRemediation = false;

        Ok(await PatchExclusionsAsync(mapping.Id, exclusions: null, attemptLimit: 50));

        Assert.That(mapping.Generation.CollisionRemediation, Is.False);
    }

    /// <summary>Connectors that can probe any attribute, never connected.</summary>
    private sealed class ProbingConnectorFactory : IConnectorFactory
    {
        public IConnector Create(string connectorName, ICredentialProtection? credentialProtection = null, ICertificateProvider? certificateProvider = null) =>
            new ProbingConnector(connectorName);
    }

    private sealed class ProbingConnector(string name) : IConnector, IConnectorUniquenessProbe
    {
        public string Name => name;
        public string? Description => null;
        public string? Url => null;
        public void OpenUniquenessProbeConnection(ConnectedSystem connectedSystem, Serilog.ILogger logger) => throw new AssertionException("must not connect");
        public bool CanProbeAttribute(string attributeName) => true;
        public Task<UniquenessProbeResult> ProbeAsync(UniquenessProbeRequest request, Serilog.ILogger logger, System.Threading.CancellationToken cancellationToken) =>
            throw new AssertionException("must not probe");
        public void CloseUniquenessProbeConnection() { }
    }
}
