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
/// Contributor re-election (#91, #1537) and Metaverse-Derived Attribute Flows (#1750, plan decision 5): re-election
/// re-flows a surviving Connected System Object through <c>FlowInboundAttributes</c>, so a derived mapping on the
/// survivor's rule is skipped there exactly as in the ordinary pass. A derived flow reads the Metaverse view of a
/// derived pass, which a re-election re-flow does not have; it is re-evaluated by its hosting system's own
/// synchronisation instead.
/// </summary>
[TestFixture]
public class ContributorReElectionDerivedFlowTests
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

    /// <summary>
    /// Email contributed by AD at priority 1 and derived from Account Name on the HR rule at priority 2; AD's
    /// Connected System Object is leaving, so Email is recalled and HR's is the surviving contributor.
    /// </summary>
    private async Task<MetaverseObject> ReElectAsync(bool withGraph)
    {
        var hr = ImportRule(1, "HR Import", HrSystemId);
        Expression(hr, 101, _model.Email, "mv[\"Account Name\"] + \"@corp.local\"", priority: 2);
        var ad = ImportRule(2, "AD Import", AdSystemId);
        Expression(ad, 201, _model.Email, "cs[\"region\"]", priority: 1);
        SyncRule[] rules = [hr, ad];

        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = _model.Person };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            MetaverseObject = mvo, Attribute = _model.AccountName, AttributeId = _model.AccountName.Id, StringValue = "jbloggs",
            ContributedBySyncRuleId = 1, ContributedBySystemId = HrSystemId
        });
        var recalled = new MetaverseObjectAttributeValue
        {
            MetaverseObject = mvo, Attribute = _model.Email, AttributeId = _model.Email.Id, StringValue = "joe@ad.local",
            ContributedBySyncRuleId = 2, ContributedBySystemId = AdSystemId
        };
        mvo.AttributeValues.Add(recalled);
        mvo.PendingAttributeValueRemovals.Add(recalled);

        var leaver = new ConnectedSystemObject { Id = Guid.NewGuid(), ConnectedSystemId = AdSystemId, TypeId = 1, Type = _csoType, MetaverseObject = mvo };
        var survivor = new ConnectedSystemObject { Id = Guid.NewGuid(), ConnectedSystemId = HrSystemId, TypeId = 1, Type = _csoType, MetaverseObject = mvo };
        survivor.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = CsRegionId, StringValue = "emea" });

        var syncRepository = new Mock<ISyncRepository>();
        syncRepository.Setup(r => r.GetConnectedSystemObjectsByMetaverseObjectIdAsync(mvo.Id)).ReturnsAsync([leaver, survivor]);

        var context = withGraph
            ? new AttributePriorityContext(rules, honourNullAssertions: true,
                derivedFlowGraph: new DerivedFlowGraph(rules, _model.Types, DerivedFlowGraphScope.EnabledMappingsOnly))
            : new AttributePriorityContext(rules, honourNullAssertions: true);

        await ContributorReElectionService.ReElectSurvivingContributorsAsync(
            mvo, [recalled], ContributorRecallScope.ForObsoletingConnectedSystemObject(leaver), context,
            new Application.Servers.SyncEngine(), syncRepository.Object, (_, _) => true, [_csoType], new DynamicExpressoEvaluator());

        return mvo;
    }

    [Test]
    public async Task ReElectSurvivingContributorsAsync_GraphPresent_DoesNotEvaluateTheSurvivorsDerivedFlowAsync()
    {
        var mvo = await ReElectAsync(withGraph: true);

        Assert.That(mvo.PendingAttributeValueAdditions.Where(av => av.AttributeId == _model.Email.Id), Is.Empty);
    }

    [Test]
    public async Task ReElectSurvivingContributorsAsync_GraphNull_ReFlowsTheSurvivorAsBeforeTheFeatureAsync()
    {
        // The control: without a graph the same survivor is re-flowed and its expression evaluated (reading nothing
        // from mv, as before the feature), so the test above is not passing vacuously.
        var mvo = await ReElectAsync(withGraph: false);

        Assert.That(mvo.PendingAttributeValueAdditions.Count(av => av.AttributeId == _model.Email.Id), Is.EqualTo(1));
    }
}
