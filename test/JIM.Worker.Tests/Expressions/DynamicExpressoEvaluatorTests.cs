using JIM.Application.Expressions;
using NUnit.Framework;

namespace JIM.Worker.Tests.Expressions;

[TestFixture]
public class DynamicExpressoEvaluatorTests
{
    private IExpressionEvaluator _evaluator = null!;

    [SetUp]
    public void SetUp()
    {
        _evaluator = new DynamicExpressoEvaluator();
    }

    #region Basic Expression Tests

    [Test]
    public void Evaluate_SimpleStringConcatenation_ReturnsExpectedResultAsync()
    {
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "Display Name", "John Doe" } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate(
            "\"CN=\" + mv[\"Display Name\"] + \",OU=Users,DC=domain,DC=local\"",
            context);

        Assert.That(result, Is.EqualTo("CN=John Doe,OU=Users,DC=domain,DC=local"));
    }

    [Test]
    public void Evaluate_AccessMissingAttribute_ReturnsNullAsync()
    {
        var context = new ExpressionContext(
            new Dictionary<string, object?>(),
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate("mv[\"NonExistent\"]", context);

        Assert.That(result, Is.Null);
    }

    [Test]
    public void Evaluate_AccessCsAttribute_ReturnsValueAsync()
    {
        var context = new ExpressionContext(
            new Dictionary<string, object?>(),
            new Dictionary<string, object?> { { "employeeId", "E001" } });

        var result = _evaluator.Evaluate("cs[\"employeeId\"]", context);

        Assert.That(result, Is.EqualTo("E001"));
    }

    [Test]
    public void Evaluate_TernaryOperator_WorksCorrectlyAsync()
    {
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "Department", "IT" }, { "Account Name", "jdoe" } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate(
            "mv[\"Department\"] == \"IT\" ? \"tech-\" + mv[\"Account Name\"] : mv[\"Account Name\"]",
            context);

        Assert.That(result, Is.EqualTo("tech-jdoe"));
    }

    #endregion

    #region String Function Tests

    [Test]
    public void Evaluate_Trim_RemovesWhitespaceAsync()
    {
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "Name", "  John  " } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate("Trim(mv[\"Name\"])", context);

        Assert.That(result, Is.EqualTo("John"));
    }

    [Test]
    public void Evaluate_Upper_ConvertsToUppercaseAsync()
    {
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "Name", "john" } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate("Upper(mv[\"Name\"])", context);

        Assert.That(result, Is.EqualTo("JOHN"));
    }

    [Test]
    public void Evaluate_Lower_ConvertsToLowercaseAsync()
    {
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "Name", "JOHN" } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate("Lower(mv[\"Name\"])", context);

        Assert.That(result, Is.EqualTo("john"));
    }

    [Test]
    public void Evaluate_Capitalise_ConvertsToTitleCaseAsync()
    {
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "Name", "john DOE" } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate("Capitalise(mv[\"Name\"])", context);

        Assert.That(result, Is.EqualTo("John Doe"));
    }

    [Test]
    public void Evaluate_Capitalise_HandlesHyphenatedNamesAsync()
    {
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "Name", "mary-jane WATSON" } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate("Capitalise(mv[\"Name\"])", context);

        Assert.That(result, Is.EqualTo("Mary-Jane Watson"));
    }

    [Test]
    public void Evaluate_Left_ReturnsLeftmostCharactersAsync()
    {
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "Name", "Jonathan" } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate("Left(mv[\"Name\"], 3)", context);

        Assert.That(result, Is.EqualTo("Jon"));
    }

    [Test]
    public void Evaluate_Right_ReturnsRightmostCharactersAsync()
    {
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "Name", "Jonathan" } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate("Right(mv[\"Name\"], 4)", context);

        Assert.That(result, Is.EqualTo("than"));
    }

    [Test]
    public void Evaluate_Substring_ExtractsMiddleAsync()
    {
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "Name", "Jonathan" } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate("Substring(mv[\"Name\"], 2, 4)", context);

        Assert.That(result, Is.EqualTo("nath"));
    }

    [Test]
    public void Evaluate_Replace_SubstitutesTextAsync()
    {
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "Email", "john.doe@old.com" } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate("Replace(mv[\"Email\"], \"@old.com\", \"@new.com\")", context);

        Assert.That(result, Is.EqualTo("john.doe@new.com"));
    }

    [Test]
    public void Evaluate_Length_ReturnsStringLengthAsync()
    {
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "Name", "John" } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate("Length(mv[\"Name\"])", context);

        Assert.That(result, Is.EqualTo(4));
    }

    [Test]
    public void Evaluate_IsNullOrEmpty_ReturnsTrueForEmptyAsync()
    {
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "Name", "" } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate("IsNullOrEmpty(mv[\"Name\"])", context);

        Assert.That(result, Is.True);
    }

    #endregion

    #region Containment Function Tests

    [Test]
    public void Evaluate_Contains_ReturnsTrueWhenFoundAsync()
    {
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "Email", "john@company.com" } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate("Contains(mv[\"Email\"], \"@company.com\")", context);

        Assert.That(result, Is.True);
    }

    [Test]
    public void Evaluate_Contains_ReturnsFalseWhenNotFoundAsync()
    {
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "Email", "john@other.com" } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate("Contains(mv[\"Email\"], \"@company.com\")", context);

        Assert.That(result, Is.False);
    }

    [Test]
    public void Evaluate_CollectionContains_ReturnsTrueWhenFoundAsync()
    {
        var context = new ExpressionContext(
            new Dictionary<string, object?>(),
            new Dictionary<string, object?> { { "memberOf", new List<string> { "CN=Users", "CN=Admins", "CN=Developers" } } });

        var result = _evaluator.Evaluate("CollectionContains(cs[\"memberOf\"], \"CN=Admins\")", context);

        Assert.That(result, Is.True);
    }

    [Test]
    public void Evaluate_CollectionContains_ReturnsFalseWhenNotFoundAsync()
    {
        var context = new ExpressionContext(
            new Dictionary<string, object?>(),
            new Dictionary<string, object?> { { "memberOf", new List<string> { "CN=Users", "CN=Developers" } } });

        var result = _evaluator.Evaluate("CollectionContains(cs[\"memberOf\"], \"CN=Admins\")", context);

        Assert.That(result, Is.False);
    }

    #endregion

    #region Conditional Function Tests

    [Test]
    public void Evaluate_Coalesce_ReturnsFirstNonNullAsync()
    {
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "Preferred Name", null }, { "First Name", "Jane" } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate("Coalesce(mv[\"Preferred Name\"], mv[\"First Name\"])", context);

        Assert.That(result, Is.EqualTo("Jane"));
    }

    [Test]
    public void Evaluate_IIF_ReturnsTrueValueWhenConditionTrueAsync()
    {
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "IsActive", true } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate("IIF((bool)mv[\"IsActive\"], \"Active\", \"Inactive\")", context);

        Assert.That(result, Is.EqualTo("Active"));
    }

    [Test]
    public void Evaluate_NestedIIF_WorksCorrectlyAsync()
    {
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "Department", "HR" }, { "Account Name", "jdoe" } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate(
            "IIF(mv[\"Department\"] == \"IT\", \"tech-\" + mv[\"Account Name\"], IIF(mv[\"Department\"] == \"HR\", \"hr-\" + mv[\"Account Name\"], mv[\"Account Name\"]))",
            context);

        Assert.That(result, Is.EqualTo("hr-jdoe"));
    }

    #endregion

    #region DN Helper Tests

    [Test]
    public void Evaluate_EscapeDN_EscapesCommaAsync()
    {
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "Display Name", "Doe, John" } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate("EscapeDN(mv[\"Display Name\"])", context);

        Assert.That(result, Is.EqualTo("Doe\\, John"));
    }

    [Test]
    public void Evaluate_EscapeDN_EscapesMultipleSpecialCharsAsync()
    {
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "Display Name", "John + Jane <Test>" } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate("EscapeDN(mv[\"Display Name\"])", context);

        Assert.That(result, Is.EqualTo("John \\+ Jane \\<Test\\>"));
    }

    [Test]
    public void Evaluate_EscapeDN_EscapesLeadingSpaceAsync()
    {
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "Display Name", " John" } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate("EscapeDN(mv[\"Display Name\"])", context);

        Assert.That(result, Is.EqualTo("\\ John"));
    }

    [Test]
    public void Evaluate_EscapeDN_EscapesTrailingSpaceAsync()
    {
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "Display Name", "John " } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate("EscapeDN(mv[\"Display Name\"])", context);

        Assert.That(result, Is.EqualTo("John\\ "));
    }

    [Test]
    public void Evaluate_FullDNConstruction_WorksCorrectlyAsync()
    {
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "Display Name", "Doe, John" } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate(
            "\"CN=\" + EscapeDN(mv[\"Display Name\"]) + \",OU=Users,DC=domain,DC=local\"",
            context);

        Assert.That(result, Is.EqualTo("CN=Doe\\, John,OU=Users,DC=domain,DC=local"));
    }

    #endregion

    #region Password Generation Tests

    [Test]
    public void Evaluate_RandomPassword_GeneratesCorrectLengthAsync()
    {
        var context = new ExpressionContext();

        var result = _evaluator.Evaluate("RandomPassword(16, false)", context) as string;

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Length, Is.EqualTo(16));
    }

    [Test]
    public void Evaluate_RandomPassword_WithExtendedChars_ContainsSpecialCharsAsync()
    {
        var context = new ExpressionContext();

        // Run multiple times to increase chance of seeing special chars
        var hasSpecialChar = false;
        for (var i = 0; i < 10; i++)
        {
            var result = _evaluator.Evaluate("RandomPassword(20, true)", context) as string;
            if (result != null && result.Any(c => "!@#$%^&*".Contains(c)))
            {
                hasSpecialChar = true;
                break;
            }
        }

        Assert.That(hasSpecialChar, Is.True, "Expected at least one result to contain special characters");
    }

    [Test]
    public void Evaluate_RandomPassphrase_GeneratesCorrectWordCountAsync()
    {
        var context = new ExpressionContext();

        var result = _evaluator.Evaluate("RandomPassphrase(4, \"-\")", context) as string;

        Assert.That(result, Is.Not.Null);
        var words = result!.Split('-');
        Assert.That(words.Length, Is.EqualTo(4));
    }

    [Test]
    public void Evaluate_RandomPassphrase_UsesSeparatorAsync()
    {
        var context = new ExpressionContext();

        var result = _evaluator.Evaluate("RandomPassphrase(3, \"_\")", context) as string;

        Assert.That(result, Is.Not.Null);
        Assert.That(result, Does.Contain("_"));
        Assert.That(result, Does.Not.Contain("-"));
    }

    #endregion

    #region Validation Tests

    [Test]
    public void Validate_ValidExpression_ReturnsSuccessAsync()
    {
        var result = _evaluator.Validate("mv[\"Name\"] + \" Test\"");

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.ErrorMessage, Is.Null);
    }

    [Test]
    public void Validate_InvalidExpression_ReturnsFailureAsync()
    {
        var result = _evaluator.Validate("this is {{ invalid");

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.ErrorMessage, Is.Not.Null);
    }

    [Test]
    public void Validate_EmptyExpression_ReturnsFailureAsync()
    {
        var result = _evaluator.Validate("");

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.ErrorMessage, Is.EqualTo("Expression cannot be empty."));
    }

    #endregion

    #region Test Method Tests

    [Test]
    public void Test_ValidExpression_ReturnsSuccessWithResultAsync()
    {
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "Name", "John" } },
            new Dictionary<string, object?>());

        var result = _evaluator.Test("Upper(mv[\"Name\"])", context);

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Result, Is.EqualTo("JOHN"));
        Assert.That(result.ResultType, Is.EqualTo("String"));
    }

    [Test]
    public void Test_InvalidExpression_ReturnsFailureAsync()
    {
        var context = new ExpressionContext();

        var result = _evaluator.Test("invalid {{ syntax", context);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.ErrorMessage, Is.Not.Null);
    }

    #endregion

    #region Date Function Tests

    [Test]
    public void Evaluate_Now_ReturnsCurrentDateTimeAsync()
    {
        var context = new ExpressionContext();
        var before = DateTime.UtcNow;

        var result = _evaluator.Evaluate("Now()", context);

        var after = DateTime.UtcNow;
        Assert.That(result, Is.InstanceOf<DateTime>());
        Assert.That((DateTime)result!, Is.InRange(before, after));
    }

    [Test]
    public void Evaluate_FormatDate_FormatsCorrectlyAsync()
    {
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "BirthDate", new DateTime(1990, 5, 15) } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate("FormatDate((DateTime)mv[\"BirthDate\"], \"yyyy-MM-dd\")", context);

        Assert.That(result, Is.EqualTo("1990-05-15"));
    }

    #endregion

    #region Conversion Function Tests

    [Test]
    public void Evaluate_ToInt_ParsesIntegerAsync()
    {
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "Age", "42" } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate("ToInt(mv[\"Age\"])", context);

        Assert.That(result, Is.EqualTo(42));
    }

    [Test]
    public void Evaluate_ToInt_ReturnsZeroForInvalidAsync()
    {
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "Age", "not a number" } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate("ToInt(mv[\"Age\"])", context);

        Assert.That(result, Is.EqualTo(0));
    }

    [Test]
    public void Evaluate_ToString_ConvertsObjectAsync()
    {
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "Count", 42 } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate("ToString(mv[\"Count\"])", context);

        Assert.That(result, Is.EqualTo("42"));
    }

    #endregion

    #region Complex Expression Tests

    [Test]
    public void Evaluate_EmailGeneration_WorksCorrectlyAsync()
    {
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "First Name", "John" }, { "Last Name", "Doe" } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate(
            "Lower(mv[\"First Name\"]) + \".\" + Lower(mv[\"Last Name\"]) + \"@company.com\"",
            context);

        Assert.That(result, Is.EqualTo("john.doe@company.com"));
    }

    [Test]
    public void Evaluate_InitialsGeneration_WorksCorrectlyAsync()
    {
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "First Name", "John" }, { "Last Name", "Doe" } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate(
            "Upper(Left(mv[\"First Name\"], 1)) + Upper(Left(mv[\"Last Name\"], 1))",
            context);

        Assert.That(result, Is.EqualTo("JD"));
    }

    #endregion

    #region Bitwise Function Tests (AD userAccountControl)

    [Test]
    public void Evaluate_EnableUser_ClearsAccountDisableBit()
    {
        // ACCOUNTDISABLE = 0x0002 (2)
        // A disabled user typically has userAccountControl = 514 (0x202 = NORMAL_ACCOUNT | ACCOUNTDISABLE)
        // Enabling should result in 512 (0x200 = NORMAL_ACCOUNT)
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "userAccountControl", 514 } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate("EnableUser(mv[\"userAccountControl\"])", context);

        Assert.That(result, Is.EqualTo(512));
    }

    [Test]
    public void Evaluate_EnableUser_LeavesAlreadyEnabledUserUnchanged()
    {
        // Already enabled user with userAccountControl = 512
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "userAccountControl", 512 } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate("EnableUser(mv[\"userAccountControl\"])", context);

        Assert.That(result, Is.EqualTo(512));
    }

    [Test]
    public void Evaluate_DisableUser_SetsAccountDisableBit()
    {
        // An enabled user with userAccountControl = 512 (NORMAL_ACCOUNT)
        // Disabling should result in 514 (NORMAL_ACCOUNT | ACCOUNTDISABLE)
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "userAccountControl", 512 } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate("DisableUser(mv[\"userAccountControl\"])", context);

        Assert.That(result, Is.EqualTo(514));
    }

    [Test]
    public void Evaluate_DisableUser_LeavesAlreadyDisabledUserUnchanged()
    {
        // Already disabled user with userAccountControl = 514
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "userAccountControl", 514 } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate("DisableUser(mv[\"userAccountControl\"])", context);

        Assert.That(result, Is.EqualTo(514));
    }

    [Test]
    public void Evaluate_SetBit_SetsSpecifiedBit()
    {
        // Set DONT_EXPIRE_PASSWORD (0x10000 = 65536) on a normal account (512)
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "userAccountControl", 512 } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate("SetBit(mv[\"userAccountControl\"], 65536)", context);

        Assert.That(result, Is.EqualTo(66048)); // 512 | 65536 = 66048
    }

    [Test]
    public void Evaluate_ClearBit_ClearsSpecifiedBit()
    {
        // Clear DONT_EXPIRE_PASSWORD (0x10000 = 65536) from an account that has it set
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "userAccountControl", 66048 } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate("ClearBit(mv[\"userAccountControl\"], 65536)", context);

        Assert.That(result, Is.EqualTo(512)); // 66048 & ~65536 = 512
    }

    [Test]
    public void Evaluate_HasBit_ReturnsTrueWhenBitIsSet()
    {
        // Check if ACCOUNTDISABLE (2) is set in 514 (which has it set)
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "userAccountControl", 514 } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate("HasBit(mv[\"userAccountControl\"], 2)", context);

        Assert.That(result, Is.True);
    }

    [Test]
    public void Evaluate_HasBit_ReturnsFalseWhenBitIsNotSet()
    {
        // Check if ACCOUNTDISABLE (2) is set in 512 (which does not have it set)
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "userAccountControl", 512 } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate("HasBit(mv[\"userAccountControl\"], 2)", context);

        Assert.That(result, Is.False);
    }

    [Test]
    public void Evaluate_EnableUser_HandlesStringInput()
    {
        // Test that string values are parsed correctly
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "userAccountControl", "514" } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate("EnableUser(mv[\"userAccountControl\"])", context);

        Assert.That(result, Is.EqualTo(512));
    }

    [Test]
    public void Evaluate_EnableUser_HandlesNullAsZero()
    {
        // Test that null values are treated as 0
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "userAccountControl", null } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate("EnableUser(mv[\"userAccountControl\"])", context);

        Assert.That(result, Is.EqualTo(0)); // 0 & ~2 = 0
    }

    [Test]
    public void Evaluate_ConditionalWithHasBit_WorksCorrectly()
    {
        // Use HasBit in a conditional to check account status
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "userAccountControl", 514 } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate(
            "IIF(HasBit(mv[\"userAccountControl\"], 2), \"Disabled\", \"Enabled\")",
            context);

        Assert.That(result, Is.EqualTo("Disabled"));
    }

    #endregion

    #region FileTime Conversion Tests (AD accountExpires, pwdLastSet, etc.)

    [Test]
    public void Evaluate_ToFileTime_ConvertsDateTimeToFileTime()
    {
        // Test with a known date: January 1, 2025 00:00:00 UTC
        // This should convert to the Windows FILETIME format
        var testDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var expectedFileTime = testDate.ToFileTimeUtc();

        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "AccountExpires", testDate } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate("ToFileTime(mv[\"AccountExpires\"])", context);

        Assert.That(result, Is.EqualTo(expectedFileTime));
    }

    [Test]
    public void Evaluate_ToFileTime_ParsesDateTimeFromString()
    {
        // Test with a string date in ISO 8601 format
        var testDate = new DateTime(2025, 6, 15, 12, 30, 0, DateTimeKind.Utc);
        var dateString = testDate.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var expectedFileTime = testDate.ToFileTimeUtc();

        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "AccountExpires", dateString } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate("ToFileTime(mv[\"AccountExpires\"])", context);

        Assert.That(result, Is.EqualTo(expectedFileTime));
    }

    [Test]
    public void Evaluate_ToFileTime_ReturnsNullForNullInput()
    {
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "AccountExpires", null } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate("ToFileTime(mv[\"AccountExpires\"])", context);

        Assert.That(result, Is.Null);
    }

    [Test]
    public void Evaluate_ToFileTime_ReturnsNullForInvalidString()
    {
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "AccountExpires", "not a date" } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate("ToFileTime(mv[\"AccountExpires\"])", context);

        Assert.That(result, Is.Null);
    }

    [Test]
    public void Evaluate_ToFileTime_HandlesDateTimeOffset()
    {
        // PostgreSQL returns DateTimeOffset for "timestamp with time zone" columns
        // This test ensures we handle the database-native type correctly
        var testDate = new DateTime(2025, 6, 15, 12, 30, 0, DateTimeKind.Utc);
        var dateTimeOffset = new DateTimeOffset(testDate, TimeSpan.Zero);
        var expectedFileTime = testDate.ToFileTimeUtc();

        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "AccountExpires", dateTimeOffset } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate("ToFileTime(mv[\"AccountExpires\"])", context);

        Assert.That(result, Is.EqualTo(expectedFileTime));
    }

    [Test]
    public void Evaluate_ToFileTime_HandlesDateTimeOffsetWithOffset()
    {
        // Test DateTimeOffset with a non-UTC offset (e.g., +05:30 India Standard Time)
        // The function should convert to UTC before calculating FILETIME
        var localTime = new DateTime(2025, 6, 15, 18, 0, 0);  // 18:00 local time
        var offset = TimeSpan.FromHours(5.5);  // +05:30
        var dateTimeOffset = new DateTimeOffset(localTime, offset);

        // Expected: 18:00 + 05:30 local = 12:30 UTC
        var expectedUtc = new DateTime(2025, 6, 15, 12, 30, 0, DateTimeKind.Utc);
        var expectedFileTime = expectedUtc.ToFileTimeUtc();

        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "AccountExpires", dateTimeOffset } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate("ToFileTime(mv[\"AccountExpires\"])", context);

        Assert.That(result, Is.EqualTo(expectedFileTime));
    }

    [Test]
    public void Evaluate_FromFileTime_ConvertsFileTimeToDateTime()
    {
        // Test with a known FILETIME value
        var expectedDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var fileTime = expectedDate.ToFileTimeUtc();

        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "AccountExpires", fileTime } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate("FromFileTime(mv[\"AccountExpires\"])", context);

        Assert.That(result, Is.EqualTo(expectedDate));
    }

    [Test]
    public void Evaluate_FromFileTime_ParsesFileTimeFromString()
    {
        // Test with a FILETIME value as string (common in LDAP responses)
        var expectedDate = new DateTime(2025, 6, 15, 12, 30, 0, DateTimeKind.Utc);
        var fileTimeString = expectedDate.ToFileTimeUtc().ToString();

        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "AccountExpires", fileTimeString } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate("FromFileTime(mv[\"AccountExpires\"])", context);

        Assert.That(result, Is.EqualTo(expectedDate));
    }

    [Test]
    public void Evaluate_FromFileTime_ReturnsNullForNullInput()
    {
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "AccountExpires", null } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate("FromFileTime(mv[\"AccountExpires\"])", context);

        Assert.That(result, Is.Null);
    }

    [Test]
    public void Evaluate_FromFileTime_ReturnsNullForZero()
    {
        // Zero means "never expires" in AD - should return null
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "AccountExpires", 0L } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate("FromFileTime(mv[\"AccountExpires\"])", context);

        Assert.That(result, Is.Null);
    }

    [Test]
    public void Evaluate_FromFileTime_ReturnsNullForMaxValue()
    {
        // Int64.MaxValue (9223372036854775807) means "never expires" in AD - should return null
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "AccountExpires", long.MaxValue } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate("FromFileTime(mv[\"AccountExpires\"])", context);

        Assert.That(result, Is.Null);
    }

    [Test]
    public void Evaluate_RoundTrip_FileTimeConversion()
    {
        // Test that ToFileTime and FromFileTime are inverse operations
        var originalDate = new DateTime(2025, 3, 15, 14, 30, 45, DateTimeKind.Utc);
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "Date", originalDate } },
            new Dictionary<string, object?>());

        // Convert to FILETIME and back
        var fileTimeResult = _evaluator.Evaluate("ToFileTime(mv[\"Date\"])", context);
        Assert.That(fileTimeResult, Is.Not.Null);

        var context2 = new ExpressionContext(
            new Dictionary<string, object?> { { "FileTime", fileTimeResult } },
            new Dictionary<string, object?>());
        var dateResult = _evaluator.Evaluate("FromFileTime(mv[\"FileTime\"])", context2);

        Assert.That(dateResult, Is.EqualTo(originalDate));
    }

    [Test]
    public void Evaluate_ToFileTime_ThrowsForUnrecognisedType()
    {
        // Passing an integer (not a DateTime, DateTimeOffset, or string) should throw
        // This ensures we fail fast rather than silently returning null for unexpected types
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "AccountExpires", 12345 } },  // int, not a date type
            new Dictionary<string, object?>());

        var ex = Assert.Throws<ArgumentException>(() =>
            _evaluator.Evaluate("ToFileTime(mv[\"AccountExpires\"])", context));

        Assert.That(ex?.Message, Does.Contain("ToFileTime cannot convert value of type 'Int32'"));
    }

    [Test]
    public void Evaluate_ToFileTime_ReturnsNullForEmptyString()
    {
        // Empty strings should return null (graceful handling for missing CSV data)
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "AccountExpires", "" } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate("ToFileTime(mv[\"AccountExpires\"])", context);

        Assert.That(result, Is.Null);
    }

    [Test]
    public void Evaluate_FromFileTime_ThrowsForUnrecognisedType()
    {
        // Passing a DateTime (not a long, int, or string) should throw
        // This ensures we fail fast rather than silently returning null for unexpected types
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "FileTime", DateTime.UtcNow } },  // DateTime, not a numeric type
            new Dictionary<string, object?>());

        var ex = Assert.Throws<ArgumentException>(() =>
            _evaluator.Evaluate("FromFileTime(mv[\"FileTime\"])", context));

        Assert.That(ex?.Message, Does.Contain("FromFileTime cannot convert value of type 'DateTime'"));
    }

    [Test]
    public void Evaluate_FromFileTime_ReturnsNullForEmptyString()
    {
        // Empty strings should return null (graceful handling for missing data)
        var context = new ExpressionContext(
            new Dictionary<string, object?> { { "FileTime", "" } },
            new Dictionary<string, object?>());

        var result = _evaluator.Evaluate("FromFileTime(mv[\"FileTime\"])", context);

        Assert.That(result, Is.Null);
    }

    #endregion
}
