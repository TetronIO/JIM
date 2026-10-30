// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Exceptions;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;
using static JIM.Worker.Tests.Services.DerivedFlowTestModel;

namespace JIM.Worker.Tests.Services;

/// <summary>
/// The survivor-scope pre-flight (#2336) asks, before anything changes, whether re-election would meet a survivor whose
/// scope cannot be decided. It must cost nothing when no contributing rule's scope can fail, read only, and hand its
/// answers to re-election so the criteria are evaluated once.
/// </summary>
[TestFixture]
public class ContributorReElectionScopePreFlightTests
{
    private const int HrSystemId = 1;
    private const int AdSystemId = 2;
    private const int CsRegionId = 301;

    private DerivedFlowTestModel _model = null!;
    private ConnectedSystemObjectType _csoType = null!;

    [SetUp]
    public void SetUp()
    {
        _model = new DerivedFlowTestModel();
        _csoType = new ConnectedSystemObjectType
        {
            Id = 1,
            Name = "user",
            Attributes = [new ConnectedSystemObjectTypeAttribute { Id = CsRegionId, Name = "region", Type = AttributeDataType.Text }]
        };
    }

    [Test]
    public async Task FindUndecidableSurvivorScopeAsync_NoContributingRuleWithCriteria_ReadsNothingAndAsksNothingAsync()
    {
        var scenario = BuildScenario(survivorRuleScoped: false);
        var asked = 0;

        var undecidable = await FindAsync(scenario, (_, _) => { asked++; return true; }, new Dictionary<(Guid CsoId, int SyncRuleId), bool>());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(undecidable, Is.Null);
            Assert.That(asked, Is.Zero);
            scenario.SyncRepository.Verify(r => r.GetConnectedSystemObjectsByMetaverseObjectIdAsync(It.IsAny<Guid>()), Times.Never,
                "the usual case pays not even a query");
        }
    }

    [Test]
    public async Task FindUndecidableSurvivorScopeAsync_SurvivorsScopeUndecidable_ReturnsTheFailureAndLeavesTheSurvivorAsFoundAsync()
    {
        var scenario = BuildScenario(survivorRuleScoped: true);
        var failure = new ScopingCriterionFailedException("HR Import", "region", "it is held as Number");

        var undecidable = await FindAsync(scenario, (_, _) => throw failure, new Dictionary<(Guid CsoId, int SyncRuleId), bool>());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(undecidable, Is.SameAs(failure));
            Assert.That(scenario.Survivor.AttributeValues, Is.Empty, "the hydrated values are read, not written back");
            Assert.That(scenario.Survivor.Type, Is.Null);
        }
    }

    [Test]
    public async Task FindUndecidableSurvivorScopeAsync_SurvivorsScopeDecided_RecordsTheAnswerForReElectionAsync()
    {
        var scenario = BuildScenario(survivorRuleScoped: true);
        var answers = new Dictionary<(Guid CsoId, int SyncRuleId), bool>();
        ConnectedSystemObject? evaluated = null;

        var undecidable = await FindAsync(scenario, (cso, _) => { evaluated = cso; return false; }, answers);
        var asked = 0;
        var gate = ContributorReElectionService.AnsweringFrom(answers, (_, _) => { asked++; return true; });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(undecidable, Is.Null);
            Assert.That(evaluated?.AttributeValues.Single().StringValue, Is.EqualTo("emea"), "the survivor is evaluated on its full load");
            Assert.That(answers, Is.EqualTo(new Dictionary<(Guid, int), bool> { [(scenario.Survivor.Id, 1)] = false }));
            Assert.That(gate(scenario.Survivor, scenario.Rules[0]), Is.False, "re-election reads the pre-flight's answer");
            Assert.That(asked, Is.Zero, "rather than evaluating the criteria again");
        }
    }

    private sealed record Scenario(
        MetaverseObject Mvo,
        MetaverseObjectAttributeValue Recalled,
        ConnectedSystemObject Leaver,
        ConnectedSystemObject Survivor,
        SyncRule[] Rules,
        Mock<ISyncRepository> SyncRepository);

    /// <summary>
    /// Email contributed by AD at priority 1 and by HR at priority 2. AD's object is leaving, so Email is about to be
    /// recalled, and HR's object, loaded without its type or values, is the survivor.
    /// </summary>
    private Scenario BuildScenario(bool survivorRuleScoped)
    {
        var hr = ImportRule(1, "HR Import", HrSystemId);
        Expression(hr, 101, _model.Email, "cs[\"region\"]", priority: 2);
        hr.ConnectedSystemObjectType = _csoType;
        if (survivorRuleScoped)
        {
            hr.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
            {
                Type = SearchGroupType.All,
                Criteria = [new SyncRuleScopingCriteria { ConnectedSystemAttributeId = CsRegionId, ComparisonType = SearchComparisonType.HasValue }]
            });
        }
        var ad = ImportRule(2, "AD Import", AdSystemId);
        Expression(ad, 201, _model.Email, "cs[\"region\"]", priority: 1);

        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = _model.Person };
        var recalled = new MetaverseObjectAttributeValue
        {
            MetaverseObject = mvo, Attribute = _model.Email, AttributeId = _model.Email.Id, StringValue = "joe@ad.local",
            ContributedBySyncRuleId = 2, ContributedBySystemId = AdSystemId
        };
        mvo.AttributeValues.Add(recalled);

        var leaver = new ConnectedSystemObject { Id = Guid.NewGuid(), ConnectedSystemId = AdSystemId, TypeId = 1, Type = _csoType, MetaverseObject = mvo };
        var survivor = new ConnectedSystemObject { Id = Guid.NewGuid(), ConnectedSystemId = HrSystemId, TypeId = 1, Type = null!, MetaverseObject = mvo };
        var hydrated = new ConnectedSystemObject { Id = survivor.Id, ConnectedSystemId = HrSystemId, TypeId = 1, Type = _csoType };
        hydrated.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = CsRegionId, StringValue = "emea" });

        var syncRepository = new Mock<ISyncRepository>();
        syncRepository.Setup(r => r.GetConnectedSystemObjectsByMetaverseObjectIdAsync(mvo.Id)).ReturnsAsync([leaver, survivor]);
        syncRepository.Setup(r => r.GetConnectedSystemObjectWithAttributeValuesAsync(HrSystemId, survivor.Id)).ReturnsAsync(hydrated);

        return new Scenario(mvo, recalled, leaver, survivor, [hr, ad], syncRepository);
    }

    private static Task<ScopingCriterionFailedException?> FindAsync(Scenario scenario,
        Func<ConnectedSystemObject, SyncRule, bool> isInScope, IDictionary<(Guid CsoId, int SyncRuleId), bool> answers) =>
        ContributorReElectionService.FindUndecidableSurvivorScopeAsync(
            scenario.Mvo, [scenario.Recalled], ContributorRecallScope.ForObsoletingConnectedSystemObject(scenario.Leaver),
            new AttributePriorityContext(scenario.Rules, honourNullAssertions: true),
            scenario.SyncRepository.Object, isInScope, answers);
}
