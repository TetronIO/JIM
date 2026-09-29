// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Logic;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Unit tests for <see cref="JIM.Application.Servers.MetaverseServer"/>'s value provenance orchestration (#399):
/// existence short-circuits, Attribute Priority source ranking and state assignment, and how the pieces returned
/// by a mocked repository are assembled into a <see cref="MetaverseAttributeProvenance"/>.
/// </summary>
[TestFixture]
public class MetaverseServerProvenanceTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IMetaverseRepository> _mockMetaverseRepo = null!;
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
    private JimApplication _jim = null!;

    private readonly Guid _mvoId = Guid.NewGuid();
    private const int AttributeId = 42;
    private const int MetaverseObjectTypeId = 1;

    private MetaverseAttribute _attribute = null!;
    private ConnectedSystem _connectedSystem = null!;
    private SyncRule _syncRule = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockMetaverseRepo = new Mock<IMetaverseRepository>();
        _mockConnectedSystemRepo = new Mock<IConnectedSystemRepository>();

        _mockRepository.Setup(r => r.Metaverse).Returns(_mockMetaverseRepo.Object);
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);

        _jim = new JimApplication(_mockRepository.Object);

        _attribute = new MetaverseAttribute { Id = AttributeId, Name = "Department", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        _connectedSystem = new ConnectedSystem { Id = 9, Name = "HR" };
        _syncRule = new SyncRule
        {
            Id = 5,
            Name = "HR Import",
            Enabled = true,
            ConnectedSystem = _connectedSystem,
            ConnectedSystemId = _connectedSystem.Id,
            ConnectedSystemObjectTypeId = 3
        };

        // Defaults so every test does not have to stub every call.
        _mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(AttributeId, false)).ReturnsAsync(_attribute);
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeIdAsync(_mvoId)).ReturnsAsync(MetaverseObjectTypeId);
        _mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeCurrentValuesAsync(_mvoId, AttributeId, It.IsAny<int>()))
            .ReturnsAsync((new List<ProvenanceValue>(), 0));
        _mockMetaverseRepo.Setup(r => r.GetLastAttributeSetChangeAsync(_mvoId, AttributeId)).ReturnsAsync((ProvenanceChange?)null);
        _mockMetaverseRepo.Setup(r => r.GetAttributeHistoryRawEntriesAsync(_mvoId, AttributeId, It.IsAny<int>()))
            .ReturnsAsync(new List<MetaverseAttributeHistoryRawEntry>());
        _mockConnectedSystemRepo.Setup(r => r.GetImportSyncRuleMappingsForMetaverseAttributeAsync(MetaverseObjectTypeId, AttributeId))
            .ReturnsAsync(new List<SyncRuleMapping>());
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    #region existence short-circuits

    [Test]
    public async Task GetMetaverseObjectProvenanceAsync_DelegatesDirectlyToRepositoryAsync()
    {
        var expected = new MetaverseObjectProvenance { MetaverseObjectId = _mvoId };
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectProvenanceAsync(_mvoId)).ReturnsAsync(expected);

        var result = await _jim.Metaverse.GetMetaverseObjectProvenanceAsync(_mvoId);

        Assert.That(result, Is.SameAs(expected));
    }

    [Test]
    public async Task GetMetaverseObjectProvenanceAsync_MetaverseObjectDoesNotExist_ReturnsNullAsync()
    {
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectProvenanceAsync(_mvoId)).ReturnsAsync((MetaverseObjectProvenance?)null);

        var result = await _jim.Metaverse.GetMetaverseObjectProvenanceAsync(_mvoId);

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetMetaverseAttributeProvenanceAsync_AttributeDoesNotExist_ReturnsNullAsync()
    {
        _mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeAsync(AttributeId, false)).ReturnsAsync((MetaverseAttribute?)null);

        var result = await _jim.Metaverse.GetMetaverseAttributeProvenanceAsync(_mvoId, AttributeId);

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetMetaverseAttributeProvenanceAsync_MetaverseObjectDoesNotExist_ReturnsNullAsync()
    {
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectTypeIdAsync(_mvoId)).ReturnsAsync((int?)null);

        var result = await _jim.Metaverse.GetMetaverseAttributeProvenanceAsync(_mvoId, AttributeId);

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetMetaverseAttributeProvenanceAsync_ExistsAsync()
    {
        var result = await _jim.Metaverse.GetMetaverseAttributeProvenanceAsync(_mvoId, AttributeId);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.AttributeName, Is.EqualTo("Department"));
        Assert.That(result!.MetaverseObjectTypeId, Is.EqualTo(MetaverseObjectTypeId));
    }

    #endregion

    #region source ranking and state

    private SyncRuleMapping NewAttributeMapping(int mappingId, int connectedSystemAttributeId)
    {
        var mapping = new SyncRuleMapping { Id = mappingId, SyncRuleId = _syncRule.Id, SyncRule = _syncRule, TargetMetaverseAttributeId = AttributeId, Enabled = true };
        mapping.Sources.Add(new SyncRuleMappingSource
        {
            ConnectedSystemAttributeId = connectedSystemAttributeId,
            ConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Id = connectedSystemAttributeId, Name = "dept", Type = AttributeDataType.Text },
            Order = 0
        });
        return mapping;
    }

    [Test]
    public async Task GetMetaverseAttributeProvenanceAsync_MultipleMappings_AssignsSequentialRanksAsync()
    {
        var mappingA = NewAttributeMapping(1, 101);
        var mappingB = NewAttributeMapping(2, 102);
        _mockConnectedSystemRepo.Setup(r => r.GetImportSyncRuleMappingsForMetaverseAttributeAsync(MetaverseObjectTypeId, AttributeId))
            .ReturnsAsync(new List<SyncRuleMapping> { mappingA, mappingB });
        _mockMetaverseRepo.Setup(r => r.GetJoinedConnectedSystemObjectForProvenanceAsync(_mvoId, _connectedSystem.Id, _syncRule.ConnectedSystemObjectTypeId))
            .ReturnsAsync((ConnectedSystemObject?)null);

        var result = await _jim.Metaverse.GetMetaverseAttributeProvenanceAsync(_mvoId, AttributeId);

        Assert.That(result!.Sources.Select(s => s.Rank), Is.EqualTo(new[] { 1, 2 }));
        Assert.That(result!.Sources.Select(s => s.MappingId), Is.EqualTo(new[] { 1, 2 }));
    }

    [Test]
    public async Task GetMetaverseAttributeProvenanceAsync_DisabledMapping_ReturnsDisabledStateAsync()
    {
        var mapping = NewAttributeMapping(1, 101);
        mapping.Enabled = false;
        _mockConnectedSystemRepo.Setup(r => r.GetImportSyncRuleMappingsForMetaverseAttributeAsync(MetaverseObjectTypeId, AttributeId))
            .ReturnsAsync(new List<SyncRuleMapping> { mapping });

        var result = await _jim.Metaverse.GetMetaverseAttributeProvenanceAsync(_mvoId, AttributeId);

        Assert.That(result!.Sources.Single().State, Is.EqualTo(AttributeSourceState.Disabled));

        // A disabled mapping needs no joined Connected System Object lookup.
        _mockMetaverseRepo.Verify(r => r.GetJoinedConnectedSystemObjectForProvenanceAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Test]
    public async Task GetMetaverseAttributeProvenanceAsync_DisabledSynchronisationRule_ReturnsDisabledStateAsync()
    {
        var disabledRule = new SyncRule { Id = 6, Name = "Disabled Rule", Enabled = false, ConnectedSystem = _connectedSystem, ConnectedSystemId = _connectedSystem.Id, ConnectedSystemObjectTypeId = 3 };
        var mapping = new SyncRuleMapping { Id = 1, SyncRuleId = disabledRule.Id, SyncRule = disabledRule, TargetMetaverseAttributeId = AttributeId, Enabled = true };
        mapping.Sources.Add(new SyncRuleMappingSource { ConnectedSystemAttributeId = 101, Order = 0 });
        _mockConnectedSystemRepo.Setup(r => r.GetImportSyncRuleMappingsForMetaverseAttributeAsync(MetaverseObjectTypeId, AttributeId))
            .ReturnsAsync(new List<SyncRuleMapping> { mapping });

        var result = await _jim.Metaverse.GetMetaverseAttributeProvenanceAsync(_mvoId, AttributeId);

        Assert.That(result!.Sources.Single().State, Is.EqualTo(AttributeSourceState.Disabled));
    }

    [Test]
    public async Task GetMetaverseAttributeProvenanceAsync_NoJoinedConnectedSystemObject_ReturnsNotJoinedAsync()
    {
        var mapping = NewAttributeMapping(1, 101);
        _mockConnectedSystemRepo.Setup(r => r.GetImportSyncRuleMappingsForMetaverseAttributeAsync(MetaverseObjectTypeId, AttributeId))
            .ReturnsAsync(new List<SyncRuleMapping> { mapping });
        _mockMetaverseRepo.Setup(r => r.GetJoinedConnectedSystemObjectForProvenanceAsync(_mvoId, _connectedSystem.Id, _syncRule.ConnectedSystemObjectTypeId))
            .ReturnsAsync((ConnectedSystemObject?)null);

        var result = await _jim.Metaverse.GetMetaverseAttributeProvenanceAsync(_mvoId, AttributeId);

        Assert.That(result!.Sources.Single().State, Is.EqualTo(AttributeSourceState.NotJoined));
    }

    private SyncRuleMapping NewGeneratedMapping(int mappingId) => new()
    {
        Id = mappingId,
        SyncRuleId = _syncRule.Id,
        SyncRule = _syncRule,
        TargetMetaverseAttributeId = AttributeId,
        Enabled = true,
        Generation = new SyncRuleMappingGeneration()
    };

    private GeneratedValueOwnership NewOwnership(int mappingId, string value = "E1001", bool corrected = false) => new()
    {
        AttributeId = AttributeId,
        SyncRuleId = _syncRule.Id,
        SyncRuleMappingId = mappingId,
        Value = value,
        Corrected = corrected
    };

    private ProvenanceValue NewCurrentValue(string displayValue, int syncRuleId) => new()
    {
        DisplayValue = displayValue,
        Origin = new ValueOrigin
        {
            Kind = ValueOriginKind.SynchronisationRule,
            ConnectedSystemId = _connectedSystem.Id,
            ConnectedSystemName = _connectedSystem.Name,
            SyncRuleId = syncRuleId,
            SyncRuleName = "HR Import"
        }
    };

    private void StubJoined() =>
        _mockMetaverseRepo.Setup(r => r.GetJoinedConnectedSystemObjectForProvenanceAsync(_mvoId, _connectedSystem.Id, _syncRule.ConnectedSystemObjectTypeId))
            .ReturnsAsync(new ConnectedSystemObject { Id = Guid.NewGuid(), Type = new ConnectedSystemObjectType { Name = "user" } });

    [Test]
    public async Task GetMetaverseAttributeProvenanceAsync_GeneratedMappingThatGeneratedTheCurrentValue_IsInUseWithTheGeneratedValueAsync()
    {
        var mapping = NewGeneratedMapping(1);
        _mockConnectedSystemRepo.Setup(r => r.GetImportSyncRuleMappingsForMetaverseAttributeAsync(MetaverseObjectTypeId, AttributeId))
            .ReturnsAsync(new List<SyncRuleMapping> { mapping });
        StubJoined();
        _mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeCurrentValuesAsync(_mvoId, AttributeId, It.IsAny<int>()))
            .ReturnsAsync((new List<ProvenanceValue> { NewCurrentValue("E1001", _syncRule.Id) }, 1));
        _mockMetaverseRepo.Setup(r => r.GetGeneratedValueOwnershipsAsync(_mvoId))
            .ReturnsAsync(new List<GeneratedValueOwnership> { NewOwnership(mapping.Id) });

        var result = await _jim.Metaverse.GetMetaverseAttributeProvenanceAsync(_mvoId, AttributeId);

        var source = result!.Sources.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.IsGeneratedValue, Is.True);
            Assert.That(source.CandidateValues, Is.EqualTo(new[] { "E1001" }));
            Assert.That(source.State, Is.EqualTo(AttributeSourceState.InUse));
            Assert.That(source.Note, Is.Null);
            Assert.That(result.CurrentValues.Single().Origin.Kind, Is.EqualTo(ValueOriginKind.GeneratedValue));
        }
    }

    [Test]
    public async Task GetMetaverseAttributeProvenanceAsync_GeneratedMappingThatHasGeneratedNothing_HasNoValueAsync()
    {
        _mockConnectedSystemRepo.Setup(r => r.GetImportSyncRuleMappingsForMetaverseAttributeAsync(MetaverseObjectTypeId, AttributeId))
            .ReturnsAsync(new List<SyncRuleMapping> { NewGeneratedMapping(1) });
        StubJoined();

        var result = await _jim.Metaverse.GetMetaverseAttributeProvenanceAsync(_mvoId, AttributeId);

        var source = result!.Sources.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.IsGeneratedValue, Is.True);
            Assert.That(source.CandidateValues, Is.Empty);
            Assert.That(source.State, Is.EqualTo(AttributeSourceState.NoValue));
        }
    }

    [Test]
    public async Task GetMetaverseAttributeProvenanceAsync_AnotherMappingsGeneratedValue_IsNotThisMappingsCandidateAsync()
    {
        // Two generated mappings can target one attribute from different rules; only the one that produced the
        // assignment supplies its value.
        _mockConnectedSystemRepo.Setup(r => r.GetImportSyncRuleMappingsForMetaverseAttributeAsync(MetaverseObjectTypeId, AttributeId))
            .ReturnsAsync(new List<SyncRuleMapping> { NewGeneratedMapping(1) });
        StubJoined();
        _mockMetaverseRepo.Setup(r => r.GetGeneratedValueOwnershipsAsync(_mvoId))
            .ReturnsAsync(new List<GeneratedValueOwnership> { NewOwnership(mappingId: 2) });

        var result = await _jim.Metaverse.GetMetaverseAttributeProvenanceAsync(_mvoId, AttributeId);

        Assert.That(result!.Sources.Single().CandidateValues, Is.Empty);
    }

    [Test]
    public async Task GetMetaverseAttributeProvenanceAsync_AttributeMappingWithValue_IsOutrankedWhenNotCurrentContributorAsync()
    {
        var mapping = NewAttributeMapping(1, 101);
        var csoAttribute = new ConnectedSystemObjectTypeAttribute { Id = 101, Name = "dept", Type = AttributeDataType.Text };
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            Type = new ConnectedSystemObjectType { Name = "user", Attributes = new List<ConnectedSystemObjectTypeAttribute> { csoAttribute } }
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Attribute = csoAttribute, AttributeId = 101, StringValue = "Engineering" });

        _mockConnectedSystemRepo.Setup(r => r.GetImportSyncRuleMappingsForMetaverseAttributeAsync(MetaverseObjectTypeId, AttributeId))
            .ReturnsAsync(new List<SyncRuleMapping> { mapping });
        _mockMetaverseRepo.Setup(r => r.GetJoinedConnectedSystemObjectForProvenanceAsync(_mvoId, _connectedSystem.Id, _syncRule.ConnectedSystemObjectTypeId))
            .ReturnsAsync(cso);
        // No current values recorded, so this mapping's Synchronisation Rule cannot be the current contributor.
        _mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeCurrentValuesAsync(_mvoId, AttributeId, It.IsAny<int>()))
            .ReturnsAsync((new List<ProvenanceValue>(), 0));

        var result = await _jim.Metaverse.GetMetaverseAttributeProvenanceAsync(_mvoId, AttributeId);

        var source = result!.Sources.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.CandidateValues, Is.EqualTo(new[] { "Engineering" }));
            Assert.That(source.State, Is.EqualTo(AttributeSourceState.Outranked));
        }
    }

    [Test]
    public async Task GetMetaverseAttributeProvenanceAsync_AttributeMappingIsCurrentContributor_ReturnsInUseAsync()
    {
        var mapping = NewAttributeMapping(1, 101);
        var csoAttribute = new ConnectedSystemObjectTypeAttribute { Id = 101, Name = "dept", Type = AttributeDataType.Text };
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            Type = new ConnectedSystemObjectType { Name = "user", Attributes = new List<ConnectedSystemObjectTypeAttribute> { csoAttribute } }
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Attribute = csoAttribute, AttributeId = 101, StringValue = "Engineering" });

        _mockConnectedSystemRepo.Setup(r => r.GetImportSyncRuleMappingsForMetaverseAttributeAsync(MetaverseObjectTypeId, AttributeId))
            .ReturnsAsync(new List<SyncRuleMapping> { mapping });
        _mockMetaverseRepo.Setup(r => r.GetJoinedConnectedSystemObjectForProvenanceAsync(_mvoId, _connectedSystem.Id, _syncRule.ConnectedSystemObjectTypeId))
            .ReturnsAsync(cso);
        _mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeCurrentValuesAsync(_mvoId, AttributeId, It.IsAny<int>()))
            .ReturnsAsync((new List<ProvenanceValue>
            {
                new() { DisplayValue = "Engineering", Origin = new ValueOrigin { Kind = ValueOriginKind.SynchronisationRule, ConnectedSystemId = _connectedSystem.Id, SyncRuleId = _syncRule.Id } }
            }, 1));

        var result = await _jim.Metaverse.GetMetaverseAttributeProvenanceAsync(_mvoId, AttributeId);

        Assert.That(result!.Sources.Single().State, Is.EqualTo(AttributeSourceState.InUse));
    }

    [Test]
    public async Task GetMetaverseAttributeProvenanceAsync_ExpressionMappingEvaluatesAgainstJoinedCsoAsync()
    {
        var mapping = new SyncRuleMapping { Id = 1, SyncRuleId = _syncRule.Id, SyncRule = _syncRule, TargetMetaverseAttributeId = AttributeId, Enabled = true };
        mapping.Sources.Add(new SyncRuleMappingSource { Expression = "cs[\"dept\"] + \"-suffix\"", Order = 0 });

        var csoAttribute = new ConnectedSystemObjectTypeAttribute { Id = 101, Name = "dept", Type = AttributeDataType.Text };
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            Type = new ConnectedSystemObjectType { Name = "user", Attributes = new List<ConnectedSystemObjectTypeAttribute> { csoAttribute } }
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Attribute = csoAttribute, AttributeId = 101, StringValue = "Engineering" });

        _mockConnectedSystemRepo.Setup(r => r.GetImportSyncRuleMappingsForMetaverseAttributeAsync(MetaverseObjectTypeId, AttributeId))
            .ReturnsAsync(new List<SyncRuleMapping> { mapping });
        _mockMetaverseRepo.Setup(r => r.GetJoinedConnectedSystemObjectForProvenanceAsync(_mvoId, _connectedSystem.Id, _syncRule.ConnectedSystemObjectTypeId))
            .ReturnsAsync(cso);

        var result = await _jim.Metaverse.GetMetaverseAttributeProvenanceAsync(_mvoId, AttributeId);

        var source = result!.Sources.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.IsExpression, Is.True);
            Assert.That(source.CandidateValues, Is.EqualTo(new[] { "Engineering-suffix" }));
            Assert.That(source.State, Is.EqualTo(AttributeSourceState.Outranked));
        }
    }

    [Test]
    public async Task GetMetaverseAttributeProvenanceAsync_ExpressionMappingThrows_ReturnsNotEvaluatedWithMessageAsync()
    {
        var mapping = new SyncRuleMapping { Id = 1, SyncRuleId = _syncRule.Id, SyncRule = _syncRule, TargetMetaverseAttributeId = AttributeId, Enabled = true };
        mapping.Sources.Add(new SyncRuleMappingSource { Expression = "1 / 0", Order = 0 });

        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), Type = new ConnectedSystemObjectType { Name = "user" } };

        _mockConnectedSystemRepo.Setup(r => r.GetImportSyncRuleMappingsForMetaverseAttributeAsync(MetaverseObjectTypeId, AttributeId))
            .ReturnsAsync(new List<SyncRuleMapping> { mapping });
        _mockMetaverseRepo.Setup(r => r.GetJoinedConnectedSystemObjectForProvenanceAsync(_mvoId, _connectedSystem.Id, _syncRule.ConnectedSystemObjectTypeId))
            .ReturnsAsync(cso);

        var result = await _jim.Metaverse.GetMetaverseAttributeProvenanceAsync(_mvoId, AttributeId);

        var source = result!.Sources.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.State, Is.EqualTo(AttributeSourceState.NotEvaluated));
            Assert.That(source.Note, Is.Not.Null.And.Not.Empty);
        }
    }

    #endregion

    #region assembly wiring

    [Test]
    public async Task GetMetaverseAttributeProvenanceAsync_PassesThroughLastSetAndHistoryFromRepositoryAsync()
    {
        var change = new ProvenanceChange { ChangeTime = DateTime.UtcNow, ActivityDescription = "Delta Synchronisation" };
        _mockMetaverseRepo.Setup(r => r.GetLastAttributeSetChangeAsync(_mvoId, AttributeId)).ReturnsAsync(change);

        var rawHistory = new List<MetaverseAttributeHistoryRawEntry>
        {
            new()
            {
                ChangeId = Guid.NewGuid(),
                ValueChangeType = JIM.Models.Enums.ValueChangeType.Add,
                DisplayValue = "Engineering",
                Change = new ProvenanceChange { ChangeTime = DateTime.UtcNow }
            }
        };
        _mockMetaverseRepo.Setup(r => r.GetAttributeHistoryRawEntriesAsync(_mvoId, AttributeId, It.IsAny<int>())).ReturnsAsync(rawHistory);

        var result = await _jim.Metaverse.GetMetaverseAttributeProvenanceAsync(_mvoId, AttributeId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result!.LastSet, Is.SameAs(change));
            Assert.That(result!.History, Has.Count.EqualTo(1));
            Assert.That(result!.History[0].Kind, Is.EqualTo(AttributeHistoryChangeKind.Added));
            Assert.That(result!.HistoryTruncated, Is.False);
        }
    }

    [Test]
    public async Task GetMetaverseAttributeProvenanceAsync_GeneratedCurrentValue_MarksItsHistoryAndSkipsTheConnectedSystemObjectAsync()
    {
        // The Connected System Object did not supply a generated value, so naming it as where the value came
        // from would be wrong.
        _mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeCurrentValuesAsync(_mvoId, AttributeId, It.IsAny<int>()))
            .ReturnsAsync((new List<ProvenanceValue> { NewCurrentValue("E1001", _syncRule.Id) }, 1));
        _mockMetaverseRepo.Setup(r => r.GetGeneratedValueOwnershipsAsync(_mvoId))
            .ReturnsAsync(new List<GeneratedValueOwnership> { NewOwnership(1, corrected: true) });
        _mockMetaverseRepo.Setup(r => r.GetAttributeHistoryRawEntriesAsync(_mvoId, AttributeId, It.IsAny<int>())).ReturnsAsync(new List<MetaverseAttributeHistoryRawEntry>
        {
            new() { ChangeId = Guid.NewGuid(), ValueChangeType = JIM.Models.Enums.ValueChangeType.Add, DisplayValue = "E1001", SyncRuleId = _syncRule.Id, Change = new ProvenanceChange { ChangeTime = DateTime.UtcNow } }
        });

        var result = await _jim.Metaverse.GetMetaverseAttributeProvenanceAsync(_mvoId, AttributeId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result!.CurrentValues.Single().Origin.Kind, Is.EqualTo(ValueOriginKind.GeneratedValue));
            Assert.That(result.CurrentValues.Single().Origin.Corrected, Is.True);
            Assert.That(result.History.Single().IsGeneratedValue, Is.True);
            Assert.That(result.ContributingConnectedSystemObject, Is.Null);
        }
        _mockMetaverseRepo.Verify(r => r.GetContributingConnectedSystemObjectAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int?>()), Times.Never);
    }

    [Test]
    public async Task GetMetaverseObjectProvenanceAsync_AttributeHeldByAGeneratedValue_ReportsAGeneratedValueOriginAsync()
    {
        var provenance = new MetaverseObjectProvenance { MetaverseObjectId = _mvoId };
        provenance.Attributes.Add(new MetaverseAttributeOriginSummary { AttributeId = AttributeId, AttributeName = "Employee ID", Origins = { NewCurrentValue("E1001", _syncRule.Id).Origin } });
        provenance.Attributes.Add(new MetaverseAttributeOriginSummary { AttributeId = 43, AttributeName = "Department", Origins = { NewCurrentValue("Engineering", _syncRule.Id).Origin } });
        _mockMetaverseRepo.Setup(r => r.GetMetaverseObjectProvenanceAsync(_mvoId)).ReturnsAsync(provenance);
        _mockMetaverseRepo.Setup(r => r.GetGeneratedValueOwnershipsAsync(_mvoId))
            .ReturnsAsync(new List<GeneratedValueOwnership> { NewOwnership(1) });

        var result = await _jim.Metaverse.GetMetaverseObjectProvenanceAsync(_mvoId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result!.Attributes[0].Origins.Single().Kind, Is.EqualTo(ValueOriginKind.GeneratedValue));
            Assert.That(result.Attributes[1].Origins.Single().Kind, Is.EqualTo(ValueOriginKind.SynchronisationRule), "only the generated attribute changes");
        }
    }

    [Test]
    public async Task GetMetaverseAttributeProvenanceAsync_ResolvesContributingConnectedSystemObjectFromFirstOriginAsync()
    {
        var provenanceCso = new ProvenanceConnectedSystemObject { Id = Guid.NewGuid(), ConnectedSystemId = _connectedSystem.Id, ConnectedSystemName = "HR", TypeName = "user" };
        _mockMetaverseRepo.Setup(r => r.GetMetaverseAttributeCurrentValuesAsync(_mvoId, AttributeId, It.IsAny<int>()))
            .ReturnsAsync((new List<ProvenanceValue>
            {
                new() { DisplayValue = "Engineering", Origin = new ValueOrigin { Kind = ValueOriginKind.SynchronisationRule, ConnectedSystemId = _connectedSystem.Id, SyncRuleId = _syncRule.Id } }
            }, 1));
        _mockMetaverseRepo.Setup(r => r.GetContributingConnectedSystemObjectAsync(_mvoId, _connectedSystem.Id, _syncRule.Id))
            .ReturnsAsync(provenanceCso);

        var result = await _jim.Metaverse.GetMetaverseAttributeProvenanceAsync(_mvoId, AttributeId);

        Assert.That(result!.ContributingConnectedSystemObject, Is.SameAs(provenanceCso));
    }

    [Test]
    public async Task GetMetaverseAttributeProvenanceAsync_NoRecordedOrigin_NoContributingConnectedSystemObjectLookupAsync()
    {
        var result = await _jim.Metaverse.GetMetaverseAttributeProvenanceAsync(_mvoId, AttributeId);

        Assert.That(result!.ContributingConnectedSystemObject, Is.Null);
        _mockMetaverseRepo.Verify(r => r.GetContributingConnectedSystemObjectAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int?>()), Times.Never);
    }

    #endregion
}
