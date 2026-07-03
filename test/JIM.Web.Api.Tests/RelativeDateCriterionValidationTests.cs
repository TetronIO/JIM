// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Search;
using JIM.Web.Models.Api;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for the shared relative-date criterion validation used by both the scoping-criteria and
/// predefined-search-criteria endpoints.
/// </summary>
[TestFixture]
public class RelativeDateCriterionValidationTests
{
    [Test]
    public void Validate_AbsoluteByDefault_NoRelativeFields_Succeeds()
    {
        var error = RelativeDateCriterionValidation.Validate(
            valueMode: null, relativeCount: null, relativeUnit: null, relativeDirection: null,
            AttributeDataType.DateTime, hasAbsoluteDate: true,
            out var mode, out var count, out var unit, out var direction);

        Assert.That(error, Is.Null);
        Assert.That(mode, Is.EqualTo(DateCriteriaValueMode.Absolute));
        Assert.That(count, Is.Null);
        Assert.That(unit, Is.Null);
        Assert.That(direction, Is.Null);
    }

    [Test]
    public void Validate_AbsoluteWithRelativeFields_Fails()
    {
        var error = RelativeDateCriterionValidation.Validate(
            "Absolute", relativeCount: 7, relativeUnit: "Days", relativeDirection: "Ago",
            AttributeDataType.DateTime, hasAbsoluteDate: false,
            out _, out _, out _, out _);

        Assert.That(error, Does.Contain("must not be set for an absolute criterion"));
    }

    [Test]
    public void Validate_ValidRelative_Succeeds()
    {
        var error = RelativeDateCriterionValidation.Validate(
            "Relative", relativeCount: 30, relativeUnit: "Days", relativeDirection: "Ago",
            AttributeDataType.DateTime, hasAbsoluteDate: false,
            out var mode, out var count, out var unit, out var direction);

        Assert.That(error, Is.Null);
        Assert.That(mode, Is.EqualTo(DateCriteriaValueMode.Relative));
        Assert.That(count, Is.EqualTo(30));
        Assert.That(unit, Is.EqualTo(RelativeDateUnit.Days));
        Assert.That(direction, Is.EqualTo(RelativeDateDirection.Ago));
    }

    [Test]
    public void Validate_RelativeOnNonDateAttribute_Fails()
    {
        var error = RelativeDateCriterionValidation.Validate(
            "Relative", 7, "Days", "FromNow",
            AttributeDataType.Text, hasAbsoluteDate: false,
            out _, out _, out _, out _);

        Assert.That(error, Does.Contain("only valid for DateTime attributes"));
    }

    [Test]
    public void Validate_RelativeWithAbsoluteDateAlsoSet_Fails()
    {
        var error = RelativeDateCriterionValidation.Validate(
            "Relative", 7, "Days", "FromNow",
            AttributeDataType.DateTime, hasAbsoluteDate: true,
            out _, out _, out _, out _);

        Assert.That(error, Does.Contain("not both"));
    }

    [Test]
    public void Validate_RelativeMissingCount_Fails()
    {
        var error = RelativeDateCriterionValidation.Validate(
            "Relative", relativeCount: null, relativeUnit: "Days", relativeDirection: "Ago",
            AttributeDataType.DateTime, hasAbsoluteDate: false,
            out _, out _, out _, out _);

        Assert.That(error, Does.Contain("relativeCount is required"));
    }

    [Test]
    public void Validate_RelativeNegativeCount_Fails()
    {
        var error = RelativeDateCriterionValidation.Validate(
            "Relative", relativeCount: -1, relativeUnit: "Days", relativeDirection: "Ago",
            AttributeDataType.DateTime, hasAbsoluteDate: false,
            out _, out _, out _, out _);

        Assert.That(error, Does.Contain("zero or positive"));
    }

    [Test]
    public void Validate_RelativeMissingUnit_Fails()
    {
        var error = RelativeDateCriterionValidation.Validate(
            "Relative", 7, relativeUnit: null, relativeDirection: "Ago",
            AttributeDataType.DateTime, hasAbsoluteDate: false,
            out _, out _, out _, out _);

        Assert.That(error, Does.Contain("relativeUnit"));
    }

    [Test]
    public void Validate_RelativeMissingDirection_Fails()
    {
        var error = RelativeDateCriterionValidation.Validate(
            "Relative", 7, "Days", relativeDirection: null,
            AttributeDataType.DateTime, hasAbsoluteDate: false,
            out _, out _, out _, out _);

        Assert.That(error, Does.Contain("relativeDirection"));
    }

    [Test]
    public void Validate_InvalidValueMode_Fails()
    {
        var error = RelativeDateCriterionValidation.Validate(
            "Sometimes", null, null, null,
            AttributeDataType.DateTime, hasAbsoluteDate: false,
            out _, out _, out _, out _);

        Assert.That(error, Does.Contain("Invalid value mode"));
    }
}
