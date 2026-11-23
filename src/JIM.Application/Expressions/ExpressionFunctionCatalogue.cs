// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Localisation;
using JIM.Application.Resources;
using JIM.Models.Expressions;
using JIM.Models.Localisation;

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
        Function("Trim", ExpressionFunctionCategory.Text, nameof(ExpressionMessages.ReturnsText), ExpressionResultType.Text, nameof(ExpressionMessages.FunctionTrim), "Trim(mv[\"Name\"])",
            Value()),
        Function("Upper", ExpressionFunctionCategory.Text, nameof(ExpressionMessages.ReturnsText), ExpressionResultType.Text, nameof(ExpressionMessages.FunctionUpper), "Upper(mv[\"Name\"])",
            Value()),
        Function("Lower", ExpressionFunctionCategory.Text, nameof(ExpressionMessages.ReturnsText), ExpressionResultType.Text, nameof(ExpressionMessages.FunctionLower), "Lower(mv[\"Name\"])",
            Value()),
        Function("Capitalise", ExpressionFunctionCategory.Text, nameof(ExpressionMessages.ReturnsText), ExpressionResultType.Text,
            nameof(ExpressionMessages.FunctionCapitalise), "Capitalise(mv[\"Name\"])",
            Value()),
        Function("Left", ExpressionFunctionCategory.Text, nameof(ExpressionMessages.ReturnsText), ExpressionResultType.Text, nameof(ExpressionMessages.FunctionLeft), "Left(mv[\"Name\"], 3)",
            Value(), Param("count", nameof(ExpressionMessages.ParamCharacterCount))),
        Function("Right", ExpressionFunctionCategory.Text, nameof(ExpressionMessages.ReturnsText), ExpressionResultType.Text, nameof(ExpressionMessages.FunctionRight), "Right(mv[\"Name\"], 4)",
            Value(), Param("count", nameof(ExpressionMessages.ParamCharacterCount))),
        Function("Substring", ExpressionFunctionCategory.Text, nameof(ExpressionMessages.ReturnsText), ExpressionResultType.Text, nameof(ExpressionMessages.FunctionSubstring), "Substring(mv[\"Name\"], 2, 4)",
            Value(), Param("start", nameof(ExpressionMessages.ParamStart)), Param("length", nameof(ExpressionMessages.ParamCharacterCount))),
        Function("Replace", ExpressionFunctionCategory.Text, nameof(ExpressionMessages.ReturnsText), ExpressionResultType.Text, nameof(ExpressionMessages.FunctionReplace),
            "Replace(mv[\"Email\"], \"@old.com\", \"@new.com\")",
            Value(), Param("old", nameof(ExpressionMessages.ParamTextToFind)), Param("new", nameof(ExpressionMessages.ParamTextToPut))),
        Function("Length", ExpressionFunctionCategory.Text, nameof(ExpressionMessages.ReturnsWholeNumber), ExpressionResultType.Number, nameof(ExpressionMessages.FunctionLength), "Length(mv[\"Name\"])",
            Value()),
        Function("IsNullOrEmpty", ExpressionFunctionCategory.Text, nameof(ExpressionMessages.ReturnsTrueOrFalse), ExpressionResultType.Boolean,
            nameof(ExpressionMessages.FunctionIsNullOrEmpty), "IsNullOrEmpty(mv[\"Name\"])",
            Value()),
        Function("IsNullOrWhitespace", ExpressionFunctionCategory.Text, nameof(ExpressionMessages.ReturnsTrueOrFalse), ExpressionResultType.Boolean,
            nameof(ExpressionMessages.FunctionIsNullOrWhitespace), "IsNullOrWhitespace(mv[\"Name\"])",
            Value()),
        Function("HasValue", ExpressionFunctionCategory.Text, nameof(ExpressionMessages.ReturnsTrueOrFalse), ExpressionResultType.Boolean,
            nameof(ExpressionMessages.FunctionHasValue), "HasValue(mv[\"Manager\"])",
            Value()),
        Function("StartsWith", ExpressionFunctionCategory.Text, nameof(ExpressionMessages.ReturnsTrueOrFalse), ExpressionResultType.Boolean,
            nameof(ExpressionMessages.FunctionStartsWith), "StartsWith(mv[\"Email\"], \"admin\")",
            Value(), Param("prefix", nameof(ExpressionMessages.ParamPrefix)), IgnoreCase()),
        Function("EndsWith", ExpressionFunctionCategory.Text, nameof(ExpressionMessages.ReturnsTrueOrFalse), ExpressionResultType.Boolean,
            nameof(ExpressionMessages.FunctionEndsWith), "EndsWith(mv[\"Email\"], \"@company.com\")",
            Value(), Param("suffix", nameof(ExpressionMessages.ParamSuffix)), IgnoreCase()),
        Function("Contains", ExpressionFunctionCategory.Text, nameof(ExpressionMessages.ReturnsTrueOrFalse), ExpressionResultType.Boolean,
            nameof(ExpressionMessages.FunctionContains), "Contains(mv[\"Email\"], \"@company\")",
            Value(), Param("search", nameof(ExpressionMessages.ParamSearch)), IgnoreCase()),

        // Pattern
        Function("IsMatch", ExpressionFunctionCategory.Pattern, nameof(ExpressionMessages.ReturnsTrueOrFalse), ExpressionResultType.Boolean,
            nameof(ExpressionMessages.FunctionIsMatch),
            "IsMatch(cs[\"employeeId\"], \"^E[0-9]{6}$\")",
            Value(), Pattern(), IgnoreCase()),
        Function("RegexReplace", ExpressionFunctionCategory.Pattern, nameof(ExpressionMessages.ReturnsText), ExpressionResultType.Text,
            nameof(ExpressionMessages.FunctionRegexReplace),
            "RegexReplace(cs[\"phone\"], \"[^0-9+]\", \"\")",
            Value(), Pattern(), Param("replacement", nameof(ExpressionMessages.ParamReplacement)), IgnoreCase()),
        Function("RegexExtract", ExpressionFunctionCategory.Pattern, nameof(ExpressionMessages.ReturnsText), ExpressionResultType.Text,
            nameof(ExpressionMessages.FunctionRegexExtract),
            "RegexExtract(cs[\"mail\"], \"@(.+)$\", 1)",
            Value(), Pattern(), Param("group", nameof(ExpressionMessages.ParamGroup), isOptional: true)),

        // Conditional
        Function("IIF", ExpressionFunctionCategory.Conditional, nameof(ExpressionMessages.ReturnsEitherValue), ExpressionResultType.Unknown,
            nameof(ExpressionMessages.FunctionIIF), "IIF(Eq(mv[\"Status\"], \"Active\"), 512, 514)",
            Param("condition", nameof(ExpressionMessages.ParamCondition)), Param("trueValue", nameof(ExpressionMessages.ParamTrueValue)), Param("falseValue", nameof(ExpressionMessages.ParamFalseValue))),
        Function("Coalesce", ExpressionFunctionCategory.Conditional, nameof(ExpressionMessages.ReturnsEitherValue), ExpressionResultType.Unknown,
            nameof(ExpressionMessages.FunctionCoalesce),
            "Coalesce(mv[\"Preferred Name\"], mv[\"First Name\"])",
            Param("value1", nameof(ExpressionMessages.ParamFirstChoice)), Param("value2", nameof(ExpressionMessages.ParamOtherwise))),
        Function("Eq", ExpressionFunctionCategory.Conditional, nameof(ExpressionMessages.ReturnsTrueOrFalse), ExpressionResultType.Boolean,
            nameof(ExpressionMessages.FunctionEq),
            "Eq(cs[\"memberOf\"], \"CN=Admins\")",
            Param("value1", nameof(ExpressionMessages.ParamFirstValue)), Param("value2", nameof(ExpressionMessages.ParamCompareWith))),

        // Conversion
        Function("ToString", ExpressionFunctionCategory.Conversion, nameof(ExpressionMessages.ReturnsText), ExpressionResultType.Text, nameof(ExpressionMessages.FunctionToString), "ToString(mv[\"EmployeeId\"])",
            Value()),
        Function("ToInt", ExpressionFunctionCategory.Conversion, nameof(ExpressionMessages.ReturnsWholeNumber), ExpressionResultType.Number,
            nameof(ExpressionMessages.FunctionToInt), "ToInt(mv[\"Age\"])",
            Value()),
        Function("ToDecimal", ExpressionFunctionCategory.Conversion, nameof(ExpressionMessages.ReturnsDecimal), ExpressionResultType.Decimal,
            nameof(ExpressionMessages.FunctionToDecimal),
            "ToDecimal(cs[\"Salary\"])",
            Value()),

        // Date
        Function("Now", ExpressionFunctionCategory.Date, nameof(ExpressionMessages.ReturnsDate), ExpressionResultType.DateTime, nameof(ExpressionMessages.FunctionNow), "Now()", isRepeatable: false),
        Function("Today", ExpressionFunctionCategory.Date, nameof(ExpressionMessages.ReturnsDate), ExpressionResultType.DateTime, nameof(ExpressionMessages.FunctionToday), "Today()", isRepeatable: false),
        Function("FormatDate", ExpressionFunctionCategory.Date, nameof(ExpressionMessages.ReturnsText), ExpressionResultType.Text,
            nameof(ExpressionMessages.FunctionFormatDate),
            "FormatDate(mv[\"Hire Date\"], \"yyyy-MM-dd\")",
            Param("value", nameof(ExpressionMessages.ParamDateOrIso)), Param("format", nameof(ExpressionMessages.ParamDateFormatWrite))),
        Function("ParseDate", ExpressionFunctionCategory.Date, nameof(ExpressionMessages.ReturnsDate), ExpressionResultType.DateTime,
            nameof(ExpressionMessages.FunctionParseDate),
            "ParseDate(cs[\"hireDate\"], \"dd/MM/yyyy\")",
            Param("text", nameof(ExpressionMessages.ParamTextToRead)), Param("format", nameof(ExpressionMessages.ParamDateFormatRead))),
        Function("ToFileTime", ExpressionFunctionCategory.Date, nameof(ExpressionMessages.ReturnsLongNumber), ExpressionResultType.LongNumber,
            nameof(ExpressionMessages.FunctionToFileTime), "ToFileTime(mv[\"Account Expires\"])",
            Param("date", nameof(ExpressionMessages.ParamDateOrIso))),
        Function("FromFileTime", ExpressionFunctionCategory.Date, nameof(ExpressionMessages.ReturnsDate), ExpressionResultType.DateTime,
            nameof(ExpressionMessages.FunctionFromFileTime), "FromFileTime(cs[\"accountExpires\"])",
            Param("filetime", nameof(ExpressionMessages.ParamFileTime))),

        // Distinguished names
        Function("EscapeDN", ExpressionFunctionCategory.DistinguishedName, nameof(ExpressionMessages.ReturnsText), ExpressionResultType.Text,
            nameof(ExpressionMessages.FunctionEscapeDN),
            "\"CN=\" + EscapeDN(mv[\"Display Name\"]) + \",OU=Users,DC=domain,DC=local\"",
            Value()),

        // Passwords
        Function("RandomPassword", ExpressionFunctionCategory.Password, nameof(ExpressionMessages.ReturnsText), ExpressionResultType.Text,
            nameof(ExpressionMessages.FunctionRandomPassword), "RandomPassword(16, true)",
            Param("length", nameof(ExpressionMessages.ParamPasswordLength)), Param("extendedChars", nameof(ExpressionMessages.ParamExtendedChars)),
            isRepeatable: false),
        Function("RandomPassphrase", ExpressionFunctionCategory.Password, nameof(ExpressionMessages.ReturnsText), ExpressionResultType.Text,
            nameof(ExpressionMessages.FunctionRandomPassphrase), "RandomPassphrase(4, \"-\")",
            Param("wordCount", nameof(ExpressionMessages.ParamWordCount)), Param("separator", nameof(ExpressionMessages.ParamWordSeparator)),
            isRepeatable: false),

        // Multi-valued
        Function("Count", ExpressionFunctionCategory.MultiValued, nameof(ExpressionMessages.ReturnsWholeNumber), ExpressionResultType.Number, nameof(ExpressionMessages.FunctionCount),
            "Count(cs[\"proxyAddresses\"])",
            Values()),
        Function("Min", ExpressionFunctionCategory.MultiValued, nameof(ExpressionMessages.ReturnsOneOfTheValues), ExpressionResultType.Unknown,
            nameof(ExpressionMessages.FunctionMin), "Min(mv[\"Start Dates\"])",
            Values()),
        Function("Max", ExpressionFunctionCategory.MultiValued, nameof(ExpressionMessages.ReturnsOneOfTheValues), ExpressionResultType.Unknown,
            nameof(ExpressionMessages.FunctionMax), "Max(cs[\"employeeNumbers\"])",
            Values()),
        Function("Where", ExpressionFunctionCategory.MultiValued, nameof(ExpressionMessages.ReturnsValuesThatMeetIt), ExpressionResultType.Unknown,
            nameof(ExpressionMessages.FunctionWhere),
            "Where(cs[\"proxyAddresses\"], \"starts with\", \"SMTP:\")",
            Values(), Param("operator", nameof(ExpressionMessages.ParamOperator)), Param("value", nameof(ExpressionMessages.ParamCompareEach)),
            IgnoreCase()),
        Function("Split", ExpressionFunctionCategory.MultiValued, nameof(ExpressionMessages.ReturnsSeveralValues), ExpressionResultType.Text,
            nameof(ExpressionMessages.FunctionSplit), "Split(cs[\"coursesCompleted\"], \"|\")",
            Value(), Param("delimiter", nameof(ExpressionMessages.ParamSplitDelimiter))),
        Function("Join", ExpressionFunctionCategory.MultiValued, nameof(ExpressionMessages.ReturnsText), ExpressionResultType.Text,
            nameof(ExpressionMessages.FunctionJoin), "Join(mv[\"Groups\"], \",\")",
            Values(), Param("delimiter", nameof(ExpressionMessages.ParamJoinDelimiter))),

        // Account control
        Function("EnableUser", ExpressionFunctionCategory.AccountControl, nameof(ExpressionMessages.ReturnsWholeNumber), ExpressionResultType.Number, nameof(ExpressionMessages.FunctionEnableUser),
            "EnableUser(cs[\"userAccountControl\"])",
            Param("uac", nameof(ExpressionMessages.ParamUac))),
        Function("DisableUser", ExpressionFunctionCategory.AccountControl, nameof(ExpressionMessages.ReturnsWholeNumber), ExpressionResultType.Number, nameof(ExpressionMessages.FunctionDisableUser),
            "DisableUser(cs[\"userAccountControl\"])",
            Param("uac", nameof(ExpressionMessages.ParamUac))),
        Function("SetBit", ExpressionFunctionCategory.AccountControl, nameof(ExpressionMessages.ReturnsWholeNumber), ExpressionResultType.Number, nameof(ExpressionMessages.FunctionSetBit), "SetBit(mv[\"uac\"], 65536)",
            Param("value", nameof(ExpressionMessages.ParamFlagsNumber)), Param("bit", nameof(ExpressionMessages.ParamFlagValueLarge))),
        Function("ClearBit", ExpressionFunctionCategory.AccountControl, nameof(ExpressionMessages.ReturnsWholeNumber), ExpressionResultType.Number, nameof(ExpressionMessages.FunctionClearBit), "ClearBit(mv[\"uac\"], 65536)",
            Param("value", nameof(ExpressionMessages.ParamFlagsNumber)), Param("bit", nameof(ExpressionMessages.ParamFlagValueLarge))),
        Function("HasBit", ExpressionFunctionCategory.AccountControl, nameof(ExpressionMessages.ReturnsTrueOrFalse), ExpressionResultType.Boolean, nameof(ExpressionMessages.FunctionHasBit),
            "HasBit(cs[\"userAccountControl\"], 2)",
            Param("value", nameof(ExpressionMessages.ParamFlagsNumber)), Param("bit", nameof(ExpressionMessages.ParamFlagValueSmall))),
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
    public static string CategoryName(ExpressionFunctionCategory category) =>
        CategoryNameDetail(category)?.ToEnglish() ?? category.ToString();

    /// <summary>
    /// <see cref="CategoryName"/> as a message, so the portal's Insert function list can head each group in the viewer's
    /// language; null for a category with no name.
    /// </summary>
    public static JimMessage? CategoryNameDetail(ExpressionFunctionCategory category) => category switch
    {
        ExpressionFunctionCategory.Text => Say(nameof(ExpressionMessages.CategoryText)),
        ExpressionFunctionCategory.Pattern => Say(nameof(ExpressionMessages.CategoryPattern)),
        ExpressionFunctionCategory.Conditional => Say(nameof(ExpressionMessages.CategoryConditional)),
        ExpressionFunctionCategory.Conversion => Say(nameof(ExpressionMessages.CategoryConversion)),
        ExpressionFunctionCategory.Date => Say(nameof(ExpressionMessages.CategoryDate)),
        ExpressionFunctionCategory.DistinguishedName => Say(nameof(ExpressionMessages.CategoryDistinguishedName)),
        ExpressionFunctionCategory.Password => Say(nameof(ExpressionMessages.CategoryPassword)),
        ExpressionFunctionCategory.MultiValued => Say(nameof(ExpressionMessages.CategoryMultiValued)),
        ExpressionFunctionCategory.AccountControl => Say(nameof(ExpressionMessages.CategoryAccountControl)),
        _ => null
    };

    private static JimMessage Say(string key) => JimMessage.Create(ApplicationMessageCatalogues.Expressions, key);

    private static string English(string key) => Say(key).ToEnglish();

    /// <summary>
    /// A function whose text is held as messages: <see cref="ExpressionFunction.Description"/> and
    /// <see cref="ExpressionFunction.Returns"/> are the messages' British English, which the documentation and the REST
    /// API read, and the messages themselves are for the portal.
    /// </summary>
    private static ExpressionFunction Build(string name, ExpressionFunctionCategory category, ExpressionFunctionParameter[] parameters,
        string returnsKey, ExpressionResultType resultType, string descriptionKey, string example, bool isRepeatable) =>
        new(name, category, parameters, English(returnsKey), resultType, English(descriptionKey), example, isRepeatable)
        {
            DescriptionDetail = Say(descriptionKey),
            ReturnsDetail = Say(returnsKey)
        };

    private static ExpressionFunction Function(string name, ExpressionFunctionCategory category, string returnsKey, ExpressionResultType resultType, string descriptionKey,
        string example, params ExpressionFunctionParameter[] parameters) =>
        Build(name, category, parameters, returnsKey, resultType, descriptionKey, example, true);

    private static ExpressionFunction Function(string name, ExpressionFunctionCategory category, string returnsKey, ExpressionResultType resultType, string descriptionKey,
        string example, bool isRepeatable) =>
        Build(name, category, [], returnsKey, resultType, descriptionKey, example, isRepeatable);

    private static ExpressionFunction Function(string name, ExpressionFunctionCategory category, string returnsKey, ExpressionResultType resultType, string descriptionKey,
        string example, ExpressionFunctionParameter first, ExpressionFunctionParameter second, bool isRepeatable) =>
        Build(name, category, [first, second], returnsKey, resultType, descriptionKey, example, isRepeatable);

    private static ExpressionFunctionParameter Param(string name, string descriptionKey, bool isOptional = false) =>
        new(name, English(descriptionKey), isOptional) { DescriptionDetail = Say(descriptionKey) };

    private static ExpressionFunctionParameter Value() =>
        Param("value", nameof(ExpressionMessages.ParamValue));

    private static ExpressionFunctionParameter Values() =>
        Param("values", nameof(ExpressionMessages.ParamValues));

    private static ExpressionFunctionParameter Pattern() =>
        Param("pattern", nameof(ExpressionMessages.ParamPattern));

    private static ExpressionFunctionParameter IgnoreCase() =>
        Param("ignoreCase", nameof(ExpressionMessages.ParamIgnoreCase), isOptional: true);
}
