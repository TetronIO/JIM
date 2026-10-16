// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Expressions;

namespace JIM.Application.Expressions;

/// <summary>
/// Every built-in expression function, described as data (#2053, PRD Phase 2 requirement 6). This is the one place a
/// function is described: the function reference in <c>docs/concepts/expressions.md</c> is tested to state exactly
/// this, the evaluator is tested to register exactly these names, the expression editor suggests and explains from
/// it, and <see cref="NonRepeatableFunctionDetector"/> reads which ones return a different value each time.
/// </summary>
/// <remarks>
/// Adding a function: register it in <see cref="DynamicExpressoEvaluator"/>, describe it here, and give it a row in the
/// reference section for its category. <c>ExpressionFunctionCatalogueTests</c> fails until all three agree.
/// </remarks>
public static class ExpressionFunctionCatalogue
{
    /// <summary>
    /// Every function, in reference order: by category, then as each section lists them.
    /// </summary>
    public static IReadOnlyList<ExpressionFunction> All { get; } =
    [
        // Text
        Function("Trim", ExpressionFunctionCategory.Text, "text", ExpressionResultType.Text, "Remove spaces from the start and end", "Trim(mv[\"Name\"])",
            Value()),
        Function("Upper", ExpressionFunctionCategory.Text, "text", ExpressionResultType.Text, "Convert to uppercase", "Upper(mv[\"Name\"])",
            Value()),
        Function("Lower", ExpressionFunctionCategory.Text, "text", ExpressionResultType.Text, "Convert to lowercase", "Lower(mv[\"Name\"])",
            Value()),
        Function("Capitalise", ExpressionFunctionCategory.Text, "text", ExpressionResultType.Text,
            "Capitalise each word (handles hyphenated names like \"O'Brien-Smith\")", "Capitalise(mv[\"Name\"])",
            Value()),
        Function("Left", ExpressionFunctionCategory.Text, "text", ExpressionResultType.Text, "Take the first N characters", "Left(mv[\"Name\"], 3)",
            Value(), Param("count", "How many characters to take.")),
        Function("Right", ExpressionFunctionCategory.Text, "text", ExpressionResultType.Text, "Take the last N characters", "Right(mv[\"Name\"], 4)",
            Value(), Param("count", "How many characters to take.")),
        Function("Substring", ExpressionFunctionCategory.Text, "text", ExpressionResultType.Text, "Extract a portion of text", "Substring(mv[\"Name\"], 2, 4)",
            Value(), Param("start", "Where to start, counting the first character as 0."), Param("length", "How many characters to take.")),
        Function("Replace", ExpressionFunctionCategory.Text, "text", ExpressionResultType.Text, "Replace one piece of text with another",
            "Replace(mv[\"Email\"], \"@old.com\", \"@new.com\")",
            Value(), Param("old", "The text to find."), Param("new", "The text to put in its place.")),
        Function("Length", ExpressionFunctionCategory.Text, "a whole number", ExpressionResultType.Number, "Count the number of characters", "Length(mv[\"Name\"])",
            Value()),
        Function("IsNullOrEmpty", ExpressionFunctionCategory.Text, "true or false", ExpressionResultType.Boolean,
            "Check if there is no value: missing, blank, or a Multi-Valued attribute with no values", "IsNullOrEmpty(mv[\"Name\"])",
            Value()),
        Function("IsNullOrWhitespace", ExpressionFunctionCategory.Text, "true or false", ExpressionResultType.Boolean,
            "Check if there is no value, or only spaces", "IsNullOrWhitespace(mv[\"Name\"])",
            Value()),
        Function("HasValue", ExpressionFunctionCategory.Text, "true or false", ExpressionResultType.Boolean,
            "Check if there is a value: the opposite of `IsNullOrEmpty`", "HasValue(mv[\"Manager\"])",
            Value()),
        Function("StartsWith", ExpressionFunctionCategory.Text, "true or false", ExpressionResultType.Boolean,
            "Check if text begins with a specific value, exactly unless `ignoreCase` is `true`", "StartsWith(mv[\"Email\"], \"admin\")",
            Value(), Param("prefix", "The text it should begin with."), IgnoreCase()),
        Function("EndsWith", ExpressionFunctionCategory.Text, "true or false", ExpressionResultType.Boolean,
            "Check if text ends with a specific value, exactly unless `ignoreCase` is `true`", "EndsWith(mv[\"Email\"], \"@company.com\")",
            Value(), Param("suffix", "The text it should end with."), IgnoreCase()),
        Function("Contains", ExpressionFunctionCategory.Text, "true or false", ExpressionResultType.Boolean,
            "Check if text contains a specific value, exactly unless `ignoreCase` is `true`", "Contains(mv[\"Email\"], \"@company\")",
            Value(), Param("search", "The text it should contain."), IgnoreCase()),

        // Pattern
        Function("IsMatch", ExpressionFunctionCategory.Pattern, "true or false", ExpressionResultType.Boolean,
            "Check if text matches a [regular expression](#pattern-functions), true when any value of a Multi-Valued attribute does",
            "IsMatch(cs[\"employeeId\"], \"^E[0-9]{6}$\")",
            Value(), Pattern(), IgnoreCase()),
        Function("RegexReplace", ExpressionFunctionCategory.Pattern, "text", ExpressionResultType.Text,
            "Replace every match of a regular expression; the replacement can use `$1` for the first group",
            "RegexReplace(cs[\"phone\"], \"[^0-9+]\", \"\")",
            Value(), Pattern(), Param("replacement", "The text to put in place of each match; $1, $2 and so on are its groups."), IgnoreCase()),
        Function("RegexExtract", ExpressionFunctionCategory.Pattern, "text", ExpressionResultType.Text,
            "The first match of a regular expression, or the group named by number; nothing when it does not match",
            "RegexExtract(cs[\"mail\"], \"@(.+)$\", 1)",
            Value(), Pattern(), Param("group", "Which group to return: 0, the default, is the whole match.", isOptional: true)),

        // Conditional
        Function("IIF", ExpressionFunctionCategory.Conditional, "either value", ExpressionResultType.Unknown,
            "Return one value if a condition is true, another if false", "IIF(Eq(mv[\"Status\"], \"Active\"), 512, 514)",
            Param("condition", "A true or false test."), Param("trueValue", "The value when it is true."), Param("falseValue", "The value when it is false.")),
        Function("Coalesce", ExpressionFunctionCategory.Conditional, "either value", ExpressionResultType.Unknown,
            "Use the first value if it has one (not missing, blank or an empty list), otherwise use the second",
            "Coalesce(mv[\"Preferred Name\"], mv[\"First Name\"])",
            Param("value1", "The value to use when it has one."), Param("value2", "The value to use otherwise.")),
        Function("Eq", ExpressionFunctionCategory.Conditional, "true or false", ExpressionResultType.Boolean,
            "Check if two values are equal, true when any value of a Multi-Valued attribute is; a date compares by [instant](#dates-and-numbers-as-text)",
            "Eq(cs[\"memberOf\"], \"CN=Admins\")",
            Param("value1", "The first value."), Param("value2", "The value to compare it with.")),

        // Conversion
        Function("ToString", ExpressionFunctionCategory.Conversion, "text", ExpressionResultType.Text, "Convert a value to text", "ToString(mv[\"EmployeeId\"])",
            Value()),
        Function("ToInt", ExpressionFunctionCategory.Conversion, "a whole number", ExpressionResultType.Number,
            "Convert text to a whole number (returns 0 if not a valid number)", "ToInt(mv[\"Age\"])",
            Value()),
        Function("ToDecimal", ExpressionFunctionCategory.Conversion, "a decimal number", ExpressionResultType.Decimal,
            "Convert to an exact decimal number: text is read with `.` as the decimal point, and accepts exponent notation (`1.5E3` is 1500). Returns 0 if not a valid number",
            "ToDecimal(cs[\"Salary\"])",
            Value()),

        // Date
        Function("Now", ExpressionFunctionCategory.Date, "a date", ExpressionResultType.DateTime, "Current date and time (UTC)", "Now()", isRepeatable: false),
        Function("Today", ExpressionFunctionCategory.Date, "a date", ExpressionResultType.DateTime, "Current date at midnight (UTC)", "Today()", isRepeatable: false),
        Function("FormatDate", ExpressionFunctionCategory.Date, "text", ExpressionResultType.Text,
            "Write a date, or ISO 8601 text, in a [.NET date format](https://learn.microsoft.com/dotnet/standard/base-types/custom-date-and-time-format-strings); nothing for other text or no value",
            "FormatDate(mv[\"Hire Date\"], \"yyyy-MM-dd\")",
            Param("value", "A date, or ISO 8601 text."), Param("format", "A .NET date format, such as \"dd MMM yyyy\".")),
        Function("ParseDate", ExpressionFunctionCategory.Date, "a date", ExpressionResultType.DateTime,
            "Read text in a [.NET date format](https://learn.microsoft.com/dotnet/standard/base-types/custom-date-and-time-format-strings) as a date, in UTC unless the format has an offset (`zzz` or `K`); nothing for empty text, an error for text that does not match",
            "ParseDate(cs[\"hireDate\"], \"dd/MM/yyyy\")",
            Param("text", "The text to read."), Param("format", "The .NET date format it is in, such as \"dd/MM/yyyy\".")),
        Function("ToFileTime", ExpressionFunctionCategory.Date, "a long number", ExpressionResultType.LongNumber,
            "Convert a date, or ISO 8601 text, to Active Directory's FILETIME format", "ToFileTime(mv[\"Account Expires\"])",
            Param("date", "A date, or ISO 8601 text.")),
        Function("FromFileTime", ExpressionFunctionCategory.Date, "a date", ExpressionResultType.DateTime,
            "Convert an Active Directory FILETIME back to a date", "FromFileTime(cs[\"accountExpires\"])",
            Param("filetime", "A FILETIME, such as accountExpires.")),

        // Distinguished names
        Function("EscapeDN", ExpressionFunctionCategory.DistinguishedName, "text", ExpressionResultType.Text,
            "Safely escape special characters for use in LDAP distinguished names",
            "\"CN=\" + EscapeDN(mv[\"Display Name\"]) + \",OU=Users,DC=domain,DC=local\"",
            Value()),

        // Passwords
        Function("RandomPassword", ExpressionFunctionCategory.Password, "text", ExpressionResultType.Text,
            "Generate a random password. Set extendedChars to `true` for special characters (!@#$%^&*)", "RandomPassword(16, true)",
            Param("length", "How many characters, from 8 to 128."), Param("extendedChars", "true to include special characters."),
            isRepeatable: false),
        Function("RandomPassphrase", ExpressionFunctionCategory.Password, "text", ExpressionResultType.Text,
            "Generate a passphrase from random words", "RandomPassphrase(4, \"-\")",
            Param("wordCount", "How many words, from 3 to 10."), Param("separator", "The text between words, such as \"-\"."),
            isRepeatable: false),

        // Multi-valued
        Function("Count", ExpressionFunctionCategory.MultiValued, "a whole number", ExpressionResultType.Number, "How many values there are: 0 for none",
            "Count(cs[\"proxyAddresses\"])",
            Values()),
        Function("Min", ExpressionFunctionCategory.MultiValued, "one of the values", ExpressionResultType.Unknown,
            "The smallest value: numbers by value, dates by instant, text by ordinal; nothing for no values", "Min(mv[\"Start Dates\"])",
            Values()),
        Function("Max", ExpressionFunctionCategory.MultiValued, "one of the values", ExpressionResultType.Unknown,
            "The largest value, compared as `Min` compares", "Max(cs[\"employeeNumbers\"])",
            Values()),
        Function("Where", ExpressionFunctionCategory.MultiValued, "the values that meet it", ExpressionResultType.Unknown,
            "The values that meet a [scoping operator](../configuration/scoping-criteria.md#operators), named as the editor shows it (`\"starts with\"`) or as the REST API names it (`\"StartsWith\"`); text compares exactly unless `ignoreCase` is `true`",
            "Where(cs[\"proxyAddresses\"], \"starts with\", \"SMTP:\")",
            Values(), Param("operator", "A scoping operator, such as \"starts with\"."), Param("value", "The value to compare each value with."),
            IgnoreCase()),
        Function("Split", ExpressionFunctionCategory.MultiValued, "several values", ExpressionResultType.Text,
            "Split a delimited text value into several values, in the order they appear", "Split(cs[\"coursesCompleted\"], \"|\")",
            Value(), Param("delimiter", "The text between values, such as \"|\".")),
        Function("Join", ExpressionFunctionCategory.MultiValued, "text", ExpressionResultType.Text,
            "Combine values into a single delimited text value, in the order they are read", "Join(mv[\"Groups\"], \",\")",
            Values(), Param("delimiter", "The text to put between values; a comma when left empty.")),

        // Account control
        Function("EnableUser", ExpressionFunctionCategory.AccountControl, "a whole number", ExpressionResultType.Number, "Enable a user account",
            "EnableUser(cs[\"userAccountControl\"])",
            Param("uac", "The userAccountControl value.")),
        Function("DisableUser", ExpressionFunctionCategory.AccountControl, "a whole number", ExpressionResultType.Number, "Disable a user account",
            "DisableUser(cs[\"userAccountControl\"])",
            Param("uac", "The userAccountControl value.")),
        Function("SetBit", ExpressionFunctionCategory.AccountControl, "a whole number", ExpressionResultType.Number, "Turn on a specific flag", "SetBit(mv[\"uac\"], 65536)",
            Param("value", "The number holding the flags."), Param("bit", "The flag's value, such as 65536.")),
        Function("ClearBit", ExpressionFunctionCategory.AccountControl, "a whole number", ExpressionResultType.Number, "Turn off a specific flag", "ClearBit(mv[\"uac\"], 65536)",
            Param("value", "The number holding the flags."), Param("bit", "The flag's value, such as 65536.")),
        Function("HasBit", ExpressionFunctionCategory.AccountControl, "true or false", ExpressionResultType.Boolean, "Check if a specific flag is turned on",
            "HasBit(cs[\"userAccountControl\"], 2)",
            Param("value", "The number holding the flags."), Param("bit", "The flag's value, such as 2.")),
    ];

    private static readonly Dictionary<string, ExpressionFunction> ByName =
        All.ToDictionary(f => f.Name, StringComparer.Ordinal);

    /// <summary>
    /// The function an expression calls by <paramref name="name"/>, or null for none. Function names are exact.
    /// </summary>
    public static ExpressionFunction? Find(string? name) =>
        name != null && ByName.TryGetValue(name, out var function) ? function : null;

    /// <summary>
    /// The reference's name for a category: its section heading, and the editor's group heading.
    /// </summary>
    public static string CategoryName(ExpressionFunctionCategory category) => category switch
    {
        ExpressionFunctionCategory.Text => "String Functions",
        ExpressionFunctionCategory.Pattern => "Pattern Functions",
        ExpressionFunctionCategory.Conditional => "Conditional Functions",
        ExpressionFunctionCategory.Conversion => "Conversion Functions",
        ExpressionFunctionCategory.Date => "Date Functions",
        ExpressionFunctionCategory.DistinguishedName => "Distinguished Name (DN) Functions",
        ExpressionFunctionCategory.Password => "Password Generation",
        ExpressionFunctionCategory.MultiValued => "Multi-Valued Functions",
        ExpressionFunctionCategory.AccountControl => "Account Control Functions",
        _ => category.ToString()
    };

    private static ExpressionFunction Function(string name, ExpressionFunctionCategory category, string returns, ExpressionResultType resultType, string description,
        string example, params ExpressionFunctionParameter[] parameters) =>
        new(name, category, parameters, returns, resultType, description, example);

    private static ExpressionFunction Function(string name, ExpressionFunctionCategory category, string returns, ExpressionResultType resultType, string description,
        string example, bool isRepeatable) =>
        new(name, category, [], returns, resultType, description, example, isRepeatable);

    private static ExpressionFunction Function(string name, ExpressionFunctionCategory category, string returns, ExpressionResultType resultType, string description,
        string example, ExpressionFunctionParameter first, ExpressionFunctionParameter second, bool isRepeatable) =>
        new(name, category, [first, second], returns, resultType, description, example, isRepeatable);

    private static ExpressionFunctionParameter Param(string name, string description, bool isOptional = false) =>
        new(name, description, isOptional);

    private static ExpressionFunctionParameter Value() =>
        Param("value", "The value to use; each value of a Multi-Valued attribute is used in turn.");

    private static ExpressionFunctionParameter Values() =>
        Param("values", "The values, usually a Multi-Valued attribute such as cs[\"proxyAddresses\"].");

    private static ExpressionFunctionParameter Pattern() =>
        Param("pattern", "A .NET regular expression, matched with a time limit.");

    private static ExpressionFunctionParameter IgnoreCase() =>
        Param("ignoreCase", "true to ignore case; text compares exactly when left out.", isOptional: true);
}
