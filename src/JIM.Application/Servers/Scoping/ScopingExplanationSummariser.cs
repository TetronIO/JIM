// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Text;
using JIM.Models.Core;
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
/// Comparisons are worded with the criteria editors' labels (<see cref="SearchComparisonOperators.LabelFor"/>), in two
/// forms: a requirement for a line of its own ("must equal", "must be before") and a condition inside a choice
/// ("equals", "is before"). Values are culture-invariant and dates are UTC, so the text is the same on every surface
/// and unambiguous when forwarded; text values are quoted in plain text so their boundaries survive being pasted.
/// Values of credential attributes never appear: the explanation withholds them and the words say so.
/// </remarks>
internal static class ScopingExplanationSummariser
{
    /// <summary>
    /// Line breaks in generated text are always "\n", whatever the server's platform, so every surface returns
    /// identical text.
    /// </summary>
    private const string LineBreak = "\n";

    private const string GenericObjectName = "This Metaverse Object";

    private enum Form { Requirement, Condition }

    #region Tree lines

    /// <summary>A criterion as one line of the tree, for example "Department equals Finance".</summary>
    internal static string DescribeCriterion(ScopingCriterionExplanation criterion)
    {
        if (criterion.Outcome == ScopingCriterionOutcome.AttributeMissing || criterion.AttributeName == null)
            return "Criterion with no attribute";

        var line = new LineBuilder();
        AppendComparison(line, criterion, Form.Condition, includeCurrent: false);
        if (criterion.AttributeType == AttributeDataType.Text && !criterion.CaseSensitive)
            line.Text(" (ignoring case)");
        return line.Unquoted;
    }

    /// <summary>What the object held, for the tree, for example "is Engineering" or "has no value".</summary>
    internal static string DescribeActual(ScopingCriterionExplanation criterion)
    {
        if (criterion.Outcome == ScopingCriterionOutcome.AttributeMissing)
            return "references no attribute";
        if (criterion.Outcome == ScopingCriterionOutcome.Invalid)
            return "cannot be evaluated";
        if (criterion.Masked)
            return "is hidden";
        if (criterion.ActualDisplay == null)
            return "has no value";

        return criterion.AdditionalValuesNotEvaluated > 0
            ? $"is {criterion.ActualDisplay} ({FurtherValues(criterion.AdditionalValuesNotEvaluated)})"
            : $"is {criterion.ActualDisplay}";
    }

    /// <summary>A group as one line of the tree, for example "All of these must be met (not met)".</summary>
    internal static string DescribeGroup(ScopingGroupExplanation group)
    {
        var heading = group.Type == SearchGroupType.All ? "All of these must be met" : "Any one of these";
        var state = group.Met switch
        {
            null => "cannot be evaluated",
            true => "met",
            false => group.Type == SearchGroupType.Any ? "none met" : "not met"
        };
        return $"{heading} ({state})";
    }

    #endregion

    #region Text carried on every explanation

    /// <summary>
    /// Writes the words every surface shows onto an explanation: its hint and each line of its tree. Done once, on the
    /// server, so the portal, the REST API and PowerShell cannot word an explanation differently.
    /// </summary>
    internal static void Describe(ScopingExplanation explanation)
    {
        explanation.Hint = explanation.Outcome switch
        {
            ScopingRuleOutcome.OutOfScope => FailingAttributesHint(explanation),
            ScopingRuleOutcome.Undetermined => InvalidCriteriaHint(explanation),
            _ => string.Empty
        };

        foreach (var group in explanation.Groups)
            DescribeTree(group);
    }

    private static void DescribeTree(ScopingGroupExplanation group)
    {
        group.Description = DescribeGroup(group);
        foreach (var criterion in group.Criteria)
        {
            criterion.Description = DescribeCriterion(criterion);
            criterion.ActualDescription = DescribeActual(criterion);
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
        $"The Synchronisation Rule \"{syncRuleName}\" targets \"{targetObjectTypeName}\" objects in {connectedSystemName}, but this " +
        $"Metaverse Object is already represented there by a \"{existingObjectTypeName}\" object. A Metaverse Object can have only one " +
        "Connected System Object per Connected System, so this rule cannot connect it.";

    #endregion

    #region Hint

    /// <summary>
    /// The attributes an out-of-scope object fails on, without their values: "Fails on Department; Cost Centre or Job
    /// Title". A semicolon separates requirements that must all be met; "or" separates alternatives. Empty when the
    /// object is not out of scope.
    /// </summary>
    internal static string FailingAttributesHint(ScopingExplanation explanation)
    {
        if (explanation.Outcome != ScopingRuleOutcome.OutOfScope || explanation.Groups.Count == 0)
            return string.Empty;

        var text = explanation.Groups.Count == 1
            ? HintFor(explanation.Groups[0], insideChoice: false)
            : string.Join(" or ", DistinctTerms(explanation.Groups.Select(g => HintFor(g, insideChoice: true))));
        return $"Fails on {text}";
    }

    private static string HintFor(ScopingGroupExplanation group, bool insideChoice)
    {
        var terms = DistinctTerms(FailingCriteria(group).Select(HintTerm)
            .Concat(FailingChildGroups(group).Select(child => HintFor(child, insideChoice: group.Type == SearchGroupType.Any))));

        if (group.Type == SearchGroupType.Any)
            return string.Join(" or ", terms);

        var requirements = string.Join("; ", terms);
        return insideChoice && terms.Count > 1 ? $"({requirements})" : requirements;
    }

    private static string HintTerm(ScopingCriterionExplanation criterion) =>
        criterion.AttributeName ?? $"criterion {criterion.Path}";

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
            line.Text(i == 0 ? "either " : "or ");
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
        line.Text(joinsEarlierRequirements ? "and either " : "either ");
        for (var i = 0; i < alternatives.Count; i++)
        {
            if (i > 0)
                line.Text(", or ");
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
                    line.Text(" or ");
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
            line.Text("(");
        for (var i = 0; i < requirements.Count; i++)
        {
            if (i > 0)
                line.Text(" and ");
            switch (requirements[i])
            {
                case ScopingCriterionExplanation criterion:
                    AppendCriterion(line, criterion, Form.Condition, startsLine: false);
                    break;
                case ScopingGroupExplanation { Type: SearchGroupType.Any } choice when Alternatives(choice).Count > 1:
                    line.Text("(");
                    AppendGroupCondition(line, choice, insideChoice: false);
                    line.Text(")");
                    break;
                case ScopingGroupExplanation child:
                    AppendGroupCondition(line, child, insideChoice: false);
                    break;
            }
        }
        if (parenthesise)
            line.Text(")");
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
            line.Text($"{(startsLine ? "Criterion" : "criterion")} {criterion.Path} has no attribute and can never be met");
            return;
        }

        AppendComparison(line, criterion, form, includeCurrent: true);
    }

    private static void AppendComparison(LineBuilder line, ScopingCriterionExplanation criterion, Form form, bool includeCurrent)
    {
        var (condition, requirement) = Wording(criterion.ComparisonType, criterion.AttributeType);
        var verb = form == Form.Requirement ? requirement : condition;
        var quote = criterion.AttributeType == AttributeDataType.Text;
        var expectedEmpty = criterion.ExpectedDisplay == null;
        var noValue = criterion.Outcome == ScopingCriterionOutcome.NoValue;

        line.Attribute(criterion.AttributeName!);

        if (criterion.Masked)
        {
            line.Text($" {verb} ");
            line.Value(ExplanationSegmentKind.Hidden, "a hidden value", quote: false);
            if (includeCurrent)
            {
                line.Text(" (");
                line.Value(ExplanationSegmentKind.Hidden, "current value hidden", quote: false);
                line.Text(")");
            }
            return;
        }

        // An empty expected value turns equality into presence: "has no value", "has a value".
        if (expectedEmpty && criterion.ComparisonType == SearchComparisonType.Equals)
        {
            line.Text(form == Form.Requirement ? " must have no value" : " has no value");
            if (includeCurrent && !noValue)
                AppendCurrent(line, criterion, quote, caseIsTheReason: false);
            return;
        }
        if (expectedEmpty && criterion.ComparisonType == SearchComparisonType.NotEquals)
        {
            line.Text(form == Form.Requirement ? " needs a value" : " has a value");
            return;
        }

        // A negated comparison fails on a missing value only because there is no value: the value is what is needed.
        if (includeCurrent && noValue && IsNegated(criterion.ComparisonType))
        {
            line.Text(form == Form.Requirement ? $" needs a value that {condition} " : $" has a value that {condition} ");
            AppendExpected(line, criterion, quote);
            return;
        }

        line.Text($" {verb} ");
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
                line.Text(", ");
                line.Value(ExplanationSegmentKind.ExpectedValue, criterion.ExpectedDisplay, quote: false);
            }
            return;
        }

        if (criterion.ExpectedDisplay == null)
            line.Text("an empty value");
        else
            line.Value(ExplanationSegmentKind.ExpectedValue, criterion.ExpectedDisplay, quote);
    }

    private static void AppendCurrent(LineBuilder line, ScopingCriterionExplanation criterion, bool quote, bool caseIsTheReason)
    {
        line.Text(caseIsTheReason ? " (case-sensitive; currently " : " (currently ");
        if (criterion.ActualDisplay == null)
            line.Value(ExplanationSegmentKind.NoValue, "no value", quote: false);
        else
            line.Value(ExplanationSegmentKind.CurrentValue, criterion.ActualDisplay, quote);

        if (criterion.AdditionalValuesNotEvaluated > 0)
            line.Text($"; {FurtherValues(criterion.AdditionalValuesNotEvaluated)}");
        line.Text(")");
    }

    /// <summary>
    /// The criteria editors' label for the comparison as a condition ("equals", "is before") and as a requirement
    /// ("must equal", "must be before").
    /// </summary>
    private static (string Condition, string Requirement) Wording(SearchComparisonType comparison, AttributeDataType? type)
    {
        var label = Label(comparison, type);
        return label switch
        {
            "equals" => ("equals", "must equal"),
            "does not equal" => ("does not equal", "must not equal"),
            "starts with" => ("starts with", "must start with"),
            "does not start with" => ("does not start with", "must not start with"),
            "ends with" => ("ends with", "must end with"),
            "does not end with" => ("does not end with", "must not end with"),
            "contains" => ("contains", "must contain"),
            "does not contain" => ("does not contain", "must not contain"),
            // The ordered comparisons' labels are not verbs ("before", "greater than").
            _ => ($"is {label}", $"must be {label}")
        };
    }

    /// <summary>The criteria editors' label for a comparison, lower-cased to sit mid-sentence.</summary>
    private static string Label(SearchComparisonType comparison, AttributeDataType? type) =>
        (type.HasValue
            ? SearchComparisonOperators.LabelFor(comparison, type.Value)
            : comparison.ToString().SplitOnCapitalLetters()).ToLowerInvariant();

    private static bool IsNegated(SearchComparisonType comparison) => comparison is
        SearchComparisonType.NotEquals or SearchComparisonType.NotStartsWith or
        SearchComparisonType.NotEndsWith or SearchComparisonType.NotContains;

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

    private static string FurtherValues(int count) =>
        count == 1 ? "1 more value not compared" : $"{count.ToString(CultureInfo.InvariantCulture)} more values not compared";

    #endregion

    #region Not connected

    /// <summary>
    /// The words for an entry under Not connected: its hint, its bullets with their title, and the plain-text summary
    /// an administrator copies into a message or ticket.
    /// </summary>
    internal static NotConnectedSummary SummariseNotConnected(string? objectDisplayName, string connectedSystemName,
        bool connectedSystemDisabled, NotConnectedReason reason, ScopingExplanation explanation)
    {
        var objectName = string.IsNullOrWhiteSpace(objectDisplayName) ? GenericObjectName : objectDisplayName;
        var ruleName = explanation.SyncRuleName;

        var (hint, title, bullets, reasonLine) = reason switch
        {
            NotConnectedReason.NotInScope => (
                FailingAttributesHint(explanation),
                "To come into scope",
                ToComeIntoScope(explanation),
                $"not in scope of the Synchronisation Rule \"{ruleName}\"."),
            NotConnectedReason.NotYetProvisioned => (
                "In scope; nothing staged yet",
                "What happens next",
                TextBullets(
                    "Provisioning is staged the next time this Metaverse Object's attribute values change during synchronisation.",
                    "A synchronisation that changes none of its attribute values does not stage it."),
                $"in scope of the Synchronisation Rule \"{ruleName}\", but nothing has been staged yet."),
            NotConnectedReason.ProvisioningDisabled => (
                "In scope; provisioning is off",
                "What would connect it",
                TextBullets(
                    "Switch on Provision to Connected System in the Synchronisation Rule, or",
                    $"create an object in {connectedSystemName} that joins to this Metaverse Object."),
                $"in scope of the Synchronisation Rule \"{ruleName}\", but the rule does not provision new objects."),
            NotConnectedReason.RuleMisconfigured => (
                InvalidCriteriaHint(explanation),
                "What would fix it",
                InvalidCriteriaBullets(explanation),
                $"the scoping of the Synchronisation Rule \"{ruleName}\" cannot be evaluated."),
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown not-connected reason.")
        };

        if (connectedSystemDisabled)
            hint = string.IsNullOrEmpty(hint) ? "Connected System disabled" : $"{hint} · Connected System disabled";

        var summary = new StringBuilder();
        summary.Append(objectName).Append(" is not provisioned to ").Append(connectedSystemName).Append('.').Append(LineBreak);
        summary.Append("Reason: ").Append(reasonLine).Append(LineBreak);
        if (connectedSystemDisabled)
            summary.Append("Note: the Connected System ").Append(connectedSystemName).Append(" is disabled.").Append(LineBreak);
        if (bullets.Count > 0)
        {
            summary.Append(title).Append(':').Append(LineBreak);
            foreach (var bullet in bullets)
                summary.Append("- ").Append(bullet.PlainText).Append(LineBreak);
        }
        summary.Append("Evaluated ").Append(FormatEvaluatedAt(explanation.EvaluatedAt)).Append('.');

        return new NotConnectedSummary
        {
            Hint = hint,
            BulletsTitle = title,
            Bullets = bullets,
            Summary = summary.ToString()
        };
    }

    private static string InvalidCriteriaHint(ScopingExplanation explanation)
    {
        var attributes = DistinctTerms(InvalidCriteria(explanation).Select(HintTerm));
        return attributes.Count switch
        {
            0 => "Scoping cannot be evaluated",
            1 => $"Invalid criterion on {attributes[0]}",
            _ => $"Invalid criteria on {string.Join(", ", attributes.Take(attributes.Count - 1))} and {attributes[^1]}"
        };
    }

    private static List<ExplanationBullet> InvalidCriteriaBullets(ScopingExplanation explanation) =>
        InvalidCriteria(explanation).Select(criterion =>
        {
            var line = new LineBuilder();
            line.Text($"Criterion {criterion.Path} compares ");
            line.Attribute(criterion.AttributeName ?? "an attribute");
            var typeName = TypeName(criterion.AttributeType);
            line.Text($" with \"{Label(criterion.ComparisonType, criterion.AttributeType)}\", which {Article(typeName)} {typeName} attribute cannot use; " +
                      "correct or remove it in the Synchronisation Rule.");
            return line.ToBullet();
        }).ToList();

    /// <summary>Every invalid criterion, in evaluation order.</summary>
    private static IEnumerable<ScopingCriterionExplanation> InvalidCriteria(ScopingExplanation explanation) =>
        explanation.Groups.SelectMany(AllCriteria).Where(c => c.Outcome == ScopingCriterionOutcome.Invalid);

    private static IEnumerable<ScopingCriterionExplanation> AllCriteria(ScopingGroupExplanation group) =>
        group.Criteria.Concat(group.ChildGroups.SelectMany(AllCriteria));

    private static string TypeName(AttributeDataType? type) => type switch
    {
        AttributeDataType.Text => "text",
        AttributeDataType.Number => "number",
        AttributeDataType.LongNumber => "long number",
        AttributeDataType.Decimal => "decimal",
        AttributeDataType.DateTime => "date",
        AttributeDataType.Boolean => "Boolean",
        AttributeDataType.Guid => "GUID",
        AttributeDataType.Binary => "binary",
        AttributeDataType.Reference => "reference",
        _ => "untyped"
    };

    private static string Article(string word) => "aeiou".Contains(char.ToLowerInvariant(word[0])) ? "an" : "a";

    private static List<ExplanationBullet> TextBullets(params string[] lines) => lines.Select(text =>
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

    private static List<string> DistinctTerms(IEnumerable<string> terms) => terms.Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Builds one line as typed segments and, alongside, its plain text with text values quoted, so the two forms
    /// cannot drift apart.
    /// </summary>
    private sealed class LineBuilder
    {
        private readonly List<ExplanationSegment> _segments = [];
        private readonly StringBuilder _plainText = new();

        public string Unquoted => string.Concat(_segments.Select(s => s.Text));

        public void Text(string text)
        {
            _plainText.Append(text);
            if (_segments.Count > 0 && _segments[^1].Kind == ExplanationSegmentKind.Text)
                _segments[^1].Text += text;
            else
                _segments.Add(new ExplanationSegment { Kind = ExplanationSegmentKind.Text, Text = text });
        }

        public void Attribute(string name)
        {
            _plainText.Append(name);
            _segments.Add(new ExplanationSegment { Kind = ExplanationSegmentKind.Attribute, Text = name });
        }

        public void Value(ExplanationSegmentKind kind, string text, bool quote)
        {
            _plainText.Append(quote ? $"\"{text}\"" : text);
            _segments.Add(new ExplanationSegment { Kind = kind, Text = text });
        }

        public ExplanationBullet ToBullet() => new() { Segments = _segments, PlainText = _plainText.ToString() };
    }
}
