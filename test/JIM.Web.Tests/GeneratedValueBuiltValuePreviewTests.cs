// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Models.Logic;
using JIM.Web;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The generated flow's preview works out the values built from each candidate that are checked with it (Generated
/// Value Forms, #2023, PRD requirement 11), so an administrator sees the Email and User Principal Name each candidate
/// would carry. See <c>GeneratedValueBuiltValuePreview</c>.
/// </summary>
[TestFixture]
public class GeneratedValueBuiltValuePreviewTests
{
    private readonly DynamicExpressoEvaluator _evaluator = new();

    private static GeneratedValueBuiltValueDescription Value(string name, string expression, IReadOnlyList<string>? via = null,
        bool checkedBeforeIssuing = true, bool exportExpression = false) => new()
    {
        GeneratedAttributeName = "Account Name",
        BuiltFromAttributeNames = ["Account Name"],
        MetaverseAttributeName = exportExpression ? null : name,
        ConnectedSystemAttributeName = exportExpression ? name : null,
        Via = via ?? [],
        Kind = GeneratedValueBuiltValueKind.Form,
        KeepUnique = true,
        CheckedBeforeIssuing = checkedBeforeIssuing,
        Expression = expression
    };

    private static readonly IReadOnlyDictionary<string, string?> NoSamples = new Dictionary<string, string?>();

    [Test]
    public void WorkOut_UserPrincipalNameBuiltThroughEmail_WorksEachOutInTurn()
    {
        var values = new[]
        {
            Value("User Principal Name", "mv[\"Email\"]", via: ["Email"]),
            Value("Email", "Lower(mv[\"Account Name\"]) + \"@panoply.local\"")
        };

        var worked = GeneratedValueBuiltValuePreview.WorkOut("joe.bloggs", values, NoSamples, _evaluator);

        Assert.That(worked.Select(v => (v.Name, v.Value)), Is.EqualTo(new[]
        {
            ("Email", "joe.bloggs@panoply.local"),
            ("User Principal Name", "joe.bloggs@panoply.local")
        }), "Email first, since the User Principal Name reads it");
    }

    [Test]
    public void WorkOut_ValueNotCheckedBeforeIssuing_IsLeftOut()
    {
        var values = new[]
        {
            Value("Email", "mv[\"Account Name\"] + \"@panoply.local\""),
            Value("Mailbox Address", "mv[\"Account Name\"] + \"@mail.panoply.local\"", checkedBeforeIssuing: false)
        };

        var worked = GeneratedValueBuiltValuePreview.WorkOut("joe.bloggs", values, NoSamples, _evaluator);

        Assert.That(worked.Select(v => v.Name), Is.EqualTo(new[] { "Email" }));
    }

    [Test]
    public void WorkOut_ExportExpression_IsWorkedOutUnderItsConnectedSystemAttribute()
    {
        var worked = GeneratedValueBuiltValuePreview.WorkOut("joe.bloggs",
            [Value("sAMAccountName", "Left(mv[\"Account Name\"], 6)", exportExpression: true)], NoSamples, _evaluator);

        Assert.That(worked.Select(v => (v.Name, v.Value)), Is.EqualTo(new[] { ("sAMAccountName", "joe.bl") }));
    }

    [Test]
    public void WorkOut_InputNoSampleGives_ReadsItsNameAsThePlaceholder()
    {
        // "Test this Expression" offers only the base expression's inputs, so a Region read only by Email has no sample.
        var values = new[] { Value("Email", "mv[\"Account Name\"] + \"@\" + Lower(mv[\"Region\"]) + \".panoply.local\"") };

        var worked = GeneratedValueBuiltValuePreview.WorkOut("joe.bloggs", values, NoSamples, _evaluator);

        Assert.That(worked.Single().Value, Is.EqualTo("joe.bloggs@region.panoply.local"));
    }

    [Test]
    public void WorkOut_SampleGiven_ReadsTheSample()
    {
        var values = new[] { Value("Email", "mv[\"Account Name\"] + \"@\" + Lower(cs[\"domain\"])") };
        var samples = new Dictionary<string, string?> { ["cs[\"domain\"]"] = "PANOPLY.LOCAL" };

        var worked = GeneratedValueBuiltValuePreview.WorkOut("joe.bloggs", values, samples, _evaluator);

        Assert.That(worked.Single().Value, Is.EqualTo("joe.bloggs@panoply.local"));
    }

    [Test]
    public void WorkOut_ExpressionFails_SaysSoForThatValueAndWorksOutTheRest()
    {
        var values = new[]
        {
            Value("Email", "mv[\"Account Name\"] +"),
            Value("Display Name", "Upper(mv[\"Account Name\"])")
        };

        var worked = GeneratedValueBuiltValuePreview.WorkOut("joe.bloggs", values, NoSamples, _evaluator);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(worked.Single(v => v.Name == "Email").Value, Is.Null);
            Assert.That(worked.Single(v => v.Name == "Email").Error, Is.Not.Null);
            Assert.That(worked.Single(v => v.Name == "Display Name").Value, Is.EqualTo("JOE.BLOGGS"));
        }
    }
}
