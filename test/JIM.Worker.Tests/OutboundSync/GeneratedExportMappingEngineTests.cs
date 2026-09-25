// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Worker.Tests.OutboundSync;

/// <summary>
/// Engine-level tests for a generated export mapping's marker (Unique Value Generation, #242, Phase 2 work
/// package H): <see cref="SyncEngine.ComputeAttributeValueChanges"/> called directly, with no worker, no
/// repository and no resolution - proving the pure engine's behaviour in isolation (plan decision 6: the
/// engine performs no I/O and never resolves a value itself).
/// </summary>
public class GeneratedExportMappingEngineTests
{
    private SyncEngine _engine = null!;
    private MetaverseObjectType _mvType = null!;
    private MetaverseAttribute _mvEmployeeIdAttribute = null!;
    private ConnectedSystemObjectType _csoType = null!;
    private ConnectedSystemObjectTypeAttribute _csoLoginNameAttribute = null!;
    private SyncRule _exportRule = null!;

    [SetUp]
    public void SetUp()
    {
        _engine = new SyncEngine();

        _mvEmployeeIdAttribute = new MetaverseAttribute
        {
            Id = 1,
            Name = "EmployeeId",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued
        };
        _mvType = new MetaverseObjectType { Id = 1, Name = "Person" };

        _csoLoginNameAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = 1,
            Name = "loginName",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued
        };
        _csoType = new ConnectedSystemObjectType { Id = 1, Name = "TicketingUser" };

        _exportRule = new SyncRule
        {
            Id = 1,
            Name = "Ticketing Export",
            Direction = SyncRuleDirection.Export,
            Enabled = true,
            ConnectedSystemObjectType = _csoType
        };
    }

    private MetaverseObject BuildMvoWithEmployeeId(string? employeeId)
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = _mvType };
        if (employeeId != null)
        {
            mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
            {
                Id = Guid.NewGuid(),
                MetaverseObject = mvo,
                Attribute = _mvEmployeeIdAttribute,
                AttributeId = _mvEmployeeIdAttribute.Id,
                StringValue = employeeId
            });
        }
        return mvo;
    }

    private SyncRuleMapping BuildGeneratedMapping(string? baseExpression, MissingInputBehaviour missingInputBehaviour = MissingInputBehaviour.ContributeNoValue)
    {
        var mapping = new SyncRuleMapping
        {
            Id = 1,
            SyncRule = _exportRule,
            SyncRuleId = _exportRule.Id,
            TargetConnectedSystemAttribute = _csoLoginNameAttribute,
            TargetConnectedSystemAttributeId = _csoLoginNameAttribute.Id,
            Generation = new SyncRuleMappingGeneration { TokenKind = GeneratedValueTokenKind.OnlyIfTaken }
        };

        if (baseExpression != null)
        {
            mapping.Sources.Add(new SyncRuleMappingSource
            {
                Order = 0,
                Expression = baseExpression,
                MissingInputBehaviour = missingInputBehaviour
            });
        }

        _exportRule.AttributeFlowRules.Add(mapping);
        return mapping;
    }

    [Test]
    public void ComputeAttributeValueChanges_GeneratedMappingBaseEvaluates_StagesMarkedChangeWithBaseValueAsync()
    {
        BuildGeneratedMapping("Lower(mv[\"EmployeeId\"])");
        var mvo = BuildMvoWithEmployeeId("E123");

        var changes = _engine.ComputeAttributeValueChanges(
            mvo, _exportRule, changedAttributes: [], changeType: PendingExportChangeType.Update,
            existingCso: null, csoAttributeCache: null, out _);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes, Has.Count.EqualTo(1));
            var change = changes[0];
            Assert.That(change.ChangeType, Is.EqualTo(PendingExportAttributeChangeType.Update));
            Assert.That(change.AttributeId, Is.EqualTo(_csoLoginNameAttribute.Id));
            Assert.That(change.StringValue, Is.Null, "the value must be left unset for the worker to resolve");
            Assert.That(change.PendingGeneration, Is.Not.Null);
            Assert.That(change.PendingGeneration!.BaseValue, Is.EqualTo("e123"));
            Assert.That(change.PendingGeneration.BaseUnavailable, Is.False);
        }
    }

    [Test]
    public void ComputeAttributeValueChanges_GeneratedMappingMissingInput_StagesMarkedChangeWithBaseUnavailable()
    {
        BuildGeneratedMapping("Lower(mv[\"EmployeeId\"])"); // default Missing Input Behaviour: ContributeNoValue
        var mvo = BuildMvoWithEmployeeId(null); // no EmployeeId value: the input is missing

        var changes = _engine.ComputeAttributeValueChanges(
            mvo, _exportRule, changedAttributes: [], changeType: PendingExportChangeType.Update,
            existingCso: null, csoAttributeCache: null, out _);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes, Has.Count.EqualTo(1), "a missing input must still stage a marked change so the worker can reassert a sticky value");
            Assert.That(changes[0].PendingGeneration!.BaseValue, Is.Null);
            Assert.That(changes[0].PendingGeneration!.BaseUnavailable, Is.True);
        }
    }

    [Test]
    public void ComputeAttributeValueChanges_GeneratedMappingNoBaseExpression_StagesMarkedChangeWithNullBase()
    {
        BuildGeneratedMapping(baseExpression: null); // valid for Sequence/Random tokens
        var mvo = BuildMvoWithEmployeeId("E123");

        var changes = _engine.ComputeAttributeValueChanges(
            mvo, _exportRule, changedAttributes: [], changeType: PendingExportChangeType.Update,
            existingCso: null, csoAttributeCache: null, out _);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes, Has.Count.EqualTo(1));
            Assert.That(changes[0].PendingGeneration!.BaseValue, Is.Null);
            Assert.That(changes[0].PendingGeneration!.BaseUnavailable, Is.False);
        }
    }

    [Test]
    public void ComputeAttributeValueChanges_GeneratedMappingFailMapping_StagesNothingAndRecordsError()
    {
        BuildGeneratedMapping("Lower(mv[\"EmployeeId\"])", MissingInputBehaviour.FailMapping);
        var mvo = BuildMvoWithEmployeeId(null);
        var flowErrors = new List<AttributeFlowError>();

        var changes = _engine.ComputeAttributeValueChanges(
            mvo, _exportRule, changedAttributes: [], changeType: PendingExportChangeType.Update,
            existingCso: null, csoAttributeCache: null, out _, flowErrors: flowErrors);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes, Is.Empty, "FailMapping stages nothing for this attribute, exactly as an ordinary mapping does");
            Assert.That(flowErrors, Has.Count.EqualTo(1));
            Assert.That(flowErrors[0].Kind, Is.EqualTo(AttributeFlowErrorKind.ExpressionMissingInput));
            Assert.That(flowErrors[0].TargetAttributeName, Is.EqualTo(_csoLoginNameAttribute.Name));
        }
    }

    [Test]
    public void ComputeAttributeValueChanges_GeneratedMappingFailObject_ThrowsMissingInputException()
    {
        BuildGeneratedMapping("Lower(mv[\"EmployeeId\"])", MissingInputBehaviour.FailObject);
        var mvo = BuildMvoWithEmployeeId(null);

        Assert.That(() => _engine.ComputeAttributeValueChanges(
                mvo, _exportRule, changedAttributes: [], changeType: PendingExportChangeType.Update,
                existingCso: null, csoAttributeCache: null, out _),
            Throws.TypeOf<SyncExpressionMissingInputException>());
    }

    [Test]
    public void ComputeAttributeValueChanges_NoNetChangeCacheShowsSameValue_StillStagesTheMarkedChange()
    {
        // Decision 6: no-net-change detection cannot decide a pending generation (the value is unknown at
        // engine time), so it must never skip or compare a generated mapping's marked change - even when the
        // Connected System Object cache already holds a value that, coincidentally, is the base value the
        // mapping would evaluate.
        BuildGeneratedMapping("Lower(mv[\"EmployeeId\"])");
        var mvo = BuildMvoWithEmployeeId("E123");
        var existingCso = new ConnectedSystemObject { Id = Guid.NewGuid(), Type = _csoType, TypeId = _csoType.Id };

        var cachedValues = new List<ConnectedSystemObjectAttributeValue>
        {
            new()
            {
                Id = Guid.NewGuid(),
                ConnectedSystemObject = existingCso,
                Attribute = _csoLoginNameAttribute,
                AttributeId = _csoLoginNameAttribute.Id,
                StringValue = "e123"
            }
        };
        var csoAttributeCache = cachedValues.ToLookup(v => (v.ConnectedSystemObject.Id, v.AttributeId));

        var changes = _engine.ComputeAttributeValueChanges(
            mvo, _exportRule, changedAttributes: [], changeType: PendingExportChangeType.Update,
            existingCso: existingCso, csoAttributeCache: csoAttributeCache, out var csoAlreadyCurrentCount);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes, Has.Count.EqualTo(1), "no-net-change detection must never skip a marked change");
            Assert.That(csoAlreadyCurrentCount, Is.EqualTo(0));
            Assert.That(changes[0].PendingGeneration, Is.Not.Null);
        }
    }
}
