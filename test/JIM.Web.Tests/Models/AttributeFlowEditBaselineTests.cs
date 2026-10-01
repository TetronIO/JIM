// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests.Models;

/// <summary>
/// The Attribute Flow dialog edits its mapping in place, so the editor captures the mapping as it opened to ask, on
/// Update, which Metaverse-Derived Attribute Flows the edit would leave with a missing input (#1750, FR 3). These pin
/// which edits are worth asking about and that the capture is detached from the mapping it copies.
/// </summary>
[TestFixture]
public class AttributeFlowEditBaselineTests
{
    private static readonly MetaverseAttribute AccountName = new() { Id = 10, Name = "Account Name", Type = AttributeDataType.Text };
    private static readonly MetaverseAttribute Email = new() { Id = 11, Name = "Email", Type = AttributeDataType.Text };

    private static SyncRuleMapping ExpressionMapping()
    {
        var mapping = new SyncRuleMapping { Id = 88, SyncRuleId = 12, TargetMetaverseAttribute = AccountName, TargetMetaverseAttributeId = AccountName.Id };
        mapping.Sources.Add(new SyncRuleMappingSource { Order = 0, Expression = "Lower(cs[\"firstName\"])" });
        return mapping;
    }

    [Test]
    public void MayTakeAContributionAway_NothingChanged_IsFalse()
    {
        var mapping = ExpressionMapping();
        var baseline = AttributeFlowEditBaseline.Capture(mapping);

        Assert.That(baseline.MayTakeAContributionAway(mapping), Is.False);
    }

    [Test]
    public void MayTakeAContributionAway_EnabledCleared_IsTrue()
    {
        var mapping = ExpressionMapping();
        var baseline = AttributeFlowEditBaseline.Capture(mapping);
        mapping.Enabled = false;

        Assert.That(baseline.MayTakeAContributionAway(mapping), Is.True);
    }

    [Test]
    public void MayTakeAContributionAway_ReEnabled_IsFalse()
    {
        var mapping = ExpressionMapping();
        mapping.Enabled = false;
        var baseline = AttributeFlowEditBaseline.Capture(mapping);
        mapping.Enabled = true;

        Assert.That(baseline.MayTakeAContributionAway(mapping), Is.False, "turning a flow on only ever adds a contribution");
    }

    [Test]
    public void MayTakeAContributionAway_RetargetedThroughTheNavigation_IsTrue()
    {
        var mapping = ExpressionMapping();
        var baseline = AttributeFlowEditBaseline.Capture(mapping);
        mapping.TargetMetaverseAttribute = Email;

        Assert.That(baseline.MayTakeAContributionAway(mapping), Is.True);
    }

    [Test]
    public void MayTakeAContributionAway_ExpressionRewritten_IsTrue()
    {
        var mapping = ExpressionMapping();
        var baseline = AttributeFlowEditBaseline.Capture(mapping);
        mapping.Sources[0].Expression = "mv[\"Email\"]";

        Assert.That(baseline.MayTakeAContributionAway(mapping), Is.True);
    }

    [Test]
    public void MayTakeAContributionAway_ValueProcessingChanged_IsFalse()
    {
        var mapping = ExpressionMapping();
        var baseline = AttributeFlowEditBaseline.Capture(mapping);
        mapping.CaseNormalisation = InboundCaseNormalisation.Upper;
        mapping.NullIsValue = true;

        Assert.That(baseline.MayTakeAContributionAway(mapping), Is.False);
    }

    [Test]
    public void Capture_LaterInPlaceEdits_DoNotReachTheCopy()
    {
        var mapping = ExpressionMapping();
        var baseline = AttributeFlowEditBaseline.Capture(mapping);

        mapping.Enabled = false;
        mapping.TargetMetaverseAttribute = Email;
        mapping.Sources[0].Expression = "mv[\"Email\"]";

        using (Assert.EnterMultipleScope())
        {
            Assert.That(baseline.AsOpened.Id, Is.EqualTo(88));
            Assert.That(baseline.AsOpened.Enabled, Is.True);
            Assert.That(baseline.AsOpened.ResolveTargetMetaverseAttributeId(), Is.EqualTo(AccountName.Id));
            Assert.That(baseline.AsOpened.Sources.Single().Expression, Is.EqualTo("Lower(cs[\"firstName\"])"));
        }
    }
}
