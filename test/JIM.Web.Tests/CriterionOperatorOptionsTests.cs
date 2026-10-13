// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Search;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The criteria editors' operator lists come from the operator catalogue for the editor's context (#2053).
/// </summary>
[TestFixture]
public class CriterionOperatorOptionsTests
{
    [Test]
    public void ForType_PredefinedSearch_OffersNoPatternOperator()
    {
        var operators = CriterionOperatorOptions.ForType(AttributeDataType.Text, CriterionContext.PredefinedSearch).Select(o => o.Operator);

        Assert.That(operators, Does.Not.Contain(SearchComparisonType.MatchesPattern).And.Not.Contain(SearchComparisonType.NotMatchesPattern));
    }

    [Test]
    public void ForType_Scoping_OffersThePatternAndPresenceOperatorsWithTheirLabels()
    {
        var options = CriterionOperatorOptions.ForType(AttributeDataType.Text, CriterionContext.Scoping);

        Assert.That(options, Does.Contain(new CriterionOperatorOptions.OperatorOption(SearchComparisonType.MatchesPattern, "matches pattern"))
            .And.Contain(new CriterionOperatorOptions.OperatorOption(SearchComparisonType.HasNoValue, "has no value")));
    }

    [Test]
    public void DescribeCondition_ValueTakingOperator_IncludesTheValue()
    {
        Assert.That(CriterionOperatorOptions.DescribeCondition("Employee Type", SearchComparisonType.Equals, AttributeDataType.Text, "Contractor"),
            Is.EqualTo("Employee Type equals Contractor"));
    }

    [Test]
    public void DescribeCondition_PresenceOperator_LeavesOutTheStoredValue()
    {
        Assert.That(CriterionOperatorOptions.DescribeCondition("Leaving Date", SearchComparisonType.HasNoValue, AttributeDataType.DateTime, ""),
            Is.EqualTo("Leaving Date has no value"));
    }
}
