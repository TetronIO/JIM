// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Application.Services;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;
using static JIM.Worker.Tests.Services.DerivedFlowTestModel;

namespace JIM.Worker.Tests.Services;

/// <summary>
/// Contributor re-election binds each survivor it re-flows to the Metaverse Object in hand, and hydrates a survivor the
/// discovery load left thin. The run wants that binding; a preview, whose Metaverse Object is its own working copy, must
/// leave the repository's survivor instances as it found them (#1899).
/// </summary>
[TestFixture]
public class ContributorReElectionSurvivorIsolationTests
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
    public async Task ReElectSurvivingContributorsAsync_LeavingSurvivorsAsFound_RestoresTheSurvivorAndStillReElectsItAsync()
    {
        var scenario = BuildScenario();
        var valuesAsFound = scenario.Survivor.AttributeValues;

        await ReElectAsync(scenario, leaveSurvivorsAsFound: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scenario.WorkingMvo.PendingAttributeValueAdditions.Single(av => av.AttributeId == _model.Email.Id).StringValue,
                Is.EqualTo("emea"), "the survivor is still re-elected onto the working copy");
            Assert.That(scenario.Survivor.MetaverseObject, Is.SameAs(scenario.StoredMvo), "the survivor's link is put back");
            Assert.That(scenario.Survivor.AttributeValues, Is.SameAs(valuesAsFound), "the values the hydration loaded are put back");
            Assert.That(scenario.Survivor.Type, Is.Null, "the type the hydration loaded is put back");
        }
    }

    [Test]
    public async Task ReElectSurvivingContributorsAsync_ByDefault_LeavesTheSurvivorBoundToTheObjectInHandAsync()
    {
        // The run's behaviour, unchanged: the survivor is pinned to the page's one instance of the object.
        var scenario = BuildScenario();

        await ReElectAsync(scenario, leaveSurvivorsAsFound: false);

        Assert.That(scenario.Survivor.MetaverseObject, Is.SameAs(scenario.WorkingMvo));
    }

    private sealed record Scenario(
        MetaverseObject StoredMvo,
        MetaverseObject WorkingMvo,
        MetaverseObjectAttributeValue Recalled,
        ConnectedSystemObject Leaver,
        ConnectedSystemObject Survivor,
        SyncRule[] Rules,
        Mock<ISyncRepository> SyncRepository);

    /// <summary>
    /// Email contributed by AD at priority 1 and by HR at priority 2. AD's object is leaving, so Email is recalled from
    /// a working copy of the Metaverse Object, and HR's object, joined to the stored instance and loaded without its
    /// type or values, is the survivor.
    /// </summary>
    private Scenario BuildScenario()
    {
        var hr = ImportRule(1, "HR Import", HrSystemId);
        Expression(hr, 101, _model.Email, "cs[\"region\"]", priority: 2);
        var ad = ImportRule(2, "AD Import", AdSystemId);
        Expression(ad, 201, _model.Email, "cs[\"region\"]", priority: 1);

        var mvoId = Guid.NewGuid();
        var storedMvo = new MetaverseObject { Id = mvoId, Type = _model.Person };
        var workingMvo = new MetaverseObject { Id = mvoId, Type = _model.Person };
        var recalled = new MetaverseObjectAttributeValue
        {
            MetaverseObject = workingMvo, Attribute = _model.Email, AttributeId = _model.Email.Id, StringValue = "joe@ad.local",
            ContributedBySyncRuleId = 2, ContributedBySystemId = AdSystemId
        };
        workingMvo.AttributeValues.Add(recalled);
        workingMvo.PendingAttributeValueRemovals.Add(recalled);

        var leaver = new ConnectedSystemObject { Id = Guid.NewGuid(), ConnectedSystemId = AdSystemId, TypeId = 1, Type = _csoType, MetaverseObject = storedMvo };
        var survivor = new ConnectedSystemObject { Id = Guid.NewGuid(), ConnectedSystemId = HrSystemId, TypeId = 1, Type = null!, MetaverseObject = storedMvo };
        var hydrated = new ConnectedSystemObject { Id = survivor.Id, ConnectedSystemId = HrSystemId, TypeId = 1, Type = _csoType };
        hydrated.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = CsRegionId, StringValue = "emea" });

        var syncRepository = new Mock<ISyncRepository>();
        syncRepository.Setup(r => r.GetConnectedSystemObjectsByMetaverseObjectIdAsync(mvoId)).ReturnsAsync([leaver, survivor]);
        syncRepository.Setup(r => r.GetConnectedSystemObjectAsync(HrSystemId, survivor.Id)).ReturnsAsync(hydrated);

        return new Scenario(storedMvo, workingMvo, recalled, leaver, survivor, [hr, ad], syncRepository);
    }

    private async Task ReElectAsync(Scenario scenario, bool leaveSurvivorsAsFound)
    {
        await ContributorReElectionService.ReElectSurvivingContributorsAsync(
            scenario.WorkingMvo, [scenario.Recalled], ContributorRecallScope.ForObsoletingConnectedSystemObject(scenario.Leaver),
            new AttributePriorityContext(scenario.Rules, honourNullAssertions: true),
            new Application.Servers.SyncEngine(), scenario.SyncRepository.Object, (_, _) => true, [_csoType], new DynamicExpressoEvaluator(),
            leaveSurvivorsAsFound: leaveSurvivorsAsFound);
    }
}
