// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Linq;
using JIM.Models.Core;
using JIM.Models.Logic.Scoping;
using JIM.Web.Models.Api;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// A scoping explanation over the REST API carries an expression criterion's detail (#2054): the expression, each
/// attribute it read with the object's values, and why it could not be evaluated, as the portal shows them.
/// </summary>
[TestFixture]
public class ScopingCriterionExplanationDtoTests
{
    [Test]
    public void FromModel_ExpressionCriterion_CarriesTheExpressionItsInputsAndTheReason()
    {
        var model = new ScopingCriterionExplanation
        {
            Path = "1.1",
            AttributeName = "cs[\"department\"]",
            AttributeType = AttributeDataType.Number,
            Outcome = ScopingCriterionOutcome.Invalid,
            Expression = "cs[\"department\"]",
            FailureReason = "Its expression computed 'Finance', which cannot be compared as a Number.",
            ExpressionInputs =
            [
                new ScopingExpressionInputExplanation { Accessor = "cs[\"department\"]", Values = ["Finance"], ValueCount = 1 },
                new ScopingExpressionInputExplanation { Accessor = "cs[\"userPassword\"]", ValueCount = 1, Masked = true }
            ]
        };

        var dto = ScopingCriterionExplanationDto.FromModel(model);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.Expression, Is.EqualTo("cs[\"department\"]"));
            Assert.That(dto.FailureReason, Is.EqualTo(model.FailureReason));
            Assert.That(dto.ExpressionInputs.Select(i => i.Accessor), Is.EqualTo(new[] { "cs[\"department\"]", "cs[\"userPassword\"]" }));
            Assert.That(dto.ExpressionInputs[0].Values, Is.EqualTo(new[] { "Finance" }));
            Assert.That(dto.ExpressionInputs[1].Masked, Is.True);
            Assert.That(dto.ExpressionInputs[1].ValueCount, Is.EqualTo(1));
        }
    }

    [Test]
    public void FromModel_AttributeCriterion_HasNoExpressionDetail()
    {
        var dto = ScopingCriterionExplanationDto.FromModel(new ScopingCriterionExplanation { AttributeName = "department" });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.Expression, Is.Null);
            Assert.That(dto.ExpressionInputs, Is.Empty);
            Assert.That(dto.FailureReason, Is.Null);
        }
    }
}
