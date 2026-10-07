// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Connectors;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.TestSupport;
using Moq;
using NUnit.Framework;
using Serilog;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// A generated value's exclusions (Unique Value Generation, #242, release 3) on every mapping save path, and the
/// participants read model <see cref="JIM.Application.Servers.ConnectedSystemServer.GetGeneratedValueParticipantsAsync(int)"/>.
/// An exclusion only means something for a Connected System the value is exported to unchanged, so every save refuses
/// one naming any other system, and an export-mode generated value, checked only in its own system, takes none.
/// </summary>
[TestFixture]
public class ConnectedSystemServerGeneratedValueExclusionsTests
{
    private const int HrSystemId = 1;
    private const int AdSystemId = 2;
    private const int PayrollSystemId = 3;
    private const int MailSystemId = 4;

    private Mock<IRepository> _repo = null!;
    private Mock<IConnectedSystemRepository> _csRepo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private MetaverseObject _initiator = null!;
    private MetaverseAttribute _accountName = null!;
    private List<SyncRule> _exportRules = null!;
    private RecordingConnectorFactory _connectorFactory = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _repo = new Mock<IRepository>();
        _csRepo = new Mock<IConnectedSystemRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _repo.Setup(r => r.ConnectedSystems).Returns(_csRepo.Object);
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _repo.Setup(r => r.ServiceSettings).Returns(new InMemoryServiceSettingsRepository());

        _csRepo.Setup(r => r.GetImportSyncRulesForMetaverseObjectTypeAsync(It.IsAny<int>())).ReturnsAsync(() => []);
        // The stored generation settings a save compares against to release a Needs Decision (#242, release 4): none.
        _csRepo.Setup(r => r.GetSyncRuleMappingGenerationsAsync(It.IsAny<IReadOnlyCollection<int>>()))
            .ReturnsAsync(() => new Dictionary<int, SyncRuleMappingGeneration>());
        _csRepo.Setup(r => r.GetSyncRuleMappingsAsync(It.IsAny<int>())).ReturnsAsync(new List<SyncRuleMapping>());
        _csRepo.Setup(r => r.GetImportSyncRuleMappingsForMetaverseAttributeAsync(It.IsAny<int>(), It.IsAny<int>())).ReturnsAsync(new List<SyncRuleMapping>());
        _csRepo.Setup(r => r.CreateSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>())).Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.UpdateSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>())).Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.CreateSyncRuleAsync(It.IsAny<SyncRule>())).Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.GetMaxConfigurationChangeVersionAsync(It.IsAny<ActivityTargetType>(), It.IsAny<int>())).ReturnsAsync(0);

        _accountName = new MetaverseAttribute { Id = 5, Name = "Account Name", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };

        // AD takes Account Name unchanged; Payroll takes it unchanged through a disabled flow; Mail only reads it
        // inside an expression.
        _exportRules =
        [
            ExportRule(AdSystemId, enabled: true, Direct(20, "sAMAccountName", enabled: true)),
            ExportRule(PayrollSystemId, enabled: true, Direct(30, "login", enabled: false)),
            ExportRule(MailSystemId, enabled: true, new SyncRuleMapping
            {
                Enabled = true,
                TargetConnectedSystemAttributeId = 40,
                TargetConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Id = 40, Name = "mail", Type = AttributeDataType.Text },
                Sources = { new SyncRuleMappingSource { Expression = "mv[\"Account Name\"] + \"@corp.example\"" } }
            })
        ];
        _csRepo.Setup(r => r.GetExportSyncRulesWithAttributeFlowsAsync()).ReturnsAsync(() => _exportRules);
        _csRepo.Setup(r => r.GetConnectedSystemNamesAsync()).ReturnsAsync(new Dictionary<int, string>
        {
            [HrSystemId] = "HR", [AdSystemId] = "Active Directory", [PayrollSystemId] = "Payroll", [MailSystemId] = "Mail", [9] = "Unrelated"
        });
        _csRepo.Setup(r => r.GetConnectedSystemsWithConnectorDefinitionsAsync()).ReturnsAsync(() =>
        [
            System(HrSystemId, "HR", "JIM File Connector", supportsProbe: false),
            System(AdSystemId, "Active Directory", "JIM LDAP Connector", supportsProbe: true),
            System(PayrollSystemId, "Payroll", "JIM SQL Connector", supportsProbe: false),
            System(MailSystemId, "Mail", "JIM LDAP Connector", supportsProbe: true)
        ]);

        _connectorFactory = new RecordingConnectorFactory();
        _initiator = TestUtilities.GetInitiatedBy();
    }

    private JimApplication BuildApplication() =>
        new(_repo.Object, syncRepository: new JIM.InMemoryData.SyncRepository(), connectorFactory: _connectorFactory);

    private static ConnectedSystem System(int id, string name, string connectorName, bool supportsProbe) => new()
    {
        Id = id,
        Name = name,
        ConnectorDefinition = new ConnectorDefinition { Name = connectorName, SupportsUniquenessProbe = supportsProbe }
    };

    private SyncRuleMapping Direct(int targetId, string targetName, bool enabled) => new()
    {
        Enabled = enabled,
        TargetConnectedSystemAttributeId = targetId,
        TargetConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Id = targetId, Name = targetName, Type = AttributeDataType.Text },
        Sources = { new SyncRuleMappingSource { MetaverseAttributeId = _accountName.Id, MetaverseAttribute = _accountName } }
    };

    private static SyncRule ExportRule(int connectedSystemId, bool enabled, SyncRuleMapping mapping)
    {
        var rule = new SyncRule { Id = 100 + connectedSystemId, Name = $"Export {connectedSystemId}", ConnectedSystemId = connectedSystemId, Enabled = enabled, Direction = SyncRuleDirection.Export };
        mapping.SyncRuleId = rule.Id;
        rule.AttributeFlowRules.Add(mapping);
        return rule;
    }

    private SyncRuleMapping NewImportGeneratedMapping(params int[] excludedSystemIds)
    {
        var mapping = new SyncRuleMapping
        {
            Id = 12,
            SyncRuleId = 1,
            SyncRule = new SyncRule { Id = 1, Name = "HR Import", Direction = SyncRuleDirection.Import, ConnectedSystemId = HrSystemId },
            TargetMetaverseAttribute = _accountName,
            TargetMetaverseAttributeId = _accountName.Id,
            Generation = new SyncRuleMappingGeneration { Id = 900, TokenKind = GeneratedValueTokenKind.Random }
        };
        foreach (var id in excludedSystemIds)
            mapping.Generation.Exclusions.Add(new SyncRuleMappingGenerationExclusion { ConnectedSystemId = id });
        return mapping;
    }

    private static SyncRuleMapping NewExportGeneratedMapping(params int[] excludedSystemIds)
    {
        var target = new ConnectedSystemObjectTypeAttribute { Id = 21, Name = "employeeID", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, Writability = AttributeWritability.Writable };
        var mapping = new SyncRuleMapping
        {
            Id = 13,
            SyncRuleId = 101,
            SyncRule = new SyncRule { Id = 101, Name = "AD Export", Direction = SyncRuleDirection.Export, ConnectedSystemId = AdSystemId },
            TargetConnectedSystemAttribute = target,
            TargetConnectedSystemAttributeId = target.Id,
            Generation = new SyncRuleMappingGeneration { Id = 901, TokenKind = GeneratedValueTokenKind.Random }
        };
        foreach (var id in excludedSystemIds)
            mapping.Generation.Exclusions.Add(new SyncRuleMappingGenerationExclusion { ConnectedSystemId = id });
        return mapping;
    }

    #region Create

    [Test]
    public async Task CreateSyncRuleMappingAsync_ExcludesASystemTheValueIsExportedToUnchanged_IsSavedAsync()
    {
        var mapping = NewImportGeneratedMapping(AdSystemId);

        await BuildApplication().ConnectedSystems.CreateSyncRuleMappingAsync(mapping, _initiator);

        _csRepo.Verify(r => r.CreateSyncRuleMappingAsync(mapping), Times.Once);
    }

    [Test]
    public async Task CreateSyncRuleMappingAsync_ExcludesASystemReachedOnlyThroughADisabledFlow_IsSavedAsync()
    {
        var mapping = NewImportGeneratedMapping(PayrollSystemId);

        await BuildApplication().ConnectedSystems.CreateSyncRuleMappingAsync(mapping, _initiator);

        _csRepo.Verify(r => r.CreateSyncRuleMappingAsync(mapping), Times.Once);
    }

    [Test]
    public void CreateSyncRuleMappingAsync_ExcludesASystemWithNoFlowOfTheValue_IsRefusedNamingTheSystem()
    {
        var mapping = NewImportGeneratedMapping(9);

        var ex = Assert.ThrowsAsync<ArgumentException>(() => BuildApplication().ConnectedSystems.CreateSyncRuleMappingAsync(mapping, _initiator));

        Assert.That(ex!.Message, Does.Contain("Unrelated"));
        _csRepo.Verify(r => r.CreateSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>()), Times.Never);
    }

    [Test]
    public void CreateSyncRuleMappingAsync_ExcludesASystemReachedOnlyThroughAnExpression_IsRefusedNamingTheSystem()
    {
        var mapping = NewImportGeneratedMapping(MailSystemId);

        var ex = Assert.ThrowsAsync<ArgumentException>(() => BuildApplication().ConnectedSystems.CreateSyncRuleMappingAsync(mapping, _initiator));

        Assert.That(ex!.Message, Does.Contain("Mail"));
    }

    [Test]
    public void CreateSyncRuleMappingAsync_ExportModeGeneratedValueWithExclusions_IsRefused()
    {
        var mapping = NewExportGeneratedMapping(AdSystemId);

        Assert.ThrowsAsync<ArgumentException>(() => BuildApplication().ConnectedSystems.CreateSyncRuleMappingAsync(mapping, _initiator));
        _csRepo.Verify(r => r.CreateSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>()), Times.Never);
    }

    [Test]
    public async Task CreateSyncRuleMappingAsync_NoExclusions_DoesNotReadTheExportRulesAsync()
    {
        await BuildApplication().ConnectedSystems.CreateSyncRuleMappingAsync(NewImportGeneratedMapping(), _initiator);

        _csRepo.Verify(r => r.GetExportSyncRulesWithAttributeFlowsAsync(), Times.Never);
    }

    #endregion

    #region Settings update

    private SyncRuleMapping ArrangeForSettingsUpdate(params int[] excludedSystemIds)
    {
        var mapping = NewImportGeneratedMapping(excludedSystemIds);
        _csRepo.Setup(r => r.GetSyncRuleMappingForUpdateAsync(mapping.Id)).ReturnsAsync(mapping);
        return mapping;
    }

    private static SyncRuleMappingSettingsUpdate ExclusionsUpdate(List<int>? exclusions, int? attemptLimit = null) =>
        new() { Generation = new SyncRuleMappingGenerationSettingsUpdate { Exclusions = exclusions, AttemptLimit = attemptLimit } };

    [Test]
    public async Task UpdateSyncRuleMappingSettingsAsync_ExclusionsList_ReplacesTheExclusionsAsync()
    {
        var mapping = ArrangeForSettingsUpdate(AdSystemId);

        var result = await BuildApplication().ConnectedSystems.UpdateSyncRuleMappingSettingsAsync(mapping.Id, ExclusionsUpdate([PayrollSystemId]), _initiator);

        Assert.That(result!.Generation!.Exclusions.Select(e => e.ConnectedSystemId), Is.EqualTo(new[] { PayrollSystemId }));
        _csRepo.Verify(r => r.UpdateSyncRuleMappingAsync(mapping), Times.Once);
    }

    [Test]
    public async Task UpdateSyncRuleMappingSettingsAsync_ListKeepingAnExistingExclusion_KeepsTheSameInstanceAsync()
    {
        var mapping = ArrangeForSettingsUpdate(AdSystemId);
        var existing = mapping.Generation!.Exclusions.Single();

        var result = await BuildApplication().ConnectedSystems.UpdateSyncRuleMappingSettingsAsync(mapping.Id, ExclusionsUpdate([AdSystemId, PayrollSystemId]), _initiator);

        Assert.That(result!.Generation!.Exclusions, Does.Contain(existing), "an exclusion kept by the update must not be deleted and re-added");
        Assert.That(result.Generation.Exclusions.Select(e => e.ConnectedSystemId), Is.EquivalentTo(new[] { AdSystemId, PayrollSystemId }));
    }

    [Test]
    public async Task UpdateSyncRuleMappingSettingsAsync_EmptyList_ClearsTheExclusionsAsync()
    {
        var mapping = ArrangeForSettingsUpdate(AdSystemId, PayrollSystemId);

        var result = await BuildApplication().ConnectedSystems.UpdateSyncRuleMappingSettingsAsync(mapping.Id, ExclusionsUpdate([]), _initiator);

        Assert.That(result!.Generation!.Exclusions, Is.Empty);
    }

    [Test]
    public async Task UpdateSyncRuleMappingSettingsAsync_NoExclusionsNamed_LeavesThemUnchangedAsync()
    {
        var mapping = ArrangeForSettingsUpdate(AdSystemId);

        var result = await BuildApplication().ConnectedSystems.UpdateSyncRuleMappingSettingsAsync(mapping.Id, ExclusionsUpdate(null, attemptLimit: 50), _initiator);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result!.Generation!.AttemptLimit, Is.EqualTo(50));
            Assert.That(result.Generation.Exclusions.Select(e => e.ConnectedSystemId), Is.EqualTo(new[] { AdSystemId }));
        }
    }

    [Test]
    public void UpdateSyncRuleMappingSettingsAsync_DuplicateExclusion_IsRefused()
    {
        var mapping = ArrangeForSettingsUpdate();

        Assert.ThrowsAsync<ArgumentException>(() =>
            BuildApplication().ConnectedSystems.UpdateSyncRuleMappingSettingsAsync(mapping.Id, ExclusionsUpdate([AdSystemId, AdSystemId]), _initiator));
        _csRepo.Verify(r => r.UpdateSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>()), Times.Never);
    }

    [Test]
    public void UpdateSyncRuleMappingSettingsAsync_ExclusionOfANonParticipatingSystem_IsRefusedAndNothingIsSaved()
    {
        var mapping = ArrangeForSettingsUpdate();

        var ex = Assert.ThrowsAsync<ArgumentException>(() =>
            BuildApplication().ConnectedSystems.UpdateSyncRuleMappingSettingsAsync(mapping.Id, ExclusionsUpdate([MailSystemId]), _initiator));

        Assert.That(ex!.Message, Does.Contain("Mail"));
        _csRepo.Verify(r => r.UpdateSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>()), Times.Never);
        _activityRepo.Verify(r => r.CreateActivityAsync(It.IsAny<Activity>()), Times.Never, "a refused update must leave no Activity behind");
    }

    #endregion

    #region Full mapping update and whole-rule save

    [Test]
    public void UpdateSyncRuleMappingAsync_ExclusionOfANonParticipatingSystem_IsRefused()
    {
        var mapping = NewImportGeneratedMapping(9);

        Assert.ThrowsAsync<ArgumentException>(() => BuildApplication().ConnectedSystems.UpdateSyncRuleMappingAsync(mapping, _initiator));
        _csRepo.Verify(r => r.UpdateSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>()), Times.Never);
    }

    private SyncRule NewImportRuleWith(SyncRuleMapping mapping)
    {
        var rule = new SyncRule
        {
            Id = 0,
            Name = "HR Import",
            Direction = SyncRuleDirection.Import,
            Enabled = false,
            ConnectedSystem = new ConnectedSystem { Id = HrSystemId, Name = "HR" },
            ConnectedSystemId = HrSystemId,
            ConnectedSystemObjectType = new ConnectedSystemObjectType { Id = 7, Name = "person" },
            ConnectedSystemObjectTypeId = 7,
            MetaverseObjectType = new MetaverseObjectType { Id = 1, Name = "Person" },
            MetaverseObjectTypeId = 1
        };
        mapping.Id = 0;
        mapping.SyncRule = null;
        mapping.SyncRuleId = 0;
        rule.AttributeFlowRules.Add(mapping);
        return rule;
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_ValidExclusion_IsSavedAsync()
    {
        var rule = NewImportRuleWith(NewImportGeneratedMapping(AdSystemId));

        await BuildApplication().ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, _initiator);

        _csRepo.Verify(r => r.CreateSyncRuleAsync(rule), Times.Once);
    }

    [Test]
    public void CreateOrUpdateSyncRuleAsync_ExclusionOfANonParticipatingSystem_IsRefusedNamingTheSystem()
    {
        var rule = NewImportRuleWith(NewImportGeneratedMapping(9));

        var ex = Assert.ThrowsAsync<ArgumentException>(() => BuildApplication().ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, _initiator));

        Assert.That(ex!.Message, Does.Contain("Unrelated"));
        _csRepo.Verify(r => r.CreateSyncRuleAsync(It.IsAny<SyncRule>()), Times.Never);
    }

    [Test]
    public void CreateOrUpdateSyncRuleAsync_ApiKeyInitiatedExclusionOfANonParticipatingSystem_IsRefused()
    {
        var rule = NewImportRuleWith(NewImportGeneratedMapping(9));
        var apiKey = new JIM.Models.Security.ApiKey { Id = Guid.NewGuid(), Name = "Key", KeyHash = "h", KeyPrefix = "p", IsEnabled = true };

        Assert.ThrowsAsync<ArgumentException>(() => BuildApplication().ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, apiKey));
    }

    #endregion

    #region Participants

    [Test]
    public async Task GetGeneratedValueParticipantsAsync_ByMappingId_DescribesEachTargetAndNeverOpensAConnectionAsync()
    {
        var mapping = NewImportGeneratedMapping(PayrollSystemId);
        _csRepo.Setup(r => r.GetSyncRuleMappingAsync(mapping.Id)).ReturnsAsync(mapping);

        var rows = await BuildApplication().ConnectedSystems.GetGeneratedValueParticipantsAsync(mapping.Id);

        Assert.That(rows.Select(r => (r.ConnectedSystemName, r.AttributeName, r.Check, r.Reason)), Is.EqualTo(new[]
        {
            ("Active Directory", "sAMAccountName", GeneratedValueParticipantCheck.JimRecordsAndProbe, GeneratedValueParticipantReason.None),
            ("Mail", "mail", GeneratedValueParticipantCheck.NotChecked, GeneratedValueParticipantReason.ExportedThroughExpression),
            ("Payroll", "login", GeneratedValueParticipantCheck.NotChecked, GeneratedValueParticipantReason.Excluded)
        }));
        Assert.That(rows[0].ConnectorName, Is.EqualTo("JIM LDAP Connector"));
        Assert.That(_connectorFactory.Created.All(c => !c.Opened), Is.True, "describing participants must never open a connection");
        Assert.That(_connectorFactory.Created.Select(c => c.Name), Does.Not.Contain("JIM SQL Connector"),
            "a Connector Definition that does not declare the probe needs no Connector");
    }

    [Test]
    public async Task GetGeneratedValueParticipantsAsync_ConnectorRefusesTheAttribute_IsCheckedByRecordsOnlyAsync()
    {
        _connectorFactory.UnprobedAttributes.Add("sAMAccountName");
        var mapping = NewImportGeneratedMapping();
        _csRepo.Setup(r => r.GetSyncRuleMappingAsync(mapping.Id)).ReturnsAsync(mapping);

        var rows = await BuildApplication().ConnectedSystems.GetGeneratedValueParticipantsAsync(mapping.Id);

        Assert.That(rows.Single(r => r.ConnectedSystemId == AdSystemId).Reason, Is.EqualTo(GeneratedValueParticipantReason.AttributeNotProbed));
    }

    [Test]
    public async Task GetGeneratedValueParticipantsAsync_UnsavedMappingWithSuppliedExportRules_UsesThoseRulesAsync()
    {
        var mapping = NewImportGeneratedMapping();
        mapping.Id = 0;
        var suppliedRules = new[] { ExportRule(PayrollSystemId, enabled: true, Direct(30, "login", enabled: true)) };

        var rows = await BuildApplication().ConnectedSystems.GetGeneratedValueParticipantsAsync(mapping, HrSystemId, suppliedRules);

        var row = rows.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(row.ConnectedSystemName, Is.EqualTo("Payroll"));
            Assert.That(row.Reason, Is.EqualTo(GeneratedValueParticipantReason.ConnectorCannotProbe));
        }
        _csRepo.Verify(r => r.GetExportSyncRulesWithAttributeFlowsAsync(), Times.Never);
    }

    [Test]
    public async Task GetGeneratedValueParticipantsAsync_SeveralMappings_ReadsTheExportRulesOnceAsync()
    {
        var first = NewImportGeneratedMapping();
        var second = NewImportGeneratedMapping();
        second.Id = 14;
        var ordinary = new SyncRuleMapping { Id = 15, TargetMetaverseAttributeId = 6 };

        var rows = await BuildApplication().ConnectedSystems.GetGeneratedValueParticipantsAsync([first, second, ordinary], HrSystemId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rows.Keys, Is.EquivalentTo(new[] { first.Id, second.Id }));
            Assert.That(rows[first.Id], Has.Count.EqualTo(2), "AD and Mail; Payroll's flow is disabled and it is not excluded");
        }
        _csRepo.Verify(r => r.GetExportSyncRulesWithAttributeFlowsAsync(), Times.Once);
    }

    [Test]
    public async Task GetGeneratedValueParticipantsAsync_ExportModeMapping_IsItsOwnTargetAsync()
    {
        var mapping = NewExportGeneratedMapping();
        _csRepo.Setup(r => r.GetSyncRuleMappingAsync(mapping.Id)).ReturnsAsync(mapping);

        var row = (await BuildApplication().ConnectedSystems.GetGeneratedValueParticipantsAsync(mapping.Id)).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(row.ConnectedSystemName, Is.EqualTo("Active Directory"));
            Assert.That(row.AttributeName, Is.EqualTo("employeeID"));
            Assert.That(row.CanBeExcluded, Is.False);
        }
    }

    #endregion

    /// <summary>A Connector factory that records what it created, and Connectors that record whether they were opened.</summary>
    private sealed class RecordingConnectorFactory : IConnectorFactory
    {
        public List<RecordingProbeConnector> Created { get; } = [];

        public HashSet<string> UnprobedAttributes { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IConnector Create(string connectorName, ICredentialProtection? credentialProtection = null, ICertificateProvider? certificateProvider = null)
        {
            var connector = new RecordingProbeConnector(connectorName, UnprobedAttributes);
            Created.Add(connector);
            return connector;
        }
    }

    private sealed class RecordingProbeConnector(string name, HashSet<string> unprobedAttributes) : IConnector, IConnectorUniquenessProbe
    {
        public string Name => name;
        public string? Description => null;
        public string? Url => null;
        public bool Opened { get; private set; }

        public void OpenUniquenessProbeConnection(ConnectedSystem connectedSystem, ILogger logger) => Opened = true;

        public bool CanProbeAttribute(string attributeName) => !unprobedAttributes.Contains(attributeName);

        public Task<UniquenessProbeResult> ProbeAsync(UniquenessProbeRequest request, ILogger logger, CancellationToken cancellationToken) =>
            throw new AssertionException("describing participants must never probe");

        public void CloseUniquenessProbeConnection() { }
    }
}
