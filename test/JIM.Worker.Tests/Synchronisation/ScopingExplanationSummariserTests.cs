// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Text.Json;
using JIM.Application.Servers;
using JIM.Application.Servers.Scoping;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Logic.Scoping;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using NUnit.Framework;

namespace JIM.Worker.Tests.Synchronisation;

/// <summary>
/// The words an explanation is turned into (#348): the one-line hint, the "To come into scope" bullets, the copyable
/// summary and the tree's line descriptions. Explanations come from evaluating real rules, so these tests also pin the
/// wording to what the evaluator records. The text is generated once on the server and shown unchanged by the portal,
/// the REST API and PowerShell, so its exact wording is behaviour here, not presentation.
/// </summary>
[TestFixture]
public class ScopingExplanationSummariserTests
{
    private static readonly DateTime Now = new(2026, 10, 4, 10, 41, 0, DateTimeKind.Utc);

    private static readonly MetaverseAttribute EmployeeStatus = Attribute(1, "Employee Status", AttributeDataType.Text);
    private static readonly MetaverseAttribute Department = Attribute(2, "Department", AttributeDataType.Text);
    private static readonly MetaverseAttribute CostCentre = Attribute(3, "Cost Centre", AttributeDataType.Text);
    private static readonly MetaverseAttribute JobTitle = Attribute(4, "Job Title", AttributeDataType.Text);
    private static readonly MetaverseAttribute WorkerType = Attribute(5, "Worker Type", AttributeDataType.Text);
    private static readonly MetaverseAttribute Grade = Attribute(6, "Grade", AttributeDataType.Number);
    private static readonly MetaverseAttribute Active = Attribute(7, "Active", AttributeDataType.Boolean);
    private static readonly MetaverseAttribute StartDate = Attribute(8, "Start Date", AttributeDataType.DateTime);
    private static readonly MetaverseAttribute Allowance = Attribute(9, "Allowance", AttributeDataType.Decimal);
    private static readonly MetaverseAttribute Region = Attribute(10, "Region", AttributeDataType.Text);

    private readonly ScopingEvaluationServer _scoping = new();
    private int _nextId = 1;

    #region Builders

    private static MetaverseAttribute Attribute(int id, string name, AttributeDataType type) => new() { Id = id, Name = name, Type = type };

    private SyncRule Rule(params SyncRuleScopingCriteriaGroup[] groups)
    {
        var rule = new SyncRule { Id = 42, Name = "Finance App Users Export", Direction = SyncRuleDirection.Export };
        rule.ObjectScopingCriteriaGroups.AddRange(groups);
        return rule;
    }

    private SyncRuleScopingCriteriaGroup All(params object[] children) => Group(SearchGroupType.All, children);

    private SyncRuleScopingCriteriaGroup Any(params object[] children) => Group(SearchGroupType.Any, children);

    private SyncRuleScopingCriteriaGroup Group(SearchGroupType type, object[] children)
    {
        var group = new SyncRuleScopingCriteriaGroup { Id = _nextId++, Type = type };
        group.Criteria.AddRange(children.OfType<SyncRuleScopingCriteria>());
        group.ChildGroups.AddRange(children.OfType<SyncRuleScopingCriteriaGroup>());
        return group;
    }

    private SyncRuleScopingCriteria Criterion(MetaverseAttribute? attribute, SearchComparisonType op, object? expected, bool caseSensitive = true)
    {
        var criterion = new SyncRuleScopingCriteria
        {
            Id = _nextId++,
            MetaverseAttribute = attribute,
            ComparisonType = op,
            CaseSensitive = caseSensitive,
            StringValue = expected as string,
            IntValue = expected as int?,
            DecimalValue = expected as decimal?,
            DateTimeValue = expected as DateTime?,
            BoolValue = expected as bool?
        };
        return criterion;
    }

    private SyncRuleScopingCriteria RelativeDate(MetaverseAttribute attribute, SearchComparisonType op, int count, RelativeDateUnit unit, RelativeDateDirection direction)
    {
        var criterion = Criterion(attribute, op, null);
        criterion.ValueMode = DateCriteriaValueMode.Relative;
        criterion.RelativeCount = count;
        criterion.RelativeUnit = unit;
        criterion.RelativeDirection = direction;
        return criterion;
    }

    private static MetaverseObject Person(params (MetaverseAttribute Attribute, object? Value)[] values)
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        foreach (var (attribute, value) in values)
        {
            mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
            {
                AttributeId = attribute.Id,
                StringValue = value as string,
                IntValue = value as int?,
                DecimalValue = value as decimal?,
                DateTimeValue = value as DateTime?,
                BoolValue = value as bool?
            });
        }
        return mvo;
    }

    /// <summary>Jane Smith from the PRD's Scenario 1: Engineering, a Software Engineer, active, with no Cost Centre.</summary>
    private static MetaverseObject Jane => Person(
        (EmployeeStatus, "Active"), (Department, "Engineering"), (JobTitle, "Software Engineer"), (Grade, 3), (Active, false),
        (StartDate, new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)), (Allowance, 1000.25m), (Region, "APAC"));

    private ScopingExplanation Explain(SyncRule rule, MetaverseObject? person = null) =>
        _scoping.ExplainMvoForExportRule(person ?? Jane, rule, Now);

    /// <summary>The PRD's Scenario 1 rule: Employee Status equals Active AND Department equals Finance AND (Cost Centre starts with FIN OR Job Title contains Accountant).</summary>
    private SyncRule FinanceAppRule() => Rule(All(
        Criterion(EmployeeStatus, SearchComparisonType.Equals, "Active"),
        Criterion(Department, SearchComparisonType.Equals, "Finance"),
        Any(
            Criterion(CostCentre, SearchComparisonType.StartsWith, "FIN"),
            Criterion(JobTitle, SearchComparisonType.Contains, "Accountant"))));

    private static IEnumerable<string> PlainText(IEnumerable<ExplanationBullet> bullets) => bullets.Select(b => b.PlainText);

    private static string Joined(ExplanationBullet bullet) => string.Concat(bullet.Segments.Select(s => s.Text));

    #endregion

    #region Scenario 1

    [Test]
    public void SummariseNotConnected_PrdScenarioOne_ProducesTheDocumentedSummary()
    {
        var summary = ScopingExplanationSummariser.SummariseNotConnected("Jane Smith", "Finance App", connectedSystemDisabled: false,
            NotConnectedReason.NotInScope, Explain(FinanceAppRule()));

        Assert.That(summary.Summary, Is.EqualTo(
            "Jane Smith is not provisioned to Finance App.\n" +
            "Reason: not in scope of the Synchronisation Rule \"Finance App Users Export\".\n" +
            "To come into scope:\n" +
            "- Department must equal \"Finance\" (currently \"Engineering\")\n" +
            "- and either Cost Centre starts with \"FIN\" (currently no value), or Job Title contains \"Accountant\" (currently \"Software Engineer\")\n" +
            "Evaluated 4 Oct 2026 10:41 UTC."));
    }

    [Test]
    public void SummariseNotConnected_PrdScenarioOne_HintNamesFailingAttributesWithoutValues()
    {
        var summary = ScopingExplanationSummariser.SummariseNotConnected("Jane Smith", "Finance App", false,
            NotConnectedReason.NotInScope, Explain(FinanceAppRule()));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary.Hint, Is.EqualTo("Fails on Department; Cost Centre or Job Title"));
            Assert.That(summary.BulletsTitle, Is.EqualTo("To come into scope"));
        }
    }

    [Test]
    public void ToComeIntoScope_PrdScenarioOne_SegmentsReadAsThePortalShowsThem()
    {
        var bullets = ScopingExplanationSummariser.ToComeIntoScope(Explain(FinanceAppRule()));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bullets.Select(Joined), Is.EqualTo(new[]
            {
                "Department must equal Finance (currently Engineering)",
                "and either Cost Centre starts with FIN (currently no value), or Job Title contains Accountant (currently Software Engineer)"
            }));
            Assert.That(bullets[0].Segments.Select(s => (s.Kind, s.Text)), Is.EqualTo(new[]
            {
                (ExplanationSegmentKind.Attribute, "Department"),
                (ExplanationSegmentKind.Text, " must equal "),
                (ExplanationSegmentKind.ExpectedValue, "Finance"),
                (ExplanationSegmentKind.Text, " (currently "),
                (ExplanationSegmentKind.CurrentValue, "Engineering"),
                (ExplanationSegmentKind.Text, ")")
            }));
            Assert.That(bullets[1].Segments.Where(s => s.Kind == ExplanationSegmentKind.NoValue).Select(s => s.Text), Is.EqualTo(new[] { "no value" }));
        }
    }

    [Test]
    public void Describe_PrdScenarioOneTree_EachLineReadsAsDocumented()
    {
        var group = Explain(FinanceAppRule()).Groups.Single();
        var any = group.ChildGroups.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ScopingExplanationSummariser.DescribeGroup(group), Is.EqualTo("All of these must be met (not met)"));
            Assert.That(group.Criteria.Select(c => (ScopingExplanationSummariser.DescribeCriterion(c), ScopingExplanationSummariser.DescribeActual(c))),
                Is.EqualTo(new[] { ("Employee Status equals Active", "is Active"), ("Department equals Finance", "is Engineering") }));
            Assert.That(ScopingExplanationSummariser.DescribeGroup(any), Is.EqualTo("Any one of these (none met)"));
            Assert.That(any.Criteria.Select(c => (ScopingExplanationSummariser.DescribeCriterion(c), ScopingExplanationSummariser.DescribeActual(c))),
                Is.EqualTo(new[] { ("Cost Centre starts with FIN", "has no value"), ("Job Title contains Accountant", "is Software Engineer") }));
        }
    }

    #endregion

    #region Text carried on every explanation

    /// <summary>
    /// The tree's lines and the hint are generated on the server and travel with the explanation, so the portal, the
    /// REST API and PowerShell show identical words without each rebuilding them.
    /// </summary>
    [Test]
    public void ExplainMvoForExportRule_OutOfScope_CarriesTreeLinesAndHint()
    {
        var explanation = Explain(FinanceAppRule());
        var group = explanation.Groups.Single();
        var any = group.ChildGroups.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(explanation.Hint, Is.EqualTo("Fails on Department; Cost Centre or Job Title"));
            Assert.That(group.Description, Is.EqualTo("All of these must be met (not met)"));
            Assert.That(group.Criteria.Select(c => (c.Description, c.ActualDescription)),
                Is.EqualTo(new[] { ("Employee Status equals Active", "is Active"), ("Department equals Finance", "is Engineering") }));
            Assert.That(any.Description, Is.EqualTo("Any one of these (none met)"));
            Assert.That(any.Criteria[0].ActualDescription, Is.EqualTo("has no value"));
        }
    }

    [Test]
    public void ExplainMvoForExportRule_InScope_HintIsEmpty()
    {
        var explanation = Explain(Rule(All(Criterion(Department, SearchComparisonType.Equals, "Engineering"))));

        Assert.That(explanation.Hint, Is.Empty);
    }

    [Test]
    public void ExplainMvoForExportRule_InvalidCriterionReached_HintNamesTheInvalidCriterion()
    {
        var explanation = Explain(Rule(All(Criterion(StartDate, SearchComparisonType.StartsWith, null))));

        Assert.That(explanation.Hint, Is.EqualTo("Invalid criterion on Start Date"));
    }

    [Test]
    public void DescribeObjectTypeConflict_RuleTargetingAnotherType_SaysWhyTheRuleCannotConnect()
    {
        var text = ScopingExplanationSummariser.DescribeObjectTypeConflict("Finance App Groups Export", "group", "person", "Finance App");

        Assert.That(text, Is.EqualTo(
            "The Synchronisation Rule \"Finance App Groups Export\" targets \"group\" objects in Finance App, but this Metaverse Object " +
            "is already represented there by a \"person\" object. A Metaverse Object can have only one Connected System Object per " +
            "Connected System, so this rule cannot connect it."));
    }

    [TestCase(ConnectedSystemObjectJoinMethod.Projection, 7, "HR Users Import", ExpectedResult = "Projected by the Synchronisation Rule \"HR Users Import\"")]
    [TestCase(ConnectedSystemObjectJoinMethod.Provisioning, 7, "Finance App Users Export", ExpectedResult = "Provisioned by the Synchronisation Rule \"Finance App Users Export\"")]
    [TestCase(ConnectedSystemObjectJoinMethod.InboundMatching, 7, "Payroll Import", ExpectedResult = "Joined by matching, under the Synchronisation Rule \"Payroll Import\"")]
    [TestCase(ConnectedSystemObjectJoinMethod.InboundMatching, null, null, ExpectedResult = "Joined by matching on the Connected System's Object Matching Rules")]
    [TestCase(ConnectedSystemObjectJoinMethod.ExportMatching, 7, "Finance App Users Export", ExpectedResult = "Joined by the Synchronisation Rule \"Finance App Users Export\", which matched an existing object instead of provisioning a new one")]
    [TestCase(ConnectedSystemObjectJoinMethod.Projection, null, "HR Users Import", ExpectedResult = "Projected by the Synchronisation Rule \"HR Users Import\" (since deleted)")]
    public string DescribeJoin_Recorded_SaysHowAndByWhichRule(ConnectedSystemObjectJoinMethod method, int? ruleId, string? ruleName) =>
        ScopingExplanationSummariser.DescribeJoin(new JoinRecord
        {
            Method = method, SyncRuleId = ruleId, SyncRuleName = ruleName, Source = JoinRecordSource.Recorded,
            JoinType = method switch
            {
                ConnectedSystemObjectJoinMethod.Projection => ConnectedSystemObjectJoinType.Projected,
                ConnectedSystemObjectJoinMethod.Provisioning => ConnectedSystemObjectJoinType.Provisioned,
                _ => ConnectedSystemObjectJoinType.Joined
            }
        });

    [TestCase(ConnectedSystemObjectJoinType.Projected, ExpectedResult = "Projected; the Synchronisation Rule was not recorded")]
    [TestCase(ConnectedSystemObjectJoinType.Provisioned, ExpectedResult = "Provisioned; the Synchronisation Rule was not recorded")]
    [TestCase(ConnectedSystemObjectJoinType.Joined, ExpectedResult = "Joined; how was not recorded")]
    public string DescribeJoin_NotRecorded_SaysSoRatherThanGuessing(ConnectedSystemObjectJoinType joinType) =>
        ScopingExplanationSummariser.DescribeJoin(new JoinRecord { JoinType = joinType, Source = JoinRecordSource.NotRecorded });

    /// <summary>
    /// A rule found in history may since have been deleted, and history cannot say; the name is shown as it was.
    /// </summary>
    [Test]
    public void DescribeJoin_Derived_NamesTheRuleHistoryRecorded()
    {
        var text = ScopingExplanationSummariser.DescribeJoin(new JoinRecord
        {
            JoinType = ConnectedSystemObjectJoinType.Projected, Method = ConnectedSystemObjectJoinMethod.Projection,
            SyncRuleId = 7, SyncRuleName = "HR Users Import", Source = JoinRecordSource.Derived
        });

        Assert.That(text, Is.EqualTo("Projected by the Synchronisation Rule \"HR Users Import\""));
    }

    #endregion

    #region Bullets

    [Test]
    public void ToComeIntoScope_SeveralFailingCriteriaInAnAllGroup_OneRequirementEach()
    {
        var bullets = ScopingExplanationSummariser.ToComeIntoScope(Explain(Rule(All(
            Criterion(Department, SearchComparisonType.Equals, "Finance"),
            Criterion(Grade, SearchComparisonType.GreaterThan, 5),
            Criterion(EmployeeStatus, SearchComparisonType.Equals, "Active")))));

        Assert.That(PlainText(bullets), Is.EqualTo(new[]
        {
            "Department must equal \"Finance\" (currently \"Engineering\")",
            "Grade must be greater than 5 (currently 3)"
        }));
    }

    [Test]
    public void ToComeIntoScope_NestedAllGroupInsideAnAllGroup_FlattenedIntoRequirements()
    {
        var bullets = ScopingExplanationSummariser.ToComeIntoScope(Explain(Rule(All(
            Criterion(Department, SearchComparisonType.Equals, "Finance"),
            All(Criterion(Grade, SearchComparisonType.GreaterThan, 5))))));

        Assert.That(PlainText(bullets), Is.EqualTo(new[]
        {
            "Department must equal \"Finance\" (currently \"Engineering\")",
            "Grade must be greater than 5 (currently 3)"
        }));
    }

    [Test]
    public void ToComeIntoScope_SingleTopLevelAnyGroup_OneEitherBulletWithoutAnd()
    {
        var bullets = ScopingExplanationSummariser.ToComeIntoScope(Explain(Rule(Any(
            Criterion(Department, SearchComparisonType.Equals, "Finance"),
            Criterion(JobTitle, SearchComparisonType.Contains, "Accountant")))));

        Assert.That(PlainText(bullets), Is.EqualTo(new[]
        {
            "either Department equals \"Finance\" (currently \"Engineering\"), or Job Title contains \"Accountant\" (currently \"Software Engineer\")"
        }));
    }

    [Test]
    public void ToComeIntoScope_SeveralTopLevelGroups_PresentedAsAlternatives()
    {
        var bullets = ScopingExplanationSummariser.ToComeIntoScope(Explain(Rule(
            All(Criterion(Department, SearchComparisonType.Equals, "Finance"), Criterion(Grade, SearchComparisonType.GreaterThan, 5)),
            All(Criterion(Region, SearchComparisonType.Equals, "EMEA")),
            Any(Criterion(Active, SearchComparisonType.Equals, true), Criterion(CostCentre, SearchComparisonType.StartsWith, "FIN")))));

        Assert.That(PlainText(bullets), Is.EqualTo(new[]
        {
            "either Department equals \"Finance\" (currently \"Engineering\") and Grade is greater than 5 (currently 3)",
            "or Region equals \"EMEA\" (currently \"APAC\")",
            "or Active equals True (currently False) or Cost Centre starts with \"FIN\" (currently no value)"
        }));
    }

    [Test]
    public void ToComeIntoScope_AllGroupAsAnAlternativeHoldingAnAnyGroup_ParenthesisesTheInnerChoice()
    {
        var bullets = ScopingExplanationSummariser.ToComeIntoScope(Explain(Rule(All(
            Criterion(EmployeeStatus, SearchComparisonType.Equals, "Active"),
            Any(
                Criterion(JobTitle, SearchComparisonType.Contains, "Accountant"),
                All(Criterion(Department, SearchComparisonType.Equals, "Finance"),
                    Any(Criterion(Grade, SearchComparisonType.GreaterThan, 5), Criterion(Region, SearchComparisonType.Equals, "EMEA"))))))));

        Assert.That(PlainText(bullets), Is.EqualTo(new[]
        {
            "either Job Title contains \"Accountant\" (currently \"Software Engineer\"), " +
            "or Department equals \"Finance\" (currently \"Engineering\") and (Grade is greater than 5 (currently 3) or Region equals \"EMEA\" (currently \"APAC\"))"
        }));
    }

    [Test]
    public void ToComeIntoScope_InScope_NoBullets()
    {
        var bullets = ScopingExplanationSummariser.ToComeIntoScope(Explain(Rule(All(Criterion(EmployeeStatus, SearchComparisonType.Equals, "Active")))));

        Assert.That(bullets, Is.Empty);
    }

    [Test]
    public void ToComeIntoScope_MissingValueOnAPositiveComparison_SaysCurrentlyNoValue()
    {
        var bullets = ScopingExplanationSummariser.ToComeIntoScope(Explain(Rule(All(
            Criterion(CostCentre, SearchComparisonType.StartsWith, "FIN")))));

        Assert.That(PlainText(bullets), Is.EqualTo(new[] { "Cost Centre must start with \"FIN\" (currently no value)" }));
    }

    [Test]
    public void ToComeIntoScope_MissingValueOnANegatedComparison_SaysItNeedsAValue()
    {
        var explanation = Explain(Rule(
            All(Criterion(WorkerType, SearchComparisonType.NotEquals, "Test")),
            All(Criterion(WorkerType, SearchComparisonType.NotContains, "Temp"), Criterion(CostCentre, SearchComparisonType.NotEquals, null))));

        Assert.That(PlainText(ScopingExplanationSummariser.ToComeIntoScope(explanation)), Is.EqualTo(new[]
        {
            "either Worker Type has a value that does not equal \"Test\"",
            "or Worker Type has a value that does not contain \"Temp\" and Cost Centre has a value"
        }));
        Assert.That(PlainText(ScopingExplanationSummariser.ToComeIntoScope(Explain(Rule(All(Criterion(WorkerType, SearchComparisonType.NotEquals, "Test")))))),
            Is.EqualTo(new[] { "Worker Type needs a value that does not equal \"Test\"" }));
    }

    [Test]
    public void ToComeIntoScope_EmptyExpectedValueButAValueHeld_SaysItMustHaveNone()
    {
        var bullets = ScopingExplanationSummariser.ToComeIntoScope(Explain(Rule(All(Criterion(Region, SearchComparisonType.Equals, null)))));

        Assert.That(PlainText(bullets), Is.EqualTo(new[] { "Region must have no value (currently \"APAC\")" }));
    }

    [Test]
    public void ToComeIntoScope_RelativeDate_ShowsTheOffsetAndTheDateItResolvedTo()
    {
        var bullets = ScopingExplanationSummariser.ToComeIntoScope(Explain(Rule(All(
            RelativeDate(StartDate, SearchComparisonType.GreaterThan, 30, RelativeDateUnit.Days, RelativeDateDirection.Ago)))));

        Assert.That(PlainText(bullets), Is.EqualTo(new[] { "Start Date must be after 30 days ago, 4 Sep 2026 (currently 1 Aug 2026)" }));
    }

    [Test]
    public void ToComeIntoScope_BooleanAndDecimal_RenderedPlainly()
    {
        var bullets = ScopingExplanationSummariser.ToComeIntoScope(Explain(Rule(All(
            Criterion(Active, SearchComparisonType.Equals, true),
            Criterion(Allowance, SearchComparisonType.GreaterThanOrEquals, 1234.5m)))));

        Assert.That(PlainText(bullets), Is.EqualTo(new[]
        {
            "Active must equal True (currently False)",
            "Allowance must be greater than or equal to 1234.5 (currently 1000.25)"
        }));
    }

    [Test]
    public void ToComeIntoScope_ValueDiffersOnlyByCase_SaysTheComparisonIsCaseSensitive()
    {
        var person = Person((Department, "finance"));

        var caseSensitive = ScopingExplanationSummariser.ToComeIntoScope(Explain(Rule(All(Criterion(Department, SearchComparisonType.Equals, "Finance"))), person));
        var differentValue = ScopingExplanationSummariser.ToComeIntoScope(Explain(Rule(All(Criterion(Department, SearchComparisonType.Equals, "Sales"))), person));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(PlainText(caseSensitive), Is.EqualTo(new[] { "Department must equal \"Finance\" (case-sensitive; currently \"finance\")" }));
            Assert.That(PlainText(differentValue), Is.EqualTo(new[] { "Department must equal \"Sales\" (currently \"finance\")" }));
        }
    }

    [Test]
    public void ToComeIntoScope_FurtherValuesNotCompared_SaysHowMany()
    {
        var person = Person((Department, "Sales"), (Department, "Finance"), (Department, "Legal"));

        var bullets = ScopingExplanationSummariser.ToComeIntoScope(Explain(Rule(All(Criterion(Department, SearchComparisonType.Equals, "Finance"))), person));
        var describedActual = ScopingExplanationSummariser.DescribeActual(Explain(Rule(All(Criterion(Department, SearchComparisonType.Equals, "Finance"))), person)
            .Groups.Single().Criteria.Single());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(PlainText(bullets), Is.EqualTo(new[] { "Department must equal \"Finance\" (currently \"Sales\"; 2 more values not compared)" }));
            Assert.That(describedActual, Is.EqualTo("is Sales (2 more values not compared)"));
        }
    }

    [Test]
    public void ToComeIntoScope_CriterionWithNoAttribute_SaysItCanNeverBeMet()
    {
        var bullets = ScopingExplanationSummariser.ToComeIntoScope(Explain(Rule(All(
            Criterion(EmployeeStatus, SearchComparisonType.Equals, "Active"),
            Criterion(null, SearchComparisonType.Equals, "Finance")))));

        Assert.That(PlainText(bullets), Is.EqualTo(new[] { "Criterion 1.2 has no attribute and can never be met" }));
    }

    [Test]
    public void Summaries_CredentialAttribute_NeverShowTheValue()
    {
        const string secret = "Tr0ub4dor&3";
        var password = Attribute(30, "userPassword", AttributeDataType.Text);
        var person = Person((password, secret));
        var explanation = Explain(Rule(All(Criterion(password, SearchComparisonType.Equals, secret + "x"))), person);

        var summary = ScopingExplanationSummariser.SummariseNotConnected("Jane Smith", "Finance App", false, NotConnectedReason.NotInScope, explanation);
        var criterion = explanation.Groups.Single().Criteria.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(PlainText(summary.Bullets), Is.EqualTo(new[] { "userPassword must equal a hidden value (current value hidden)" }));
            Assert.That(summary.Bullets.Single().Segments.Where(s => s.Kind == ExplanationSegmentKind.Hidden).Select(s => s.Text),
                Is.EqualTo(new[] { "a hidden value", "current value hidden" }));
            Assert.That(ScopingExplanationSummariser.DescribeCriterion(criterion), Is.EqualTo("userPassword equals a hidden value"));
            Assert.That(ScopingExplanationSummariser.DescribeActual(criterion), Is.EqualTo("is hidden"));
            Assert.That(JsonSerializer.Serialize(summary), Does.Not.Contain(secret));
            Assert.That(summary.Hint, Is.EqualTo("Fails on userPassword"));
        }
    }

    #endregion

    #region Hints

    [Test]
    public void FailingAttributesHint_SameAttributeFailingTwice_NamedOnce()
    {
        var hint = ScopingExplanationSummariser.FailingAttributesHint(Explain(Rule(All(
            Criterion(Department, SearchComparisonType.Equals, "Finance"),
            Criterion(Department, SearchComparisonType.StartsWith, "Fin"),
            Criterion(Grade, SearchComparisonType.GreaterThan, 5)))));

        Assert.That(hint, Is.EqualTo("Fails on Department; Grade"));
    }

    [Test]
    public void FailingAttributesHint_AllGroupInsideAnAnyGroup_Parenthesised()
    {
        // Criteria come before child groups, as the evaluator takes them.
        var hint = ScopingExplanationSummariser.FailingAttributesHint(Explain(Rule(Any(
            Criterion(JobTitle, SearchComparisonType.Contains, "Accountant"),
            All(Criterion(Department, SearchComparisonType.Equals, "Finance"), Criterion(Grade, SearchComparisonType.GreaterThan, 5))))));

        Assert.That(hint, Is.EqualTo("Fails on Job Title or (Department; Grade)"));
    }

    [Test]
    public void FailingAttributesHint_SeveralTopLevelGroups_JoinedWithOr()
    {
        var hint = ScopingExplanationSummariser.FailingAttributesHint(Explain(Rule(
            All(Criterion(Department, SearchComparisonType.Equals, "Finance"), Criterion(Grade, SearchComparisonType.GreaterThan, 5)),
            All(Criterion(Region, SearchComparisonType.Equals, "EMEA")))));

        Assert.That(hint, Is.EqualTo("Fails on (Department; Grade) or Region"));
    }

    [Test]
    public void FailingAttributesHint_InScope_Empty()
    {
        var hint = ScopingExplanationSummariser.FailingAttributesHint(Explain(Rule(All(Criterion(EmployeeStatus, SearchComparisonType.Equals, "Active")))));

        Assert.That(hint, Is.Empty);
    }

    #endregion

    #region Other reasons

    /// <summary>
    /// Not marked for an export scope review (#1925): nothing will stage provisioning until the object's values change
    /// or the rule is saved with a change that can bring objects into scope, and the explanation says both.
    /// </summary>
    [Test]
    public void SummariseNotConnected_NotYetProvisionedAndNotMarkedForReview_SaysWhatStagesProvisioning()
    {
        var rule = Rule();
        rule.Name = "Learning Platform Users Export";

        var summary = ScopingExplanationSummariser.SummariseNotConnected("Jane Smith", "Learning Platform", false,
            NotConnectedReason.NotYetProvisioned, Explain(rule), scopeReviewPending: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary.Hint, Is.EqualTo("In scope; nothing staged yet"));
            Assert.That(summary.BulletsTitle, Is.EqualTo("What happens next"));
            Assert.That(summary.Summary, Is.EqualTo(
                "Jane Smith is not provisioned to Learning Platform.\n" +
                "Reason: in scope of the Synchronisation Rule \"Learning Platform Users Export\", but nothing has been staged yet.\n" +
                "What happens next:\n" +
                "- Provisioning is staged the next time this Metaverse Object's attribute values change during synchronisation.\n" +
                "- Saving a change to the Synchronisation Rule that can bring objects into its scope also stages it, at the next synchronisation of any Connected System.\n" +
                "Evaluated 4 Oct 2026 10:41 UTC."));
        }
    }

    /// <summary>
    /// Marked for an export scope review (a rule change or a relative date moved its scope, #1925): the next
    /// synchronisation of any Connected System stages provisioning, so the explanation says that rather than waiting.
    /// </summary>
    [Test]
    public void SummariseNotConnected_NotYetProvisionedAndMarkedForReview_SaysTheNextSynchronisationStagesIt()
    {
        var rule = Rule();
        rule.Name = "Learning Platform Users Export";

        var summary = ScopingExplanationSummariser.SummariseNotConnected("Jane Smith", "Learning Platform", false,
            NotConnectedReason.NotYetProvisioned, Explain(rule), scopeReviewPending: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary.Hint, Is.EqualTo("In scope; staged at the next synchronisation"));
            Assert.That(summary.BulletsTitle, Is.EqualTo("What happens next"));
            Assert.That(summary.Summary, Is.EqualTo(
                "Jane Smith is not provisioned to Learning Platform.\n" +
                "Reason: in scope of the Synchronisation Rule \"Learning Platform Users Export\"; the next synchronisation stages its provisioning.\n" +
                "What happens next:\n" +
                "- This Metaverse Object is marked for an export scope review.\n" +
                "- The next synchronisation of any Connected System reviews it and stages its provisioning to Learning Platform.\n" +
                "Evaluated 4 Oct 2026 10:41 UTC."));
        }
    }

    [Test]
    public void SummariseNotConnected_ProvisioningDisabled_SaysWhatWouldConnectIt()
    {
        var rule = Rule();
        rule.Name = "Contractor Portal Users Export";

        var summary = ScopingExplanationSummariser.SummariseNotConnected("Jane Smith", "Contractor Portal", false,
            NotConnectedReason.ProvisioningDisabled, Explain(rule));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary.Hint, Is.EqualTo("In scope; provisioning is off"));
            Assert.That(summary.BulletsTitle, Is.EqualTo("What would connect it"));
            Assert.That(summary.Summary, Is.EqualTo(
                "Jane Smith is not provisioned to Contractor Portal.\n" +
                "Reason: in scope of the Synchronisation Rule \"Contractor Portal Users Export\", but the rule does not provision new objects.\n" +
                "What would connect it:\n" +
                "- Switch on Provision to Connected System in the Synchronisation Rule, or\n" +
                "- create an object in Contractor Portal that joins to this Metaverse Object.\n" +
                "Evaluated 4 Oct 2026 10:41 UTC."));
        }
    }

    [Test]
    public void SummariseNotConnected_RuleMisconfigured_NamesEachInvalidCriterion()
    {
        var explanation = Explain(Rule(All(
            Criterion(EmployeeStatus, SearchComparisonType.Equals, "Active"),
            Criterion(StartDate, SearchComparisonType.StartsWith, null),
            Any(Criterion(Active, SearchComparisonType.GreaterThan, true)))));

        var summary = ScopingExplanationSummariser.SummariseNotConnected("Jane Smith", "Finance App", false, NotConnectedReason.RuleMisconfigured, explanation);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(explanation.Outcome, Is.EqualTo(ScopingRuleOutcome.Undetermined), "precondition");
            Assert.That(summary.Hint, Is.EqualTo("Invalid criteria on Start Date and Active"));
            Assert.That(summary.BulletsTitle, Is.EqualTo("What would fix it"));
            Assert.That(PlainText(summary.Bullets), Is.EqualTo(new[]
            {
                "Criterion 1.2 compares Start Date with \"starts with\", which a date attribute cannot use; correct or remove it in the Synchronisation Rule.",
                "Criterion 1.3.1 compares Active with \"greater than\", which a Boolean attribute cannot use; correct or remove it in the Synchronisation Rule."
            }));
            Assert.That(summary.Summary, Does.StartWith(
                "Jane Smith is not provisioned to Finance App.\n" +
                "Reason: the scoping of the Synchronisation Rule \"Finance App Users Export\" cannot be evaluated.\n" +
                "What would fix it:\n- Criterion 1.2 compares"));
        }
    }

    [Test]
    public void SummariseNotConnected_ConnectedSystemDisabled_QualifiesTheHintAndSaysSoInTheSummary()
    {
        var summary = ScopingExplanationSummariser.SummariseNotConnected("Jane Smith", "Finance App", connectedSystemDisabled: true,
            NotConnectedReason.NotInScope, Explain(FinanceAppRule()));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(summary.Hint, Is.EqualTo("Fails on Department; Cost Centre or Job Title · Connected System disabled"));
            Assert.That(summary.Summary, Does.Contain(
                "Reason: not in scope of the Synchronisation Rule \"Finance App Users Export\".\n" +
                "Note: the Connected System Finance App is disabled.\n" +
                "To come into scope:\n"));
        }
    }

    [Test]
    public void SummariseNotConnected_NoDisplayName_NamesTheObjectGenerically()
    {
        var summary = ScopingExplanationSummariser.SummariseNotConnected(null, "Finance App", false, NotConnectedReason.NotInScope, Explain(FinanceAppRule()));

        Assert.That(summary.Summary, Does.StartWith("This Metaverse Object is not provisioned to Finance App.\n"));
    }

    #endregion

    #region Formatting

    [TestCase(2026, 10, 4, 10, 41, 0, "4 Oct 2026 10:41 UTC")]
    [TestCase(2026, 10, 4, 10, 41, 59, "4 Oct 2026 10:41 UTC")]
    [TestCase(2026, 1, 15, 0, 0, 0, "15 Jan 2026 00:00 UTC")]
    public void FormatEvaluatedAt_AlwaysMinutesInUtc(int year, int month, int day, int hour, int minute, int second, string expected)
    {
        Assert.That(ScopingExplanationSummariser.FormatEvaluatedAt(new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc)), Is.EqualTo(expected));
    }

    [Test]
    public void Summaries_UnderAnotherCulture_ReadTheSame()
    {
        var invariant = ScopingExplanationSummariser.SummariseNotConnected("Jane Smith", "Finance App", false, NotConnectedReason.NotInScope,
            Explain(Rule(All(Criterion(Allowance, SearchComparisonType.GreaterThan, 1234.5m), RelativeDate(StartDate, SearchComparisonType.GreaterThan, 30, RelativeDateUnit.Days, RelativeDateDirection.Ago)))));

        var originalCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            var german = ScopingExplanationSummariser.SummariseNotConnected("Jane Smith", "Finance App", false, NotConnectedReason.NotInScope,
                Explain(Rule(All(Criterion(Allowance, SearchComparisonType.GreaterThan, 1234.5m), RelativeDate(StartDate, SearchComparisonType.GreaterThan, 30, RelativeDateUnit.Days, RelativeDateDirection.Ago)))));

            Assert.That(german.Summary, Is.EqualTo(invariant.Summary));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Test]
    public void Summaries_EveryReason_UseNoEmDashes()
    {
        var explanations = new[] { Explain(FinanceAppRule()), Explain(Rule()), Explain(Rule(All(Criterion(StartDate, SearchComparisonType.Contains, null)))) };
        var text = new List<string>();
        foreach (var reason in Enum.GetValues<NotConnectedReason>())
        {
            var explanation = reason switch
            {
                NotConnectedReason.NotInScope => explanations[0],
                NotConnectedReason.RuleMisconfigured => explanations[2],
                _ => explanations[1]
            };
            var summary = ScopingExplanationSummariser.SummariseNotConnected("Jane Smith", "Finance App", true, reason, explanation);
            text.Add(summary.Hint);
            text.Add(summary.Summary);
            text.AddRange(summary.Bullets.Select(Joined));
        }

        Assert.That(text, Has.None.Contains("—"));
    }

    #endregion
}
