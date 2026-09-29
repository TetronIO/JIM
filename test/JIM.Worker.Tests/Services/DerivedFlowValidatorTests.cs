// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Exceptions;
using JIM.Application.Expressions;
using JIM.Application.Services;
using JIM.Models.Expressions;
using JIM.Models.Logic;
using NUnit.Framework;
using static JIM.Worker.Tests.Services.DerivedFlowTestModel;

namespace JIM.Worker.Tests.Services;

/// <summary>
/// Save-time validation of Metaverse-Derived Attribute Flows (#1750, FR 2 and FR 12; plan decisions 14 and 15):
/// errors for a dependency cycle (naming every attribute and Synchronisation Rule on it), an <c>mv</c> name that is not
/// an attribute of the rule's Metaverse Object Type, and a Reference-typed input or target (#1861); a non-blocking
/// warning for a derived expression calling a function that returns a different value each time.
/// </summary>
[TestFixture]
public class DerivedFlowValidatorTests
{
    private DerivedFlowTestModel _model = null!;

    [SetUp]
    public void SetUp()
    {
        _model = new DerivedFlowTestModel();
    }

    private DerivedFlowValidationResult Validate(IReadOnlyCollection<SyncRuleMapping> proposed, params SyncRule[] rules) =>
        DerivedFlowValidator.Validate(new DerivedFlowGraph(rules, _model.Types, DerivedFlowGraphScope.AllMappings), proposed);

    // ---- cycles (Scenario 3) ----

    [Test]
    public void Validate_TwoRuleCycle_SavingEitherRule_NamesBothAttributesAndBothRules()
    {
        var hr = ImportRule(1, "HR Import", connectedSystemId: 1);
        var ad = ImportRule(2, "AD Import", connectedSystemId: 2);
        var displayName = Expression(hr, 101, _model.DisplayName, "mv[\"Mail Nickname\"]");
        var mailNickname = Expression(ad, 102, _model.MailNickname, "mv[\"Display Name\"]");

        var savingHr = Validate([displayName], hr, ad);
        var savingAd = Validate([mailNickname], hr, ad);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(savingHr.Errors, Is.EqualTo(new[]
            {
                "Saving would create a dependency cycle: Display Name (Synchronisation Rule 'HR Import') reads Mail Nickname, " +
                "which (Synchronisation Rule 'AD Import') reads Display Name."
            }));
            Assert.That(savingAd.Errors, Is.EqualTo(new[]
            {
                "Saving would create a dependency cycle: Mail Nickname (Synchronisation Rule 'AD Import') reads Display Name, " +
                "which (Synchronisation Rule 'HR Import') reads Mail Nickname."
            }), "the message starts from the flow being saved, whichever rule that is");
        }
    }

    [Test]
    public void Validate_ThreeNodeCycle_NamesEveryAttributeAndRule()
    {
        var hr = ImportRule(1, "HR Import", connectedSystemId: 1);
        var ad = ImportRule(2, "AD Import", connectedSystemId: 2);
        var training = ImportRule(3, "Training Import", connectedSystemId: 3);
        var email = Expression(hr, 101, _model.Email, "mv[\"Display Name\"] + \"@corp.local\"");
        Expression(ad, 102, _model.DisplayName, "mv[\"User Principal Name\"]");
        Expression(training, 103, _model.UserPrincipalName, "mv[\"Email\"]");

        var result = Validate([email], hr, ad, training);

        Assert.That(result.Errors, Is.EqualTo(new[]
        {
            "Saving would create a dependency cycle: Email (Synchronisation Rule 'HR Import') reads Display Name, " +
            "which (Synchronisation Rule 'AD Import') reads User Principal Name, " +
            "which (Synchronisation Rule 'Training Import') reads Email."
        }));
    }

    [Test]
    public void Validate_SelfReference_IsRejectedNamingTheAttributeAndRule()
    {
        var hr = ImportRule(1, "HR Import", connectedSystemId: 1);
        var displayName = Expression(hr, 101, _model.DisplayName, "mv[\"Display Name\"] + cs[\"suffix\"]");

        var result = Validate([displayName], hr);

        Assert.That(result.Errors, Is.EqualTo(new[]
        {
            "Saving would create a dependency cycle: Display Name (Synchronisation Rule 'HR Import') reads Display Name."
        }));
    }

    [Test]
    public void Validate_CycleNotInvolvingTheProposal_IsNotReportedAgainstIt()
    {
        var hr = ImportRule(1, "HR Import", connectedSystemId: 1);
        var ad = ImportRule(2, "AD Import", connectedSystemId: 2);
        Expression(hr, 101, _model.DisplayName, "mv[\"Mail Nickname\"]");
        Expression(ad, 102, _model.MailNickname, "mv[\"Display Name\"]");
        var email = Expression(ad, 103, _model.Email, "mv[\"Account Name\"] + \"@corp.local\"");

        var result = Validate([email], hr, ad);

        Assert.That(result.Errors, Is.Empty, "an unrelated save must not be blocked by other flows' configuration");
    }

    // ---- unknown names ----

    [Test]
    public void Validate_UnknownMetaverseName_IsRejectedNamingTheNameAndType()
    {
        var hr = ImportRule(1, "HR Import", connectedSystemId: 1);
        var email = Expression(hr, 101, _model.Email, "mv[\"Acount Name\"] + \"@corp.local\"");

        var result = Validate([email], hr);

        Assert.That(result.Errors, Is.EqualTo(new[]
        {
            "The Attribute Flow to Email (Synchronisation Rule 'HR Import') reads mv[\"Acount Name\"], but 'Acount Name' is not " +
            "an attribute of the Metaverse Object Type 'Person'."
        }));
    }

    // ---- Reference inputs and targets (#1861) ----

    [Test]
    public void Validate_ReferenceInput_IsRejectedAsNotSupportedYet()
    {
        var hr = ImportRule(1, "HR Import", connectedSystemId: 1);
        var displayName = Expression(hr, 101, _model.DisplayName, "mv[\"First Name\"] + \" (\" + mv[\"Manager\"] + \")\"");

        var result = Validate([displayName], hr);

        Assert.That(result.Errors, Is.EqualTo(new[]
        {
            "The Attribute Flow to Display Name (Synchronisation Rule 'HR Import') reads Manager, a Reference attribute; " +
            "reading a Reference attribute in an Attribute Flow that derives a Metaverse attribute is not supported yet."
        }));
    }

    [Test]
    public void Validate_ReferenceTarget_IsRejectedAsNotSupportedYet()
    {
        var hr = ImportRule(1, "HR Import", connectedSystemId: 1);
        var manager = Expression(hr, 101, _model.Manager, "mv[\"Region\"]");

        var result = Validate([manager], hr);

        Assert.That(result.Errors, Is.EqualTo(new[]
        {
            "The Attribute Flow to Manager (Synchronisation Rule 'HR Import') reads Metaverse attributes, but Manager is a " +
            "Reference attribute; deriving a Reference attribute from Metaverse attributes is not supported yet."
        }));
    }

    [Test]
    public void Validate_ReferenceTargetWithoutMetaverseInputs_IsUnaffected()
    {
        var hr = ImportRule(1, "HR Import", connectedSystemId: 1);
        var manager = Expression(hr, 101, _model.Manager, "cs[\"manager\"]");

        var result = Validate([manager], hr);

        Assert.That(result.Errors, Is.Empty);
    }

    // ---- non-repeatable functions (decision 15) ----

    [TestCase("mv[\"Account Name\"] + FormatDate(Now(), \"yyyy\")", "Now()")]
    [TestCase("mv[\"Account Name\"] + FormatDate(Today(), \"yyyy\")", "Today()")]
    [TestCase("mv[\"Account Name\"] + RandomPassword(8, false)", "RandomPassword()")]
    [TestCase("mv[\"Account Name\"] + RandomPassphrase(3, \"-\")", "RandomPassphrase()")]
    [TestCase("mv[\"Account Name\"] + DateTime.UtcNow.Year", "DateTime.UtcNow")]
    [TestCase("mv[\"Account Name\"] + DateTime.Now.Year", "DateTime.Now")]
    [TestCase("mv[\"Account Name\"] + Guid.NewGuid().ToString()", "Guid.NewGuid()")]
    public void Validate_DerivedExpressionCallingANonRepeatableFunction_WarnsWithoutBlocking(string expression, string function)
    {
        var hr = ImportRule(1, "HR Import", connectedSystemId: 1);
        var email = Expression(hr, 101, _model.Email, expression);

        var result = Validate([email], hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Errors, Is.Empty);
            Assert.That(result.Warnings, Has.Count.EqualTo(1));
            Assert.That(result.Warnings[0].Mapping, Is.SameAs(email));
            Assert.That(result.Warnings[0].Message, Is.EqualTo(
                $"The Attribute Flow to Email (Synchronisation Rule 'HR Import') derives its value from Metaverse attributes and calls {function}, " +
                "which returns a different value each time it is evaluated; the value will change on every synchronisation and can cause repeated exports."));
        }
    }

    [TestCase("Now()")]
    [TestCase("Today()")]
    [TestCase("RandomPassword(8, false)")]
    [TestCase("RandomPassphrase(3, \"-\")")]
    [TestCase("DateTime.UtcNow")]
    [TestCase("DateTime.Now")]
    [TestCase("Guid.NewGuid()")]
    public void NonRepeatableConstructs_AreGenuinelyCallableInExpressions(string expression)
    {
        // Guards the warning list against drifting from the evaluator: a construct the evaluator cannot run would be
        // a warning about nothing.
        var evaluator = new DynamicExpressoEvaluator();

        Assert.That(() => evaluator.Evaluate(expression, new ExpressionContext(null, null)), Throws.Nothing);
    }

    [Test]
    public void Validate_NonRepeatableFunctionInAStringLiteral_DoesNotWarn()
    {
        var hr = ImportRule(1, "HR Import", connectedSystemId: 1);
        var email = Expression(hr, 101, _model.Email, "mv[\"Account Name\"] + \" Now() \"");

        var result = Validate([email], hr);

        Assert.That(result.Warnings, Is.Empty);
    }

    [Test]
    public void Validate_NonRepeatableFunctionOnAFlowThatIsNotDerived_DoesNotWarn()
    {
        var hr = ImportRule(1, "HR Import", connectedSystemId: 1);
        var stamp = Expression(hr, 101, _model.Region, "cs[\"region\"] + FormatDate(Now(), \"yyyy\")");

        var result = Validate([stamp], hr);

        Assert.That(result.Warnings, Is.Empty, "the warning is about derived flows (decision 15); ordinary flows are out of scope");
    }

    [Test]
    public void Validate_SeveralNonRepeatableFunctions_NamesEachOnce()
    {
        var hr = ImportRule(1, "HR Import", connectedSystemId: 1);
        var email = Expression(hr, 101, _model.Email, "mv[\"Account Name\"] + FormatDate(Now(), \"yyyy\") + FormatDate(Now(), \"MM\") + RandomPassword(4, false)");

        var result = Validate([email], hr);

        Assert.That(result.Warnings, Has.Count.EqualTo(1));
        Assert.That(result.Warnings[0].Message, Does.Contain("calls Now() and RandomPassword(), which return"));
    }

    // ---- composition ----

    [Test]
    public void Validate_ValidDerivedFlow_HasNoErrorsOrWarnings()
    {
        var hr = ImportRule(1, "HR Import", connectedSystemId: 1);
        Direct(hr, 100, _model.AccountName, "sAMAccountName");
        var email = Expression(hr, 101, _model.Email, "mv[\"Account Name\"] + \"@corp.local\"");

        var result = Validate([email], hr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Errors, Is.Empty);
            Assert.That(result.Warnings, Is.Empty);
            Assert.That(result.HasErrors, Is.False);
        }
    }

    [Test]
    public void Exception_IsAnArgumentExceptionCarryingEveryErrorInOneMessage()
    {
        var errors = new[] { "First problem.", "Second problem." };

        var exception = new DerivedFlowValidationException(errors);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception, Is.InstanceOf<ArgumentException>(), "REST maps ArgumentException to 400 and the portal treats it as user-safe");
            Assert.That(exception.Message, Is.EqualTo("First problem. Second problem."));
            Assert.That(exception.Errors, Is.EqualTo(errors));
        }
    }
}
