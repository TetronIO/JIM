// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Application.Expressions;
using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Localisation;
using JIM.Models.Logic;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Expressions;

/// <summary>
/// What expressions tell an administrator is held as messages (#3030): the British English every other reader (the REST
/// API, PowerShell, logs, the documentation) gets is exactly what it was, and the portal can render the same message in
/// another language.
/// </summary>
[TestFixture]
public class ExpressionMessageLocalisationTests
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr");

    private static string InFrench(JimMessage message) => MessageRenderer.Render(message, French, French);

    [Test]
    public void FunctionCatalogue_EveryFunction_HoldsItsTextAsMessagesWhoseEnglishIsTheText()
    {
        foreach (var function in ExpressionFunctionCatalogue.All)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(function.DescriptionDetail, Is.Not.Null, function.Name);
                Assert.That(function.DescriptionDetail!.ToEnglish(), Is.EqualTo(function.Description), function.Name);
                Assert.That(function.ReturnsDetail!.ToEnglish(), Is.EqualTo(function.Returns), function.Name);
                Assert.That(InFrench(function.DescriptionDetail!), Is.Not.EqualTo(function.Description), function.Name);
                Assert.That(function.Parameters.Select(p => p.DescriptionDetail!.ToEnglish()),
                    Is.EqualTo(function.Parameters.Select(p => p.Description)), function.Name);
            }
        }
    }

    [Test]
    public void FunctionCatalogue_ACategory_HasANameInFrench_AndTheEnglishTheReferenceUses()
    {
        var category = ExpressionFunctionCategory.DistinguishedName;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ExpressionFunctionCatalogue.CategoryName(category), Is.EqualTo("Distinguished Name (DN) Functions"));
            Assert.That(InFrench(ExpressionFunctionCatalogue.CategoryNameDetail(category)!), Is.EqualTo("Fonctions de nom distinctif (DN)"));
        }
    }

    [Test]
    public void ResultTypeCheck_AProvableMismatch_IsTheEnglishItWas_AndRendersInFrench()
    {
        var message = ExpressionResultTypeCheck.DescribeProblemMessage("HasValue(cs[\"manager\"])", AttributeDataType.DateTime, "Hire Date")!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(message.ToEnglish(), Is.EqualTo("HasValue returns true or false, which 'Hire Date' can never hold as a Date Time attribute."));
            Assert.That(ExpressionResultTypeCheck.DescribeProblem("HasValue(cs[\"manager\"])", AttributeDataType.DateTime, "Hire Date"), Is.EqualTo(message.ToEnglish()));
            Assert.That(InFrench(message), Is.EqualTo("HasValue renvoie vrai ou faux, ce que « Hire Date » ne peut jamais contenir comme attribut de type date et heure."));
        }
    }

    [Test]
    public void Evaluate_WhereWithAnUnknownOperator_ThrowsAnErrorWhoseMessageIsTheEnglishItWas_AndRendersInFrench()
    {
        var evaluator = new DynamicExpressoEvaluator();
        var context = new ExpressionContext(connectedSystemAttributes: new Dictionary<string, object?> { ["mail"] = new List<object?> { "a", "b" } });

        var error = Assert.Throws<ExpressionEvaluationException>(() => evaluator.Evaluate("Where(cs[\"mail\"], \"sounds like\", \"a\")", context))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(error.Message, Does.StartWith("Where does not know the operator 'sounds like' for text values. Use one of: "));
            Assert.That(error.Detail, Is.Not.Null);
            Assert.That(InFrench(error.Detail!), Does.StartWith("Where ne connaît pas l’opérateur « sounds like » pour des valeurs de type texte. Utilisez l’un des suivants : "));
        }
    }

    [Test]
    public void Test_ExpressionLongerThanTheLimit_KeepsItsEnglish_AndCarriesTheMessage()
    {
        var result = new DynamicExpressoEvaluator().Test(new string('a', 10001), new ExpressionContext());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ErrorMessage, Is.EqualTo("Expression exceeds the maximum permitted length of 10,000 characters (actual length: 10,001)."));
            // Numbers take the reader's grouping, so French writes 10 000.
            Assert.That(InFrench(result.ErrorDetail!), Does.StartWith("L’expression dépasse la longueur maximale autorisée de 10").And.Contain("caractères (longueur réelle : 10"));
        }
    }

    [Test]
    public void Evaluate_ParseDateOfANumber_FailsWithTheEnglishItAlwaysHad()
    {
        var evaluator = new DynamicExpressoEvaluator();
        var context = new ExpressionContext(connectedSystemAttributes: new Dictionary<string, object?> { ["when"] = 42 });

        var error = Assert.Throws<ExpressionEvaluationException>(() => evaluator.Evaluate("ParseDate(cs[\"when\"], \"dd/MM/yyyy\")", context))!;

        Assert.That(error.Message, Is.EqualTo("ParseDate cannot read a value of type 'Int32'. Expected text, or a date."));
    }

    [Test]
    public void Evaluate_AFunctionRefusingItsArgument_KeepsTheArgumentExceptionInside_AndCarriesTheMessage()
    {
        var evaluator = new DynamicExpressoEvaluator();
        var context = new ExpressionContext(connectedSystemAttributes: new Dictionary<string, object?> { ["when"] = true });

        var error = Assert.Throws<ExpressionEvaluationException>(() => evaluator.Evaluate("ToFileTime(cs[\"when\"])", context))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(error.InnerException, Is.TypeOf<ArgumentException>());
            Assert.That(error.Message, Is.EqualTo(
                "ToFileTime cannot convert value of type 'Boolean'. Expected a date, or text in ISO 8601 such as 2026-03-15T10:30:00Z."));
            Assert.That(InFrench(error.Detail!), Does.StartWith("ToFileTime ne peut pas convertir une valeur de type « Boolean »."));
        }
    }

    [Test]
    public void Describe_AResultTheTargetCannotHold_IsTheMessagesEnglish_AndRendersInFrench()
    {
        var evaluation = new AttributeFlowEvaluation
        {
            Source = ExpressionSourceEvaluation.Returned("Priya"),
            Outcome = AttributeFlowOutcome.Error,
            ErrorReason = AttributeFlowErrorReason.ResultNotConvertible,
            TargetType = AttributeDataType.LongNumber,
            Resolution = ExpressionResultResolution.NotConvertible("Priya")
        };

        var message = AttributeFlowTester.DescribeMessage(evaluation, "Employee Number");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(AttributeFlowTester.Describe(evaluation, "Employee Number"), Is.EqualTo(
                "The Expression returned 'Priya', which a LongNumber attribute cannot hold, so synchronisation records an error and 'Employee Number' keeps its current value."));
            Assert.That(InFrench(message), Is.EqualTo(
                "L’expression a renvoyé « Priya », qu’un attribut nombre long ne peut pas contenir ; la synchronisation enregistre une erreur et « Employee Number » conserve sa valeur actuelle."));
        }
    }

    [Test]
    public void Validator_AnExportReadingTheConnectedSystem_IsTheEnglishItWas_AndRendersInFrench()
    {
        var target = new ConnectedSystemObjectTypeAttribute { Id = 101, Name = "cn", Type = AttributeDataType.Text };
        var csType = new ConnectedSystemObjectType { Id = 7, Name = "user", Attributes = [target] };
        var mapping = new SyncRuleMapping { TargetConnectedSystemAttribute = target };
        mapping.Sources.Add(new SyncRuleMappingSource { Order = 0, Expression = "Upper(cs[\"sn\"])" });

        var problem = new AttributeFlowExpressionValidator(new DynamicExpressoEvaluator())
            .Validate(mapping, SyncRuleDirection.Export, csType, new MetaverseObjectType { Id = 1, Name = "User", Attributes = [] }).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(problem.Message, Is.EqualTo(
                "The expression for 'cn' reads cs[\"sn\"], but an export Attribute Flow reads the Metaverse Object, so it never has a value. Read mv[\"...\"] instead."));
            Assert.That(InFrench(problem.Detail), Is.EqualTo(
                "L’expression de « cn » lit cs[\"sn\"], mais un flux d’attributs d’exportation lit l’objet Metaverse ; elle n’a donc jamais de valeur. Lisez plutôt mv[\"...\"]."));
        }
    }

    [Test]
    public void TypedSamples_AValueOfTheWrongType_IsTheEnglishItWas_AndRendersInFrench()
    {
        var errors = new List<JimMessage>();

        TestSampleValues.Typed("cs[\"hireDate\"]", ["soon"], AttributeDataType.DateTime, AttributePlurality.SingleValued, errors);

        var message = errors.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(message.ToEnglish(), Is.EqualTo(
                "The value 'soon' for cs[\"hireDate\"] is not a DateTime value, the type of that attribute. Enter a date in ISO 8601, such as 2026-03-15 or 2026-03-15T10:30:00Z."));
            Assert.That(InFrench(message), Does.StartWith("La valeur « soon » pour cs[\"hireDate\"] n’est pas une valeur de type date et heure"));
        }
    }

    [Test]
    public void ExpressionText_ABinaryValue_IsCountedInTheReadersLanguage()
    {
        var one = ExpressionText.DescribeMessage(new byte[1])!;
        var several = ExpressionText.DescribeMessage(new byte[3])!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ExpressionText.Describe(new byte[3]), Is.EqualTo("(Binary value, 3 bytes)"));
            Assert.That(InFrench(one), Is.EqualTo("(Valeur binaire, 1 octet)"));
            Assert.That(InFrench(several), Is.EqualTo("(Valeur binaire, 3 octets)"));
        }
    }
}
