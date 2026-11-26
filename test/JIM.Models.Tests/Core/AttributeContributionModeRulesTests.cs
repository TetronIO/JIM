// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using NUnit.Framework;

namespace JIM.Models.Tests.Core;

/// <summary>
/// The one rule for which contribution modes an attribute can use (#3059), shared by the server's validation and the
/// portal's attribute editor.
/// </summary>
[TestFixture]
public class AttributeContributionModeRulesTests
{
    [TestCase(AttributeDataType.Text, AttributePlurality.SingleValued)]
    [TestCase(AttributeDataType.Reference, AttributePlurality.MultiValued)]
    [TestCase(AttributeDataType.Number, AttributePlurality.MultiValued)]
    public void IsPermitted_Priority_IsAlwaysPermitted(AttributeDataType type, AttributePlurality plurality) =>
        Assert.That(AttributeContributionModeRules.IsPermitted(AttributeContributionMode.Priority, type, plurality), Is.True);

    [TestCase(AttributeDataType.Text, AttributePlurality.MultiValued, true)]
    [TestCase(AttributeDataType.Guid, AttributePlurality.MultiValued, true)]
    [TestCase(AttributeDataType.Text, AttributePlurality.SingleValued, false)]
    [TestCase(AttributeDataType.Reference, AttributePlurality.MultiValued, false)]
    public void IsPermitted_Accumulate_NeedsAMultiValuedNonReferenceAttribute(AttributeDataType type, AttributePlurality plurality, bool expected) =>
        Assert.That(AttributeContributionModeRules.IsPermitted(AttributeContributionMode.Accumulate, type, plurality), Is.EqualTo(expected));

    [TestCase(AttributeDataType.Text, AttributePlurality.MultiValued, true)]
    [TestCase(AttributeDataType.Number, AttributePlurality.MultiValued, true)]
    [TestCase(AttributeDataType.Text, AttributePlurality.SingleValued, false)]
    [TestCase(AttributeDataType.Reference, AttributePlurality.MultiValued, false)]
    public void IsPermitted_Merge_NeedsAMultiValuedNonReferenceAttribute(AttributeDataType type, AttributePlurality plurality, bool expected) =>
        Assert.That(AttributeContributionModeRules.IsPermitted(AttributeContributionMode.Merge, type, plurality), Is.EqualTo(expected));

    [Test]
    public void Constrain_AModeTheAttributeCannotUse_FallsBackToPriority() =>
        Assert.That(AttributeContributionModeRules.Constrain(AttributeContributionMode.Accumulate, AttributeDataType.Text, AttributePlurality.SingleValued),
            Is.EqualTo(AttributeContributionMode.Priority));

    [Test]
    public void Constrain_APermittedMode_IsKept() =>
        Assert.That(AttributeContributionModeRules.Constrain(AttributeContributionMode.Accumulate, AttributeDataType.Text, AttributePlurality.MultiValued),
            Is.EqualTo(AttributeContributionMode.Accumulate));

    [TestCase(AttributeContributionMode.Accumulate, AttributeContributionMode.Priority, true)]
    [TestCase(AttributeContributionMode.Accumulate, AttributeContributionMode.Merge, true)]
    [TestCase(AttributeContributionMode.Merge, AttributeContributionMode.Accumulate, false)]
    [TestCase(AttributeContributionMode.Priority, AttributeContributionMode.Merge, false)]
    [TestCase(AttributeContributionMode.Accumulate, AttributeContributionMode.Accumulate, false)]
    [TestCase(AttributeContributionMode.Priority, AttributeContributionMode.Accumulate, false)]
    [TestCase(AttributeContributionMode.Priority, AttributeContributionMode.Priority, false)]
    public void CanRemoveValues_OnlyLeavingAccumulateCanRemoveValuesSynchronisationKept(
        AttributeContributionMode from, AttributeContributionMode to, bool expected) =>
        Assert.That(AttributeContributionModeRules.CanRemoveValues(from, to), Is.EqualTo(expected));
}
