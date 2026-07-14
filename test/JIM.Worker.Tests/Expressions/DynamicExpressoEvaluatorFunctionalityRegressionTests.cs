// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Models.Expressions;
using JIM.Models.Interfaces;
using NUnit.Framework;

namespace JIM.Worker.Tests.Expressions;

/// <summary>
/// Functionality regression suite for the OWASP #500 DynamicExpresso input path review (see
/// <c>engineering/EXPRESSION_SECURITY.md</c>). Proves every built-in function registered in
/// <c>DynamicExpressoEvaluator.RegisterBuiltInFunctions</c> still evaluates correctly with the length-ceiling
/// and cache-bound guardrails (Part B, items 1-2) in place: the guardrails must be strictly non-subtractive.
/// Expression shapes are taken verbatim from the customer-facing built-in function reference table and worked
/// examples in <c>docs/concepts/expressions.md</c> and <c>docs/configuration/synchronisation-rules.md</c>, so
/// this suite doubles as proof that the documented examples actually work.
/// </summary>
[TestFixture]
public class DynamicExpressoEvaluatorFunctionalityRegressionTests
{
    private IExpressionEvaluator _evaluator = null!;

    [SetUp]
    public void SetUp()
    {
        _evaluator = new DynamicExpressoEvaluator();
    }

    #region String Functions (docs/concepts/expressions.md #string-functions)

    [Test]
    public void Trim_DocumentedExample_RemovesSurroundingWhitespace()
    {
        var context = new ExpressionContext(new Dictionary<string, object?> { { "Name", "  John  " } });
        Assert.That(_evaluator.Evaluate("Trim(mv[\"Name\"])", context), Is.EqualTo("John"));
    }

    [Test]
    public void Upper_DocumentedExample_ConvertsToUppercase()
    {
        var context = new ExpressionContext(new Dictionary<string, object?> { { "Name", "john" } });
        Assert.That(_evaluator.Evaluate("Upper(mv[\"Name\"])", context), Is.EqualTo("JOHN"));
    }

    [Test]
    public void Lower_DocumentedExample_ConvertsToLowercase()
    {
        var context = new ExpressionContext(new Dictionary<string, object?> { { "Name", "JOHN" } });
        Assert.That(_evaluator.Evaluate("Lower(mv[\"Name\"])", context), Is.EqualTo("john"));
    }

    [Test]
    public void Capitalise_DocumentedExample_CapitalisesEachWord()
    {
        var context = new ExpressionContext(new Dictionary<string, object?> { { "Name", "john doe" } });
        Assert.That(_evaluator.Evaluate("Capitalise(mv[\"Name\"])", context), Is.EqualTo("John Doe"));
    }

    [Test]
    public void Left_DocumentedExample_TakesFirstNCharacters()
    {
        var context = new ExpressionContext(new Dictionary<string, object?> { { "Name", "Jonathan" } });
        Assert.That(_evaluator.Evaluate("Left(mv[\"Name\"], 3)", context), Is.EqualTo("Jon"));
    }

    [Test]
    public void Right_DocumentedExample_TakesLastNCharacters()
    {
        var context = new ExpressionContext(new Dictionary<string, object?> { { "Name", "Jonathan" } });
        Assert.That(_evaluator.Evaluate("Right(mv[\"Name\"], 4)", context), Is.EqualTo("than"));
    }

    [Test]
    public void Substring_DocumentedExample_ExtractsPortion()
    {
        var context = new ExpressionContext(new Dictionary<string, object?> { { "Name", "Jonathan" } });
        Assert.That(_evaluator.Evaluate("Substring(mv[\"Name\"], 2, 4)", context), Is.EqualTo("nath"));
    }

    [Test]
    public void Replace_DocumentedExample_SubstitutesText()
    {
        var context = new ExpressionContext(new Dictionary<string, object?> { { "Email", "john.doe@old.com" } });
        Assert.That(_evaluator.Evaluate("Replace(mv[\"Email\"], \"@old.com\", \"@new.com\")", context), Is.EqualTo("john.doe@new.com"));
    }

    [Test]
    public void Length_DocumentedExample_CountsCharacters()
    {
        var context = new ExpressionContext(new Dictionary<string, object?> { { "Name", "John" } });
        Assert.That(_evaluator.Evaluate("Length(mv[\"Name\"])", context), Is.EqualTo(4));
    }

    [Test]
    public void IsNullOrEmpty_DocumentedExample_TrueForEmptyValue()
    {
        var context = new ExpressionContext(new Dictionary<string, object?> { { "Name", "" } });
        Assert.That(_evaluator.Evaluate("IsNullOrEmpty(mv[\"Name\"])", context), Is.True);
    }

    [Test]
    public void IsNullOrWhitespace_DocumentedExample_TrueForWhitespaceOnlyValue()
    {
        var context = new ExpressionContext(new Dictionary<string, object?> { { "Name", "   " } });
        Assert.That(_evaluator.Evaluate("IsNullOrWhitespace(mv[\"Name\"])", context), Is.True);
    }

    [Test]
    public void StartsWith_DocumentedExample_TrueWhenPrefixMatches()
    {
        var context = new ExpressionContext(new Dictionary<string, object?> { { "Email", "admin@company.com" } });
        Assert.That(_evaluator.Evaluate("StartsWith(mv[\"Email\"], \"admin\")", context), Is.True);
    }

    [Test]
    public void EndsWith_DocumentedExample_TrueWhenSuffixMatches()
    {
        var context = new ExpressionContext(new Dictionary<string, object?> { { "Email", "jane@company.com" } });
        Assert.That(_evaluator.Evaluate("EndsWith(mv[\"Email\"], \"@company.com\")", context), Is.True);
    }

    [Test]
    public void Contains_DocumentedExample_TrueWhenSubstringPresent()
    {
        var context = new ExpressionContext(new Dictionary<string, object?> { { "Email", "jane@company.com" } });
        Assert.That(_evaluator.Evaluate("Contains(mv[\"Email\"], \"@company\")", context), Is.True);
    }

    #endregion

    #region Conditional Functions (docs/concepts/expressions.md #conditional-functions)

    [Test]
    public void IIF_DocumentedExample_ReturnsTrueBranch()
    {
        var context = new ExpressionContext(new Dictionary<string, object?> { { "Status", "Active" } });
        Assert.That(_evaluator.Evaluate("IIF(Eq(mv[\"Status\"], \"Active\"), 512, 514)", context), Is.EqualTo(512));
    }

    [Test]
    public void Coalesce_DocumentedExample_FallsBackToSecondValue()
    {
        var context = new ExpressionContext(new Dictionary<string, object?> { { "Preferred Name", null }, { "First Name", "Jane" } });
        Assert.That(_evaluator.Evaluate("Coalesce(mv[\"Preferred Name\"], mv[\"First Name\"])", context), Is.EqualTo("Jane"));
    }

    [Test]
    public void Eq_DocumentedExample_TrueForMatchingText()
    {
        var context = new ExpressionContext(new Dictionary<string, object?> { { "Department", "IT" } });
        Assert.That(_evaluator.Evaluate("Eq(mv[\"Department\"], \"IT\")", context), Is.True);
    }

    #endregion

    #region Conversion Functions (docs/concepts/expressions.md #conversion-functions)

    [Test]
    public void ToStringFunction_DocumentedExample_ConvertsValueToText()
    {
        var context = new ExpressionContext(new Dictionary<string, object?> { { "EmployeeId", 42 } });
        Assert.That(_evaluator.Evaluate("ToString(mv[\"EmployeeId\"])", context), Is.EqualTo("42"));
    }

    [Test]
    public void ToInt_DocumentedExample_ParsesWholeNumber()
    {
        var context = new ExpressionContext(new Dictionary<string, object?> { { "Age", "35" } });
        Assert.That(_evaluator.Evaluate("ToInt(mv[\"Age\"])", context), Is.EqualTo(35));
    }

    #endregion

    #region Date Functions (docs/concepts/expressions.md #date-functions)

    [Test]
    public void Now_DocumentedExample_ReturnsCurrentUtcDateTime()
    {
        var before = DateTime.UtcNow;
        var result = _evaluator.Evaluate("Now()", new ExpressionContext());
        var after = DateTime.UtcNow;

        Assert.That(result, Is.InstanceOf<DateTime>());
        Assert.That((DateTime)result!, Is.InRange(before, after));
    }

    [Test]
    public void Today_DocumentedExample_ReturnsCurrentUtcDateAtMidnight()
    {
        var result = _evaluator.Evaluate("Today()", new ExpressionContext());

        Assert.That(result, Is.EqualTo(DateTime.UtcNow.Date));
    }

    [Test]
    public void FormatDate_DocumentedExample_FormatsAsIsoDate()
    {
        var context = new ExpressionContext(new Dictionary<string, object?> { { "HireDate", new DateTime(2020, 3, 15) } });
        Assert.That(_evaluator.Evaluate("FormatDate((DateTime)mv[\"HireDate\"], \"yyyy-MM-dd\")", context), Is.EqualTo("2020-03-15"));
    }

    [Test]
    public void ToFileTime_DocumentedExample_ConvertsDateToAdFileTime()
    {
        var endDate = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc);
        var context = new ExpressionContext(new Dictionary<string, object?> { { "Account Expires", endDate } });

        Assert.That(_evaluator.Evaluate("ToFileTime(mv[\"Account Expires\"])", context), Is.EqualTo(endDate.ToFileTimeUtc()));
    }

    [Test]
    public void FromFileTime_DocumentedExample_ConvertsAdFileTimeToDate()
    {
        var expected = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc);
        var context = new ExpressionContext(null, new Dictionary<string, object?> { { "accountExpires", expected.ToFileTimeUtc() } });

        Assert.That(_evaluator.Evaluate("FromFileTime(cs[\"accountExpires\"])", context), Is.EqualTo(expected));
    }

    #endregion

    #region Distinguished Name (DN) Functions (docs/concepts/expressions.md #distinguished-name-dn-functions)

    [Test]
    public void EscapeDN_DocumentedExample_BuildsSafeDistinguishedName()
    {
        var context = new ExpressionContext(new Dictionary<string, object?> { { "Display Name", "Smith, Jane" } });

        var result = _evaluator.Evaluate("\"CN=\" + EscapeDN(mv[\"Display Name\"]) + \",OU=Users,DC=domain,DC=local\"", context);

        Assert.That(result, Is.EqualTo("CN=Smith\\, Jane,OU=Users,DC=domain,DC=local"));
    }

    #endregion

    #region Password Generation (docs/concepts/expressions.md #password-generation)

    [Test]
    public void RandomPassword_DocumentedExample_GeneratesRequestedLength()
    {
        // Shape/length only, not exact output (non-deterministic by design).
        var result = _evaluator.Evaluate("RandomPassword(16, true)", new ExpressionContext()) as string;

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Length, Is.EqualTo(16));
    }

    [Test]
    public void RandomPassphrase_DocumentedExample_GeneratesRequestedWordCount()
    {
        // Shape only, not exact output (non-deterministic by design).
        var result = _evaluator.Evaluate("RandomPassphrase(4, \"-\")", new ExpressionContext()) as string;

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Split('-'), Has.Length.EqualTo(4));
    }

    #endregion

    #region Collection Functions (docs/concepts/expressions.md #collection-functions)

    [Test]
    public void CollectionContains_DocumentedExample_TrueWhenValuePresent()
    {
        var context = new ExpressionContext(null, new Dictionary<string, object?>
        {
            { "memberOf", new List<string> { "CN=Users", "CN=Admins" } }
        });

        Assert.That(_evaluator.Evaluate("CollectionContains(cs[\"memberOf\"], \"CN=Admins\")", context), Is.True);
    }

    [Test]
    public void Split_DocumentedExample_SplitsPipeDelimitedCourses()
    {
        var context = new ExpressionContext(null, new Dictionary<string, object?>
        {
            { "coursesCompleted", "SOFT101|SOFT201|SEC101" }
        });

        var result = _evaluator.Evaluate("Split(cs[\"coursesCompleted\"], \"|\")", context) as string[];

        Assert.That(result, Is.EqualTo(new[] { "SOFT101", "SOFT201", "SEC101" }));
    }

    [Test]
    public void Join_DocumentedExample_CombinesGroupsWithDelimiter()
    {
        var context = new ExpressionContext(new Dictionary<string, object?>
        {
            { "Groups", new[] { "Admin", "Users", "Developers" } }
        });

        Assert.That(_evaluator.Evaluate("Join(mv[\"Groups\"], \",\")", context), Is.EqualTo("Admin,Users,Developers"));
    }

    #endregion

    #region Account Control Functions (docs/concepts/expressions.md #account-control-functions)

    [Test]
    public void EnableUser_DocumentedExample_ClearsAccountDisableBit()
    {
        var context = new ExpressionContext(null, new Dictionary<string, object?> { { "userAccountControl", 514 } });
        Assert.That(_evaluator.Evaluate("EnableUser(cs[\"userAccountControl\"])", context), Is.EqualTo(512));
    }

    [Test]
    public void DisableUser_DocumentedExample_SetsAccountDisableBit()
    {
        var context = new ExpressionContext(null, new Dictionary<string, object?> { { "userAccountControl", 512 } });
        Assert.That(_evaluator.Evaluate("DisableUser(cs[\"userAccountControl\"])", context), Is.EqualTo(514));
    }

    [Test]
    public void SetBit_DocumentedExample_SetsPasswordNeverExpiresFlag()
    {
        var context = new ExpressionContext(new Dictionary<string, object?> { { "uac", 512 } });
        Assert.That(_evaluator.Evaluate("SetBit(mv[\"uac\"], 65536)", context), Is.EqualTo(66048));
    }

    [Test]
    public void ClearBit_DocumentedExample_ClearsPasswordNeverExpiresFlag()
    {
        var context = new ExpressionContext(new Dictionary<string, object?> { { "uac", 66048 } });
        Assert.That(_evaluator.Evaluate("ClearBit(mv[\"uac\"], 65536)", context), Is.EqualTo(512));
    }

    [Test]
    public void HasBit_DocumentedExample_TrueWhenAccountDisableBitSet()
    {
        var context = new ExpressionContext(null, new Dictionary<string, object?> { { "userAccountControl", 514 } });
        Assert.That(_evaluator.Evaluate("HasBit(cs[\"userAccountControl\"], 2)", context), Is.True);
    }

    #endregion

    #region Composed Real-World Scenarios (docs/concepts/expressions.md #common-scenarios)

    [Test]
    public void CommonScenario_BuildingEmailAddresses_ProducesExpectedAddress()
    {
        var context = new ExpressionContext(new Dictionary<string, object?> { { "First Name", "Jane" }, { "Last Name", "Smith" } });

        var result = _evaluator.Evaluate(
            "Lower(mv[\"First Name\"]) + \".\" + Lower(mv[\"Last Name\"]) + \"@company.com\"",
            context);

        Assert.That(result, Is.EqualTo("jane.smith@company.com"));
    }

    [Test]
    public void CommonScenario_EnableDisableBasedOnEmployeeStatus_EnablesActiveEmployee()
    {
        var context = new ExpressionContext(new Dictionary<string, object?> { { "Employee Status", "Active" } });

        var result = _evaluator.Evaluate("IIF(Eq(mv[\"Employee Status\"], \"Active\"), 512, 514)", context);

        Assert.That(result, Is.EqualTo(512));
    }

    [Test]
    public void CommonScenario_CheckIfAccountDisabled_ReportsDisabled()
    {
        var context = new ExpressionContext(null, new Dictionary<string, object?> { { "userAccountControl", 514 } });

        var result = _evaluator.Evaluate("IIF(HasBit(cs[\"userAccountControl\"], 2), \"Disabled\", \"Enabled\")", context);

        Assert.That(result, Is.EqualTo("Disabled"));
    }

    [Test]
    public void CommonScenario_GeneratingInitials_ProducesUppercaseInitials()
    {
        var context = new ExpressionContext(new Dictionary<string, object?> { { "First Name", "Jane" }, { "Last Name", "Smith" } });

        var result = _evaluator.Evaluate("Upper(Left(mv[\"First Name\"], 1)) + Upper(Left(mv[\"Last Name\"], 1))", context);

        Assert.That(result, Is.EqualTo("JS"));
    }

    [Test]
    public void CommonScenario_PlacingUsersInOusByDepartment_MatchesItDepartment()
    {
        var context = new ExpressionContext(new Dictionary<string, object?> { { "Department", "IT" } });

        var result = _evaluator.Evaluate(
            "IIF(Eq(mv[\"Department\"], \"IT\"), " +
            "\"OU=IT,OU=Users,DC=domain,DC=local\", " +
            "IIF(Eq(mv[\"Department\"], \"HR\"), " +
            "\"OU=HR,OU=Users,DC=domain,DC=local\", " +
            "\"OU=General,OU=Users,DC=domain,DC=local\"))",
            context);

        Assert.That(result, Is.EqualTo("OU=IT,OU=Users,DC=domain,DC=local"));
    }

    [Test]
    public void CommonScenario_PlacingUsersInOusByDepartment_FallsBackToGeneralForOtherDepartments()
    {
        var context = new ExpressionContext(new Dictionary<string, object?> { { "Department", "Finance" } });

        var result = _evaluator.Evaluate(
            "IIF(Eq(mv[\"Department\"], \"IT\"), " +
            "\"OU=IT,OU=Users,DC=domain,DC=local\", " +
            "IIF(Eq(mv[\"Department\"], \"HR\"), " +
            "\"OU=HR,OU=Users,DC=domain,DC=local\", " +
            "\"OU=General,OU=Users,DC=domain,DC=local\"))",
            context);

        Assert.That(result, Is.EqualTo("OU=General,OU=Users,DC=domain,DC=local"));
    }

    [Test]
    public void CommonScenario_AddingPrefixToAccountNames_PrefixesItDepartmentAccounts()
    {
        var context = new ExpressionContext(new Dictionary<string, object?> { { "Department", "IT" }, { "Account Name", "jsmith" } });

        var result = _evaluator.Evaluate("IIF(Eq(mv[\"Department\"], \"IT\"), \"tech-\" + mv[\"Account Name\"], mv[\"Account Name\"])", context);

        Assert.That(result, Is.EqualTo("tech-jsmith"));
    }

    [Test]
    public void CommonScenario_CaseInsensitiveStatusComparison_MatchesRegardlessOfCase()
    {
        var context = new ExpressionContext(new Dictionary<string, object?> { { "Status", "ACTIVE" } });

        var result = _evaluator.Evaluate("Eq(Lower(mv[\"Status\"]), \"active\")", context);

        Assert.That(result, Is.True);
    }

    [Test]
    public void CommonScenario_LogicalAndAcrossTwoConditions_TrueWhenBothMatch()
    {
        var context = new ExpressionContext(new Dictionary<string, object?> { { "Employee Status", "Active" }, { "Department", "IT" } });

        var result = _evaluator.Evaluate("Eq(mv[\"Employee Status\"], \"Active\") && Eq(mv[\"Department\"], \"IT\")", context);

        Assert.That(result, Is.True);
    }

    [Test]
    public void CommonScenario_DisplayNameWithOptionalTitle_OmitsMissingTitleCleanly()
    {
        var context = new ExpressionContext(new Dictionary<string, object?>
        {
            { "Title", null }, { "First Name", "Jane" }, { "Last Name", "Smith" }
        });

        var result = _evaluator.Evaluate("Coalesce(mv[\"Title\"], \"\") + \" \" + mv[\"First Name\"] + \" \" + mv[\"Last Name\"]", context);

        Assert.That(result, Is.EqualTo(" Jane Smith"));
    }

    [Test]
    public void CommonScenario_CapitaliseAndConcatenateNames_FromSynchronisationRulesDoc()
    {
        // docs/configuration/synchronisation-rules.md worked example: build a display name from raw
        // Connected System attributes.
        var context = new ExpressionContext(null, new Dictionary<string, object?> { { "givenName", "john" }, { "sn", "doe" } });

        var result = _evaluator.Evaluate("Capitalise(cs[\"givenName\"]) + \" \" + Capitalise(cs[\"sn\"])", context);

        Assert.That(result, Is.EqualTo("John Doe"));
    }

    #endregion
}
