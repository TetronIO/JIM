// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Text;
using JIM.Application.Localisation;
using JIM.Application.Resources;
using JIM.Models.Core;
using JIM.Models.Localisation;
using JIM.Models.Logic.Scoping;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Utilities;

namespace JIM.Application.Servers.Scoping;

/// <summary>
/// Turns a scoping explanation into words (#348): the one-line hint, the "To come into scope" bullets, the copyable
/// summary and the descriptions of the tree's lines. Generated once, on the server, so the portal, the REST API and
/// PowerShell say exactly the same thing.
/// </summary>
/// <remarks>
/// Comparisons are worded with the criteria editors' labels, in two forms: a requirement for a line of its own ("must
/// equal", "must be before") and a condition inside a choice ("equals", "is before"). Values are culture-invariant and
/// dates are UTC, so the text is the same on every surface and unambiguous when forwarded; text values are quoted in
/// plain text so their boundaries survive being pasted. Values of credential attributes never appear: the explanation
/// withholds them and the words say so.
///
/// Every sentence is built as a <see cref="JimMessage"/> first (#3030), so the portal can show it in the viewer's
/// language, and its British English is what the REST API and PowerShell read: the string-returning methods are that
/// English. A sentence is assembled from short phrases in the order a reader takes them in, and each language words
/// each phrase; values (attribute names, expected and current values) are data and are never translated.
/// </remarks>
internal static class ScopingExplanationSummariser
{
    /// <summary>
    /// Line breaks in generated text are always "\n", whatever the server's platform, so every surface returns
    /// identical text.
    /// </summary>
    private const string LineBreak = "\n";

    private enum Form { Requirement, Condition }

    #region Message helpers

    private static JimMessage M(string key, params MessageArgument[] arguments) =>
        JimMessage.Create(ApplicationMessageCatalogues.Scoping, key, arguments);

    /// <summary>Text shown as it is: a value, a name, punctuation, or English produced elsewhere.</summary>
    private static JimMessage V(string text) => M(nameof(ScopingMessages.Verbatim), text);

    private static JimMessage Concat(JimMessage first, JimMessage second) => M(nameof(ScopingMessages.Concat), first, second);

    private static JimMessage Concat(params JimMessage[] parts) => parts.Aggregate(Concat);

    /// <summary>The messages one after another, with <paramref name="separator"/> between them.</summary>
    private static JimMessage Join(IEnumerable<JimMessage> parts, JimMessage separator)
    {
        var list = parts.ToList();
        return list.Count == 0 ? V(string.Empty) : list.Aggregate((joined, next) => Concat(joined, separator, next));
    }

    /// <summary>A word with a space either side, for joining items: " or ", " and ".</summary>
    private static JimMessage Spaced(JimMessage word) => Concat(V(" "), word, V(" "));

    /// <summary>
    /// A sentence's reason in the form it is nested: the criterion's own message where it has one, else its English.
    /// </summary>
    private static JimMessage ReasonOf(ScopingCriterionExplanation criterion) =>
        criterion.FailureReasonDetail ?? V(criterion.FailureReason ?? string.Empty);

    #endregion

    #region Tree lines

    /// <summary>A criterion as one line of the tree, for example "Department equals Finance".</summary>
    internal static string DescribeCriterion(ScopingCriterionExplanation criterion) =>
        DescribeCriterionMessage(criterion).ToEnglish();

    /// <summary><see cref="DescribeCriterion"/> as a message.</summary>
    internal static JimMessage DescribeCriterionMessage(ScopingCriterionExplanation criterion)
    {
        if (criterion.Outcome == ScopingCriterionOutcome.AttributeMissing || criterion.AttributeName == null)
            return M(nameof(ScopingMessages.CriterionWithNoAttribute));

        var line = new LineBuilder();
        AppendComparison(line, criterion, Form.Condition, includeCurrent: false);
        if (criterion.AttributeType == AttributeDataType.Text && !criterion.CaseSensitive)
        {
            line.Raw(" (");
            line.Text(M(nameof(ScopingMessages.IgnoringCase)));
            line.Raw(")");
        }
        return line.UnquotedMessage;
    }

    /// <summary>What the object held, for the tree, for example "is Engineering" or "has no value".</summary>
    internal static string DescribeActual(ScopingCriterionExplanation criterion) =>
        DescribeActualMessage(criterion).ToEnglish();

    /// <summary><see cref="DescribeActual"/> as a message.</summary>
    internal static JimMessage DescribeActualMessage(ScopingCriterionExplanation criterion)
    {
        if (criterion.Outcome == ScopingCriterionOutcome.AttributeMissing)
            return M(nameof(ScopingMessages.ActualReferencesNoAttribute));
        if (criterion.Outcome == ScopingCriterionOutcome.Invalid)
            return criterion.FailureReason == null
                ? M(nameof(ScopingMessages.ActualCannotBeEvaluated))
                : M(nameof(ScopingMessages.ActualCannotBeEvaluatedBecause), ReasonOf(criterion));

        if (criterion.ConnectedObjectTypeId.HasValue)
            return HasConnection(criterion)
                ? M(nameof(ScopingMessages.ActualHasConnection))
                : M(nameof(ScopingMessages.ActualHasNoConnection));

        // An expression criterion's value is the one it computed (#2054), and saying "is" would read as an attribute's.
        var computed = criterion.Expression != null;
        if (criterion.Masked)
            return computed ? M(nameof(ScopingMessages.ActualComputedHidden)) : M(nameof(ScopingMessages.ActualIsHidden));
        if (criterion.ValueCount == 0)
            return computed ? M(nameof(ScopingMessages.ActualComputedNoValue)) : M(nameof(ScopingMessages.ActualHasNoValue));
        if (criterion.ValueCount == 1)
            return computed
                ? M(nameof(ScopingMessages.ActualComputedValue), criterion.ActualDisplay ?? string.Empty)
                : M(nameof(ScopingMessages.ActualIsValue), criterion.ActualDisplay ?? string.Empty);

        // Several values (#1923): name the one that decided it, or say that none matched.
        var count = criterion.ValueCount.ToString(CultureInfo.InvariantCulture);
        if (criterion.ActualDisplay != null)
            return computed
                ? M(nameof(ScopingMessages.ActualComputedOneOf), criterion.ActualDisplay, count)
                : M(nameof(ScopingMessages.ActualIncludesOneOf), criterion.ActualDisplay, count);

        var expected = criterion.ExpectedDisplay ?? criterion.RelativeDisplay;
        if (expected == null)
            return computed
                ? M(nameof(ScopingMessages.ActualComputedCount), count)
                : M(nameof(ScopingMessages.ActualHasCount), count);

        var (condition, _) = Wording(PositiveForm(criterion.ComparisonType), criterion.AttributeType);
        return computed
            ? M(nameof(ScopingMessages.ActualNoneOfComputedValues), count, condition, expected)
            : M(nameof(ScopingMessages.ActualNoneOfValues), count, condition, expected);
    }

    /// <summary>Whether the object has the connection a decided connection criterion tests for.</summary>
    private static bool HasConnection(ScopingCriterionExplanation criterion) =>
        (criterion.Outcome == ScopingCriterionOutcome.Met) == (criterion.ComparisonType == SearchComparisonType.HasValue);

    /// <summary>A group as one line of the tree, for example "All of these must be met (not met)".</summary>
    internal static string DescribeGroup(ScopingGroupExplanation group) => DescribeGroupMessage(group).ToEnglish();

    /// <summary><see cref="DescribeGroup"/> as a message.</summary>
    internal static JimMessage DescribeGroupMessage(ScopingGroupExplanation group)
    {
        var heading = group.Type == SearchGroupType.All
            ? M(nameof(ScopingMessages.GroupAllMustBeMet))
            : M(nameof(ScopingMessages.GroupAnyOneOfThese));
        var state = group.Met switch
        {
            null => M(nameof(ScopingMessages.GroupStateCannotBeEvaluated)),
            true => M(nameof(ScopingMessages.GroupStateMet)),
            false => group.Type == SearchGroupType.Any
                ? M(nameof(ScopingMessages.GroupStateNoneMet))
                : M(nameof(ScopingMessages.GroupStateNotMet))
        };
        return M(nameof(ScopingMessages.GroupLine), heading, state);
    }

    #endregion

    #region Text carried on every explanation

    /// <summary>
    /// Writes the words every surface shows onto an explanation: its hint and each line of its tree. Done once, on the
    /// server, so the portal, the REST API and PowerShell cannot word an explanation differently.
    /// </summary>
    internal static void Describe(ScopingExplanation explanation)
    {
        var hint = explanation.Outcome switch
        {
            ScopingRuleOutcome.OutOfScope => FailingAttributesHintMessage(explanation),
            ScopingRuleOutcome.Undetermined => InvalidCriteriaHintMessage(explanation),
            _ => null
        };
        explanation.HintDetail = hint;
        explanation.Hint = hint?.ToEnglish() ?? string.Empty;

        foreach (var group in explanation.Groups)
            DescribeTree(group);
    }

    private static void DescribeTree(ScopingGroupExplanation group)
    {
        group.DescriptionDetail = DescribeGroupMessage(group);
        group.Description = group.DescriptionDetail.ToEnglish();
        foreach (var criterion in group.Criteria)
        {
            criterion.DescriptionDetail = DescribeCriterionMessage(criterion);
            criterion.Description = criterion.DescriptionDetail.ToEnglish();
            criterion.ActualDescriptionDetail = DescribeActualMessage(criterion);
            criterion.ActualDescription = criterion.ActualDescriptionDetail.ToEnglish();
        }

        foreach (var child in group.ChildGroups)
            DescribeTree(child);
    }

    /// <summary>
    /// Why an enabled export rule cannot connect a Metaverse Object whose one slot in the rule's Connected System is held
    /// by an object of another type, in the terms synchronisation reports it (#1331).
    /// </summary>
    internal static string DescribeObjectTypeConflict(string syncRuleName, string targetObjectTypeName, string existingObjectTypeName,
        string connectedSystemName) =>
        DescribeObjectTypeConflictMessage(syncRuleName, targetObjectTypeName, existingObjectTypeName, connectedSystemName).ToEnglish();

    /// <summary><see cref="DescribeObjectTypeConflict"/> as a message.</summary>
    internal static JimMessage DescribeObjectTypeConflictMessage(string syncRuleName, string targetObjectTypeName,
        string existingObjectTypeName, string connectedSystemName) =>
        M(nameof(ScopingMessages.ObjectTypeConflict), syncRuleName, targetObjectTypeName, connectedSystemName, existingObjectTypeName);

    /// <summary>
    /// How a connection was joined, in one sentence: by which method and which Synchronisation Rule, or that it was
    /// not recorded. A recorded rule since deleted is named as it was, and says so.
    /// </summary>
    internal static string DescribeJoin(JoinRecord record) => DescribeJoinMessage(record).ToEnglish();

    /// <summary><see cref="DescribeJoin"/> as a message.</summary>
    internal static JimMessage DescribeJoinMessage(JoinRecord record)
    {
        var rule = record.SyncRuleName == null
            ? null
            : record.Source == JoinRecordSource.Recorded && record.SyncRuleId == null
                ? M(nameof(ScopingMessages.JoinRuleSinceDeleted), record.SyncRuleName)
                : M(nameof(ScopingMessages.JoinRule), record.SyncRuleName);

        return (record.Method, rule) switch
        {
            (ConnectedSystemObjectJoinMethod.Projection, not null) => M(nameof(ScopingMessages.JoinProjectedBy), rule),
            (ConnectedSystemObjectJoinMethod.Provisioning, not null) => M(nameof(ScopingMessages.JoinProvisionedBy), rule),
            (ConnectedSystemObjectJoinMethod.InboundMatching, not null) =>
                M(nameof(ScopingMessages.JoinMatchedUnder), rule, DescribeMatch(record)),
            (ConnectedSystemObjectJoinMethod.InboundMatching, null) when record.Source == JoinRecordSource.Recorded =>
                M(nameof(ScopingMessages.JoinMatchedOnRules), DescribeMatch(record)),
            (ConnectedSystemObjectJoinMethod.ExportMatching, not null) =>
                M(nameof(ScopingMessages.JoinExportMatched), rule),
            _ => record.JoinType switch
            {
                ConnectedSystemObjectJoinType.Projected => M(nameof(ScopingMessages.JoinProjectedNotRecorded)),
                ConnectedSystemObjectJoinType.Provisioned => M(nameof(ScopingMessages.JoinProvisionedNotRecorded)),
                _ => M(nameof(ScopingMessages.JoinNotRecorded))
            }
        };
    }

    /// <summary>
    /// The clause naming what a matching join matched on, or nothing when the join recorded no value (it predates
    /// recording). A rule since deleted still has its value, and says so.
    /// </summary>
    private static JimMessage DescribeMatch(JoinRecord record)
    {
        if (record.MatchValue == null)
            return V(string.Empty);

        return record.MatchedAttributeName == null || record.MatchSource == null
            ? M(nameof(ScopingMessages.MatchedRuleDeleted), record.MatchValue)
            : M(nameof(ScopingMessages.MatchedOn), record.MatchValue, record.MatchedAttributeName, record.MatchSource);
    }

    #endregion

    #region Hint

    /// <summary>
    /// The attributes an out-of-scope object fails on, without their values: "Fails on Department; Cost Centre or Job
    /// Title". A semicolon separates requirements that must all be met; "or" separates alternatives. Empty when the
    /// object is not out of scope.
    /// </summary>
    internal static string FailingAttributesHint(ScopingExplanation explanation) =>
        FailingAttributesHintMessage(explanation)?.ToEnglish() ?? string.Empty;

    /// <summary><see cref="FailingAttributesHint"/> as a message; null when the object is not out of scope.</summary>
    internal static JimMessage? FailingAttributesHintMessage(ScopingExplanation explanation)
    {
        if (explanation.Outcome != ScopingRuleOutcome.OutOfScope || explanation.Groups.Count == 0)
            return null;

        var text = explanation.Groups.Count == 1
            ? HintFor(explanation.Groups[0], insideChoice: false)
            : Join(DistinctTerms(explanation.Groups.Select(g => HintFor(g, insideChoice: true))), Spaced(M(nameof(ScopingMessages.Or))));
        return M(nameof(ScopingMessages.FailsOn), text);
    }

    private static JimMessage HintFor(ScopingGroupExplanation group, bool insideChoice)
    {
        var terms = DistinctTerms(FailingCriteria(group).Select(HintTerm)
            .Concat(FailingChildGroups(group).Select(child => HintFor(child, insideChoice: group.Type == SearchGroupType.Any))));

        if (group.Type == SearchGroupType.Any)
            return Join(terms, Spaced(M(nameof(ScopingMessages.Or))));

        var requirements = Join(terms, V("; "));
        return insideChoice && terms.Count > 1 ? Concat(V("("), requirements, V(")")) : requirements;
    }

    private static JimMessage HintTerm(ScopingCriterionExplanation criterion) =>
        criterion.AttributeName != null
            ? V(criterion.AttributeName)
            : M(nameof(ScopingMessages.HintCriterionPath), criterion.Path);

    #endregion

    #region To come into scope

    /// <summary>
    /// What an out-of-scope object needs to come into scope, one bullet per failing branch: each failing criterion of
    /// an All group is a requirement, a failing Any group offers its alternatives, and several top-level groups are
    /// presented as alternatives. Empty when the object is not out of scope.
    /// </summary>
    internal static List<ExplanationBullet> ToComeIntoScope(ScopingExplanation explanation)
    {
        var bullets = new List<ExplanationBullet>();
        if (explanation.Outcome != ScopingRuleOutcome.OutOfScope || explanation.Groups.Count == 0)
            return bullets;

        if (explanation.Groups.Count == 1)
        {
            AddRequirementBullets(explanation.Groups[0], bullets);
            return bullets;
        }

        for (var i = 0; i < explanation.Groups.Count; i++)
        {
            var line = new LineBuilder();
            line.Text(i == 0 ? M(nameof(ScopingMessages.Either)) : M(nameof(ScopingMessages.OrLeading)));
            line.Raw(" ");
            AppendGroupCondition(line, explanation.Groups[i], insideChoice: false);
            bullets.Add(line.ToBullet());
        }
        return bullets;
    }

    /// <summary>A failing group whose requirements stand on their own lines.</summary>
    private static void AddRequirementBullets(ScopingGroupExplanation group, List<ExplanationBullet> bullets)
    {
        if (group.Type == SearchGroupType.Any)
        {
            var alternatives = Alternatives(group);
            if (alternatives.Count == 1)
                AddAlternativeAsRequirement(alternatives[0], bullets);
            else
                bullets.Add(ChoiceBullet(alternatives, joinsEarlierRequirements: bullets.Count > 0));
            return;
        }

        foreach (var criterion in FailingCriteria(group))
        {
            var line = new LineBuilder();
            AppendCriterion(line, criterion, Form.Requirement, startsLine: true);
            bullets.Add(line.ToBullet());
        }
        foreach (var child in FailingChildGroups(group))
            AddRequirementBullets(child, bullets);
    }

    /// <summary>
    /// The only alternative of a choice, which is therefore simply required: a criterion on a line of its own, or an
    /// All group's requirements.
    /// </summary>
    private static void AddAlternativeAsRequirement(object node, List<ExplanationBullet> bullets)
    {
        switch (node)
        {
            case ScopingCriterionExplanation criterion:
                var line = new LineBuilder();
                AppendCriterion(line, criterion, Form.Requirement, startsLine: true);
                bullets.Add(line.ToBullet());
                break;
            case ScopingGroupExplanation group:
                AddRequirementBullets(group, bullets);
                break;
        }
    }

    /// <summary>
    /// "either A, or B": a failing Any group's alternatives on one line, opened with "and" when it follows other
    /// requirements, so it is not read as an alternative to them.
    /// </summary>
    private static ExplanationBullet ChoiceBullet(IReadOnlyList<object> alternatives, bool joinsEarlierRequirements)
    {
        var line = new LineBuilder();
        line.Text(joinsEarlierRequirements ? M(nameof(ScopingMessages.AndEither)) : M(nameof(ScopingMessages.Either)));
        line.Raw(" ");
        for (var i = 0; i < alternatives.Count; i++)
        {
            if (i > 0)
            {
                line.Raw(", ");
                line.Text(M(nameof(ScopingMessages.OrAlternative)));
                line.Raw(" ");
            }
            AppendAlternative(line, alternatives[i]);
        }
        return line.ToBullet();
    }

    private static void AppendAlternative(LineBuilder line, object alternative)
    {
        switch (alternative)
        {
            case ScopingCriterionExplanation criterion:
                AppendCriterion(line, criterion, Form.Condition, startsLine: false);
                break;
            case ScopingGroupExplanation group:
                AppendGroupCondition(line, group, insideChoice: false);
                break;
        }
    }

    /// <summary>
    /// A failing group as a condition within a sentence: an All group's failing requirements joined by "and", an Any
    /// group's alternatives joined by "or". A nested choice inside requirements, or requirements inside a choice, are
    /// parenthesised so the sentence cannot be misread.
    /// </summary>
    private static void AppendGroupCondition(LineBuilder line, ScopingGroupExplanation group, bool insideChoice)
    {
        if (group.Type == SearchGroupType.Any)
        {
            var alternatives = Alternatives(group);
            for (var i = 0; i < alternatives.Count; i++)
            {
                if (i > 0)
                    line.Spaced(M(nameof(ScopingMessages.Or)));
                if (alternatives[i] is ScopingCriterionExplanation criterion)
                    AppendCriterion(line, criterion, Form.Condition, startsLine: false);
                else
                    AppendGroupCondition(line, (ScopingGroupExplanation)alternatives[i], insideChoice: true);
            }
            return;
        }

        var requirements = new List<object>();
        requirements.AddRange(FailingCriteria(group));
        requirements.AddRange(FailingChildGroups(group));
        var parenthesise = insideChoice && requirements.Count > 1;
        if (parenthesise)
            line.Raw("(");
        for (var i = 0; i < requirements.Count; i++)
        {
            if (i > 0)
                line.Spaced(M(nameof(ScopingMessages.And)));
            switch (requirements[i])
            {
                case ScopingCriterionExplanation criterion:
                    AppendCriterion(line, criterion, Form.Condition, startsLine: false);
                    break;
                case ScopingGroupExplanation { Type: SearchGroupType.Any } choice when Alternatives(choice).Count > 1:
                    line.Raw("(");
                    AppendGroupCondition(line, choice, insideChoice: false);
                    line.Raw(")");
                    break;
                case ScopingGroupExplanation child:
                    AppendGroupCondition(line, child, insideChoice: false);
                    break;
            }
        }
        if (parenthesise)
            line.Raw(")");
    }

    /// <summary>
    /// A failing Any group's alternatives in evaluation order, a nested Any group's own alternatives taken in place
    /// (a choice within a choice is one choice).
    /// </summary>
    private static List<object> Alternatives(ScopingGroupExplanation group)
    {
        var alternatives = new List<object>();
        alternatives.AddRange(FailingCriteria(group));
        foreach (var child in FailingChildGroups(group))
        {
            if (child.Type == SearchGroupType.Any)
                alternatives.AddRange(Alternatives(child));
            else
                alternatives.Add(child);
        }
        return alternatives;
    }

    #endregion

    #region Criterion wording

    /// <summary>
    /// One criterion that is not met, as a requirement ("Department must equal Finance (currently Engineering)") or a
    /// condition within a sentence ("Department equals Finance (currently Engineering)").
    /// </summary>
    private static void AppendCriterion(LineBuilder line, ScopingCriterionExplanation criterion, Form form, bool startsLine)
    {
        if (criterion.Outcome == ScopingCriterionOutcome.AttributeMissing || criterion.AttributeName == null)
        {
            line.Text(M(startsLine ? nameof(ScopingMessages.CriterionHasNoAttributeStart) : nameof(ScopingMessages.CriterionHasNoAttributeMid),
                criterion.Path));
            return;
        }

        AppendComparison(line, criterion, form, includeCurrent: true);
    }

    private static void AppendComparison(LineBuilder line, ScopingCriterionExplanation criterion, Form form, bool includeCurrent)
    {
        // A connection (#2021) is had or not had, never valued: "Has user in PANOPLY AD", "Needs no contact in RESURGAM AD".
        // The Object Type's name is an identifier, so it takes no article, as the criterion's own description reads.
        if (criterion.ConnectedObjectTypeId.HasValue)
        {
            var wantsOne = criterion.ComparisonType == SearchComparisonType.HasValue;
            line.Text(form == Form.Requirement
                ? M(wantsOne ? nameof(ScopingMessages.ConnectionNeeds) : nameof(ScopingMessages.ConnectionNeedsNo))
                : M(wantsOne ? nameof(ScopingMessages.ConnectionHas) : nameof(ScopingMessages.ConnectionHasNo)));
            line.Raw(" ");
            line.Attribute(criterion.AttributeName!);
            if (includeCurrent && criterion.Outcome is ScopingCriterionOutcome.Met or ScopingCriterionOutcome.NotMet)
            {
                line.Raw(" (");
                line.Text(HasConnection(criterion)
                    ? M(nameof(ScopingMessages.CurrentlyHasConnection))
                    : M(nameof(ScopingMessages.CurrentlyHasNoConnection)));
                line.Raw(")");
            }
            return;
        }

        var (condition, requirement) = Wording(criterion.ComparisonType, criterion.AttributeType);
        var verb = form == Form.Requirement ? requirement : condition;
        var quote = criterion.AttributeType == AttributeDataType.Text;
        var noValue = criterion.Outcome == ScopingCriterionOutcome.NoValue;

        line.Attribute(criterion.AttributeName!);

        if (criterion.Masked)
        {
            line.Raw(" ");
            line.Text(verb);
            line.Raw(" ");
            line.Value(ExplanationSegmentKind.Hidden, M(nameof(ScopingMessages.AHiddenValue)));
            if (includeCurrent)
            {
                line.Raw(" (");
                line.Value(ExplanationSegmentKind.Hidden, M(nameof(ScopingMessages.CurrentValueHidden)));
                line.Raw(")");
            }
            return;
        }

        // The presence operators compare against nothing, so there is no expected value to show.
        if (criterion.ComparisonType == SearchComparisonType.HasNoValue)
        {
            line.Raw(" ");
            line.Text(form == Form.Requirement ? M(nameof(ScopingMessages.MustHaveNoValue)) : M(nameof(ScopingMessages.HasNoValueCondition)));
            if (includeCurrent && !noValue)
                AppendCurrent(line, criterion, quote, caseIsTheReason: false);
            return;
        }
        if (criterion.ComparisonType == SearchComparisonType.HasValue)
        {
            line.Raw(" ");
            line.Text(form == Form.Requirement ? M(nameof(ScopingMessages.NeedsAValue)) : M(nameof(ScopingMessages.HasAValue)));
            return;
        }

        // A negated comparison fails on a missing value only because there is no value: the value is what is needed.
        if (includeCurrent && noValue && SearchComparisonOperators.IsNegated(criterion.ComparisonType))
        {
            line.Raw(" ");
            line.Text(form == Form.Requirement
                ? M(nameof(ScopingMessages.NeedsAValueThat), condition)
                : M(nameof(ScopingMessages.HasAValueThat), condition));
            line.Raw(" ");
            AppendExpected(line, criterion, quote);
            return;
        }

        line.Raw(" ");
        line.Text(verb);
        line.Raw(" ");
        AppendExpected(line, criterion, quote);
        if (includeCurrent)
            AppendCurrent(line, criterion, quote, caseIsTheReason: DiffersOnlyByCase(criterion));
    }

    private static void AppendExpected(LineBuilder line, ScopingCriterionExplanation criterion, bool quote)
    {
        if (criterion.RelativeDisplay != null)
        {
            line.Value(ExplanationSegmentKind.ExpectedValue, criterion.RelativeDisplay, quote: false);
            if (criterion.ExpectedDisplay != null)
            {
                line.Raw(", ");
                line.Value(ExplanationSegmentKind.ExpectedValue, criterion.ExpectedDisplay, quote: false);
            }
            return;
        }

        if (criterion.ExpectedDisplay == null)
            line.Text(M(nameof(ScopingMessages.AnEmptyValue)));
        else
            line.Value(ExplanationSegmentKind.ExpectedValue, criterion.ExpectedDisplay, quote);
    }

    private static void AppendCurrent(LineBuilder line, ScopingCriterionExplanation criterion, bool quote, bool caseIsTheReason)
    {
        if (criterion.ValueCount > 1)
        {
            AppendCurrentOfSeveral(line, criterion, quote);
            return;
        }

        line.Raw(" (");
        line.Text(caseIsTheReason ? M(nameof(ScopingMessages.CaseSensitiveCurrently)) : M(nameof(ScopingMessages.Currently)));
        line.Raw(" ");
        if (criterion.ActualDisplay == null)
            line.Value(ExplanationSegmentKind.NoValue, M(nameof(ScopingMessages.NoValueWord)));
        else
            line.Value(ExplanationSegmentKind.CurrentValue, criterion.ActualDisplay, quote);
        line.Raw(")");
    }

    /// <summary>
    /// What an object holding several values for the attribute has (#1923), for a criterion it fails: the value standing
    /// in the way of a negated one, or that none of its values meets a positive one.
    /// </summary>
    private static void AppendCurrentOfSeveral(LineBuilder line, ScopingCriterionExplanation criterion, bool quote)
    {
        var count = criterion.ValueCount.ToString(CultureInfo.InvariantCulture);
        if (criterion.ActualDisplay != null)
        {
            line.Raw(" (");
            line.Text(M(nameof(ScopingMessages.CurrentlyIncludes)));
            line.Raw(" ");
            line.Value(ExplanationSegmentKind.CurrentValue, criterion.ActualDisplay, quote);
            line.Raw(", ");
            line.Text(M(nameof(ScopingMessages.OneOfCountValues), count));
            line.Raw(")");
        }
        else if (criterion.ExpectedDisplay == null && criterion.RelativeDisplay == null)
        {
            line.Raw(" (");
            line.Text(M(nameof(ScopingMessages.CurrentlyCountValues), count));
            line.Raw(")");
        }
        else
        {
            line.Raw(" (");
            line.Text(M(nameof(ScopingMessages.NoneOfItsValuesMatches), count));
            line.Raw(")");
        }
    }

    /// <summary>
    /// The criteria editors' label for the comparison as a condition ("equals", "is before") and as a requirement
    /// ("must equal", "must be before").
    /// </summary>
    private static (JimMessage Condition, JimMessage Requirement) Wording(SearchComparisonType comparison, AttributeDataType? type)
    {
        // The verbs: a label that reads as a verb has a condition and a requirement of its own. Where no data type is
        // known the label is the operator's own name, which reads as a verb for only some of them.
        if (type.HasValue || NameReadsAsVerb(comparison))
        switch (comparison)
        {
            case SearchComparisonType.Equals: return (M(nameof(ScopingMessages.OpEquals)), M(nameof(ScopingMessages.MustEqual)));
            case SearchComparisonType.NotEquals: return (M(nameof(ScopingMessages.OpNotEquals)), M(nameof(ScopingMessages.MustNotEqual)));
            case SearchComparisonType.StartsWith: return (M(nameof(ScopingMessages.OpStartsWith)), M(nameof(ScopingMessages.MustStartWith)));
            case SearchComparisonType.NotStartsWith: return (M(nameof(ScopingMessages.OpNotStartsWith)), M(nameof(ScopingMessages.MustNotStartWith)));
            case SearchComparisonType.EndsWith: return (M(nameof(ScopingMessages.OpEndsWith)), M(nameof(ScopingMessages.MustEndWith)));
            case SearchComparisonType.NotEndsWith: return (M(nameof(ScopingMessages.OpNotEndsWith)), M(nameof(ScopingMessages.MustNotEndWith)));
            case SearchComparisonType.Contains: return (M(nameof(ScopingMessages.OpContains)), M(nameof(ScopingMessages.MustContain)));
            case SearchComparisonType.NotContains: return (M(nameof(ScopingMessages.OpNotContains)), M(nameof(ScopingMessages.MustNotContain)));
            case SearchComparisonType.MatchesPattern: return (M(nameof(ScopingMessages.OpMatchesPattern)), M(nameof(ScopingMessages.MustMatchPattern)));
            case SearchComparisonType.NotMatchesPattern: return (M(nameof(ScopingMessages.OpNotMatchesPattern)), M(nameof(ScopingMessages.MustNotMatchPattern)));
        }

        // The ordered comparisons' labels are not verbs ("before", "greater than").
        var label = Label(comparison, type);
        return (Concat(M(nameof(ScopingMessages.Is)), V(" "), label), Concat(M(nameof(ScopingMessages.MustBe)), V(" "), label));
    }

    /// <summary>
    /// Whether an operator's own name, split into words ("starts with"), is the label the editors use, which it is for
    /// only some of them; for the rest a criterion with no data type words the operator by its name.
    /// </summary>
    private static bool NameReadsAsVerb(SearchComparisonType comparison) => comparison is SearchComparisonType.Equals
        or SearchComparisonType.StartsWith or SearchComparisonType.EndsWith or SearchComparisonType.Contains
        or SearchComparisonType.MatchesPattern or SearchComparisonType.LessThan or SearchComparisonType.GreaterThan;

    /// <summary>The criteria editors' label for a comparison, lower-cased to sit mid-sentence.</summary>
    private static JimMessage Label(SearchComparisonType comparison, AttributeDataType? type)
    {
        var date = type == AttributeDataType.DateTime;
        if (!type.HasValue && !NameReadsAsVerb(comparison))
            return V(comparison.ToString().SplitOnCapitalLetters().ToLowerInvariant());

        return comparison switch
        {
            SearchComparisonType.Equals => M(nameof(ScopingMessages.OpEquals)),
            SearchComparisonType.NotEquals => M(nameof(ScopingMessages.OpNotEquals)),
            SearchComparisonType.StartsWith => M(nameof(ScopingMessages.OpStartsWith)),
            SearchComparisonType.NotStartsWith => M(nameof(ScopingMessages.OpNotStartsWith)),
            SearchComparisonType.EndsWith => M(nameof(ScopingMessages.OpEndsWith)),
            SearchComparisonType.NotEndsWith => M(nameof(ScopingMessages.OpNotEndsWith)),
            SearchComparisonType.Contains => M(nameof(ScopingMessages.OpContains)),
            SearchComparisonType.NotContains => M(nameof(ScopingMessages.OpNotContains)),
            SearchComparisonType.MatchesPattern => M(nameof(ScopingMessages.OpMatchesPattern)),
            SearchComparisonType.NotMatchesPattern => M(nameof(ScopingMessages.OpNotMatchesPattern)),
            SearchComparisonType.LessThan => date ? M(nameof(ScopingMessages.OpBefore)) : M(nameof(ScopingMessages.OpLessThan)),
            SearchComparisonType.LessThanOrEquals => date ? M(nameof(ScopingMessages.OpOnOrBefore)) : M(nameof(ScopingMessages.OpLessThanOrEquals)),
            SearchComparisonType.GreaterThan => date ? M(nameof(ScopingMessages.OpAfter)) : M(nameof(ScopingMessages.OpGreaterThan)),
            SearchComparisonType.GreaterThanOrEquals => date ? M(nameof(ScopingMessages.OpOnOrAfter)) : M(nameof(ScopingMessages.OpGreaterThanOrEquals)),
            _ => V((type.HasValue
                ? SearchComparisonOperators.LabelFor(comparison, type.Value)
                : comparison.ToString().SplitOnCapitalLetters()).ToLowerInvariant())
        };
    }

    /// <summary>The operator a negated one negates ("does not equal" to "equals"); any other operator unchanged.</summary>
    private static SearchComparisonType PositiveForm(SearchComparisonType comparison) =>
        SearchComparisonOperators.Negates(comparison) ?? comparison;

    /// <summary>
    /// Whether a case-sensitive text criterion failed only on case, so the words can say that is why: without it,
    /// "must equal Finance (currently finance)" reads as nonsense.
    /// </summary>
    private static bool DiffersOnlyByCase(ScopingCriterionExplanation criterion)
    {
        if (criterion.AttributeType != AttributeDataType.Text || !criterion.CaseSensitive || criterion.Outcome != ScopingCriterionOutcome.NotMet)
            return false;

        var actual = criterion.ActualDisplay;
        var expected = criterion.ExpectedDisplay;
        if (actual == null || expected == null)
            return false;

        const StringComparison ignoringCase = StringComparison.OrdinalIgnoreCase;
        return criterion.ComparisonType switch
        {
            SearchComparisonType.Equals => string.Equals(actual, expected, ignoringCase),
            SearchComparisonType.NotEquals => !string.Equals(actual, expected, ignoringCase),
            SearchComparisonType.StartsWith => actual.StartsWith(expected, ignoringCase),
            SearchComparisonType.NotStartsWith => !actual.StartsWith(expected, ignoringCase),
            SearchComparisonType.EndsWith => actual.EndsWith(expected, ignoringCase),
            SearchComparisonType.NotEndsWith => !actual.EndsWith(expected, ignoringCase),
            SearchComparisonType.Contains => actual.Contains(expected, ignoringCase),
            SearchComparisonType.NotContains => !actual.Contains(expected, ignoringCase),
            _ => false
        };
    }

    #endregion

    #region Not connected

    /// <summary>
    /// The words for an entry under Not connected: its hint, its bullets with their title, and the plain-text summary
    /// an administrator copies into a message or ticket.
    /// </summary>
    /// <param name="scopeReviewPending">Whether the object is marked for an export scope review, which decides what a
    /// Not yet provisioned entry says happens next (#1925): the next synchronisation stages it, or nothing does until
    /// its values or the rule change.</param>
    internal static NotConnectedSummary SummariseNotConnected(string? objectDisplayName, string connectedSystemName,
        bool connectedSystemDisabled, NotConnectedReason reason, ScopingExplanation explanation, bool scopeReviewPending = false)
    {
        MessageArgument objectName = string.IsNullOrWhiteSpace(objectDisplayName)
            ? M(nameof(ScopingMessages.GenericObjectName))
            : objectDisplayName;
        var ruleName = explanation.SyncRuleName;

        var (hint, title, bullets, reasonLine) = reason switch
        {
            NotConnectedReason.NotInScope => (
                FailingAttributesHintMessage(explanation),
                M(nameof(ScopingMessages.TitleToComeIntoScope)),
                ToComeIntoScope(explanation),
                M(nameof(ScopingMessages.ReasonNotInScope), ruleName)),
            NotConnectedReason.NotYetProvisioned when scopeReviewPending => (
                M(nameof(ScopingMessages.HintInScopeStagedNext)),
                M(nameof(ScopingMessages.TitleWhatHappensNext)),
                TextBullets(
                    M(nameof(ScopingMessages.BulletAwaitingExportReview)),
                    M(nameof(ScopingMessages.BulletNextSynchronisationReviews), connectedSystemName)),
                M(nameof(ScopingMessages.ReasonInScopeStagesNext), ruleName)),
            NotConnectedReason.NotYetProvisioned => (
                M(nameof(ScopingMessages.HintInScopeNothingStaged)),
                M(nameof(ScopingMessages.TitleWhatHappensNext)),
                TextBullets(
                    M(nameof(ScopingMessages.BulletStagedWhenValuesChange)),
                    M(nameof(ScopingMessages.BulletSavingRuleStages))),
                M(nameof(ScopingMessages.ReasonInScopeNothingStaged), ruleName)),
            NotConnectedReason.ProvisioningDisabled => (
                M(nameof(ScopingMessages.HintInScopeProvisioningOff)),
                M(nameof(ScopingMessages.TitleWhatWouldConnectIt)),
                TextBullets(
                    M(nameof(ScopingMessages.BulletSwitchOnProvision)),
                    M(nameof(ScopingMessages.BulletCreateObjectThatJoins), connectedSystemName)),
                M(nameof(ScopingMessages.ReasonInScopeNoProvisioning), ruleName)),
            NotConnectedReason.RuleMisconfigured => (
                InvalidCriteriaHintMessage(explanation),
                M(nameof(ScopingMessages.TitleWhatWouldFixIt)),
                InvalidCriteriaBullets(explanation),
                M(nameof(ScopingMessages.ReasonScopingCannotBeEvaluated), ruleName)),
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown not-connected reason.")
        };

        if (connectedSystemDisabled)
            hint = hint == null
                ? M(nameof(ScopingMessages.HintConnectedSystemDisabled))
                : Concat(hint, V(" · "), M(nameof(ScopingMessages.HintConnectedSystemDisabled)));

        var lineBreak = V(LineBreak);
        var summary = new List<JimMessage>
        {
            M(nameof(ScopingMessages.SummaryNotProvisioned), objectName, connectedSystemName),
            M(nameof(ScopingMessages.SummaryReason), reasonLine)
        };
        if (connectedSystemDisabled)
            summary.Add(M(nameof(ScopingMessages.SummaryNoteDisabled), connectedSystemName));
        if (bullets.Count > 0)
        {
            summary.Add(M(nameof(ScopingMessages.SummaryTitleLine), title));
            summary.AddRange(bullets.Select(bullet =>
                Concat(V("- "), bullet.PlainTextDetail ?? V(bullet.PlainText))));
        }
        summary.Add(M(nameof(ScopingMessages.SummaryEvaluated), FormatEvaluatedAt(explanation.EvaluatedAt)));
        var summaryMessage = Join(summary, lineBreak);

        return new NotConnectedSummary
        {
            Hint = hint?.ToEnglish() ?? string.Empty,
            HintDetail = hint,
            BulletsTitle = title.ToEnglish(),
            BulletsTitleDetail = title,
            Bullets = bullets,
            Summary = summaryMessage.ToEnglish(),
            SummaryDetail = summaryMessage
        };
    }

    private static string InvalidCriteriaHint(ScopingExplanation explanation) => InvalidCriteriaHintMessage(explanation).ToEnglish();

    private static JimMessage InvalidCriteriaHintMessage(ScopingExplanation explanation)
    {
        var invalid = InvalidCriteria(explanation).ToList();

        // A value held under an earlier data type (#2329) is not a fault in the rule: nothing in it needs correcting, and
        // calling the criterion invalid would send the administrator looking for one.
        if (invalid.Count > 0 && invalid.All(criterion => criterion.HeldAsDataType.HasValue))
        {
            var held = DistinctTerms(invalid.Select(HintTerm));
            return M(nameof(ScopingMessages.UndecidedUntilFullImport), JoinTerms(held));
        }

        var attributes = DistinctTerms(invalid.Select(HintTerm));
        return attributes.Count switch
        {
            0 => M(nameof(ScopingMessages.ScopingCannotBeEvaluated)),
            1 => M(nameof(ScopingMessages.InvalidCriterionOn), attributes[0]),
            _ => M(nameof(ScopingMessages.InvalidCriteriaOn), JoinTerms(attributes))
        };
    }

    private static JimMessage JoinTerms(List<JimMessage> terms) =>
        terms.Count == 1
            ? terms[0]
            : M(nameof(ScopingMessages.JoinLastAnd), Join(terms.Take(terms.Count - 1), V(", ")), terms[^1]);

    private static List<ExplanationBullet> InvalidCriteriaBullets(ScopingExplanation explanation) =>
        InvalidCriteria(explanation).Select(criterion =>
        {
            var line = new LineBuilder();
            line.Text(M(nameof(ScopingMessages.CriterionCompares), criterion.Path));
            line.Raw(" ");
            if (criterion.AttributeName != null)
                line.Attribute(criterion.AttributeName);
            else
                line.Attribute(M(nameof(ScopingMessages.AnAttribute)));
            if (criterion.HeldAsDataType.HasValue && criterion.FailureReason != null)
            {
                // Held under an earlier data type (#2329): the reason names the value's type and what resolves it.
                line.Text(M(nameof(ScopingMessages.WhichCannotBeEvaluatedYet), ReasonOf(criterion)));
                return line.ToBullet();
            }
            if (SearchComparisonOperators.IsPattern(criterion.ComparisonType) && criterion.AttributeType == AttributeDataType.Text)
            {
                // The operator suits the type, so it is the pattern itself: invalid, or too slow on this object's value.
                line.Raw(" ");
                line.Text(M(nameof(ScopingMessages.WithPatternThatCannotBeEvaluated)));
                return line.ToBullet();
            }
            line.Raw(" ");
            line.Text(M(nameof(ScopingMessages.WithOperatorTypeCannotUse), Label(criterion.ComparisonType, criterion.AttributeType),
                TypeWithArticle(criterion.AttributeType)));
            return line.ToBullet();
        }).ToList();

    /// <summary>Every invalid criterion, in evaluation order.</summary>
    private static IEnumerable<ScopingCriterionExplanation> InvalidCriteria(ScopingExplanation explanation) =>
        explanation.Groups.SelectMany(AllCriteria).Where(c => c.Outcome == ScopingCriterionOutcome.Invalid);

    private static IEnumerable<ScopingCriterionExplanation> AllCriteria(ScopingGroupExplanation group) =>
        group.Criteria.Concat(group.ChildGroups.SelectMany(AllCriteria));

    /// <summary>An attribute's data type with its article, to sit before "attribute": "a text", "an untyped".</summary>
    private static JimMessage TypeWithArticle(AttributeDataType? type) => type switch
    {
        AttributeDataType.Text => M(nameof(ScopingMessages.TypeAText)),
        AttributeDataType.Number => M(nameof(ScopingMessages.TypeANumber)),
        AttributeDataType.LongNumber => M(nameof(ScopingMessages.TypeALongNumber)),
        AttributeDataType.Decimal => M(nameof(ScopingMessages.TypeADecimal)),
        AttributeDataType.DateTime => M(nameof(ScopingMessages.TypeADate)),
        AttributeDataType.Boolean => M(nameof(ScopingMessages.TypeABoolean)),
        AttributeDataType.Guid => M(nameof(ScopingMessages.TypeAGuid)),
        AttributeDataType.Binary => M(nameof(ScopingMessages.TypeABinary)),
        AttributeDataType.Reference => M(nameof(ScopingMessages.TypeAReference)),
        _ => M(nameof(ScopingMessages.TypeAnUntyped))
    };

    private static List<ExplanationBullet> TextBullets(params JimMessage[] lines) => lines.Select(text =>
    {
        var line = new LineBuilder();
        line.Text(text);
        return line.ToBullet();
    }).ToList();

    #endregion

    /// <summary>
    /// The instant an explanation was evaluated, to the minute and in UTC, for example "4 Oct 2026 10:41 UTC", so it
    /// reads the same wherever it is pasted.
    /// </summary>
    internal static string FormatEvaluatedAt(DateTime evaluatedAt)
    {
        var utc = evaluatedAt.Kind == DateTimeKind.Local ? evaluatedAt.ToUniversalTime() : evaluatedAt;
        return utc.ToString("d MMM yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture);
    }

    private static IEnumerable<ScopingCriterionExplanation> FailingCriteria(ScopingGroupExplanation group) =>
        group.Criteria.Where(c => c.Outcome != ScopingCriterionOutcome.Met);

    private static IEnumerable<ScopingGroupExplanation> FailingChildGroups(ScopingGroupExplanation group) =>
        group.ChildGroups.Where(g => g.Met != true);

    /// <summary>The terms with repeats (by their English) dropped, in order.</summary>
    private static List<JimMessage> DistinctTerms(IEnumerable<JimMessage> terms) =>
        terms.DistinctBy(term => term.ToEnglish(), StringComparer.Ordinal).ToList();

    /// <summary>
    /// Builds one line as typed segments and, alongside, its plain text with text values quoted, so the two forms
    /// cannot drift apart. Each piece is kept as a message beside its English, so the line can be shown in the viewer's
    /// language; adjacent pieces of words join into one segment.
    /// </summary>
    private sealed class LineBuilder
    {
        private readonly List<ExplanationSegment> _segments = [];
        private readonly List<JimMessage> _plainText = [];

        public string Unquoted => string.Concat(_segments.Select(s => s.Text));

        /// <summary>The line without quotes, as one message.</summary>
        public JimMessage UnquotedMessage => Join(_segments.Select(s => s.Message ?? V(s.Text)), V(string.Empty));

        /// <summary>Words, as a message.</summary>
        public void Text(JimMessage message)
        {
            _plainText.Add(message);
            AppendText(message);
        }

        /// <summary>Punctuation and spaces: the same in every language.</summary>
        public void Raw(string text) => Text(V(text));

        /// <summary>A word with a space either side.</summary>
        public void Spaced(JimMessage word)
        {
            Raw(" ");
            Text(word);
            Raw(" ");
        }

        private void AppendText(JimMessage message)
        {
            var english = message.ToEnglish();
            if (_segments.Count > 0 && _segments[^1].Kind == ExplanationSegmentKind.Text)
            {
                var last = _segments[^1];
                last.Message = Concat(last.Message ?? V(last.Text), message);
                last.Text += english;
            }
            else
            {
                _segments.Add(new ExplanationSegment { Kind = ExplanationSegmentKind.Text, Text = english, Message = message });
            }
        }

        public void Attribute(string name)
        {
            _plainText.Add(V(name));
            _segments.Add(new ExplanationSegment { Kind = ExplanationSegmentKind.Attribute, Text = name });
        }

        /// <summary>An attribute's place held by words, where the criterion names none.</summary>
        public void Attribute(JimMessage message)
        {
            _plainText.Add(message);
            _segments.Add(new ExplanationSegment { Kind = ExplanationSegmentKind.Attribute, Text = message.ToEnglish(), Message = message });
        }

        public void Value(ExplanationSegmentKind kind, string text, bool quote)
        {
            _plainText.Add(quote ? M(nameof(ScopingMessages.QuotedValue), text) : V(text));
            _segments.Add(new ExplanationSegment { Kind = kind, Text = text });
        }

        /// <summary>A value that is words rather than data ("no value", "a hidden value").</summary>
        public void Value(ExplanationSegmentKind kind, JimMessage message)
        {
            _plainText.Add(message);
            _segments.Add(new ExplanationSegment { Kind = kind, Text = message.ToEnglish(), Message = message });
        }

        public ExplanationBullet ToBullet()
        {
            var plain = Join(_plainText, V(string.Empty));
            return new ExplanationBullet { Segments = _segments, PlainText = plain.ToEnglish(), PlainTextDetail = plain };
        }
    }
}
