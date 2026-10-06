// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using JIM.Models.Logic;
using JIM.Models.Logic.Scoping;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;

namespace JIM.Web.Tests;

/// <summary>
/// Explanations shaped like the PRD's Scenario 1 (#348), as the server returns them: every word already written,
/// so the portal's tests can check it shows the server's text rather than wording its own.
/// </summary>
internal static class ConnectionExplanationTestData
{
    public static readonly DateTime EvaluatedAt = new(2026, 10, 4, 10, 41, 0, DateTimeKind.Utc);

    public static ScopingCriterionExplanation Criterion(string path, ScopingCriterionOutcome outcome, string description,
        string actualDescription) => new()
    {
        Path = path,
        AttributeName = description.Split(' ')[0],
        Outcome = outcome,
        Description = description,
        ActualDescription = actualDescription
    };

    /// <summary>
    /// "Finance App Users Export": Employee Status equals Active, and Department equals Finance, and either Cost Centre
    /// starts with FIN or Job Title contains Accountant; Jane Smith fails it.
    /// </summary>
    public static ScopingExplanation FinanceAppScoping() => new()
    {
        SyncRuleId = 7,
        SyncRuleName = "Finance App Users Export",
        Direction = SyncRuleDirection.Export,
        Outcome = ScopingRuleOutcome.OutOfScope,
        HasCriteria = true,
        EvaluatedAt = EvaluatedAt,
        Hint = "Fails on Department; Cost Centre or Job Title",
        Groups =
        [
            new ScopingGroupExplanation
            {
                Path = "1",
                Type = SearchGroupType.All,
                Met = false,
                Description = "All of these must be met (not met)",
                Criteria =
                [
                    Criterion("1.1", ScopingCriterionOutcome.Met, "Employee Status equals Active", "is Active"),
                    Criterion("1.2", ScopingCriterionOutcome.NotMet, "Department equals Finance", "is Engineering")
                ],
                ChildGroups =
                [
                    new ScopingGroupExplanation
                    {
                        Path = "1.3",
                        Type = SearchGroupType.Any,
                        Met = false,
                        Description = "Any one of these (none met)",
                        Criteria =
                        [
                            Criterion("1.3.1", ScopingCriterionOutcome.NoValue, "Cost Centre starts with FIN", "has no value"),
                            Criterion("1.3.2", ScopingCriterionOutcome.NotMet, "Job Title contains Accountant (ignoring case)", "is Software Engineer")
                        ]
                    }
                ]
            }
        ]
    };

    public static ScopingExplanation InScopeWithoutCriteria(int syncRuleId, string syncRuleName, SyncRuleDirection direction) => new()
    {
        SyncRuleId = syncRuleId,
        SyncRuleName = syncRuleName,
        Direction = direction,
        Outcome = ScopingRuleOutcome.InScope,
        HasCriteria = false,
        EvaluatedAt = EvaluatedAt
    };

    public static ExplanationBullet Bullet(params (ExplanationSegmentKind Kind, string Text)[] segments)
    {
        var bullet = new ExplanationBullet();
        foreach (var (kind, text) in segments)
        {
            bullet.Segments.Add(new ExplanationSegment { Kind = kind, Text = text });
            bullet.PlainText += text;
        }
        return bullet;
    }

    public static List<ExplanationBullet> FinanceAppBullets() =>
    [
        Bullet((ExplanationSegmentKind.Attribute, "Department"), (ExplanationSegmentKind.Text, " must equal "),
            (ExplanationSegmentKind.ExpectedValue, "Finance"), (ExplanationSegmentKind.Text, " (currently "),
            (ExplanationSegmentKind.CurrentValue, "Engineering"), (ExplanationSegmentKind.Text, ")")),
        Bullet((ExplanationSegmentKind.Text, "and either "), (ExplanationSegmentKind.Attribute, "Cost Centre"),
            (ExplanationSegmentKind.Text, " starts with "), (ExplanationSegmentKind.ExpectedValue, "FIN"),
            (ExplanationSegmentKind.Text, " (currently "), (ExplanationSegmentKind.NoValue, "no value"),
            (ExplanationSegmentKind.Text, "), or "), (ExplanationSegmentKind.Attribute, "Job Title"),
            (ExplanationSegmentKind.Text, " contains "), (ExplanationSegmentKind.ExpectedValue, "Accountant"),
            (ExplanationSegmentKind.Text, " (currently "), (ExplanationSegmentKind.CurrentValue, "Software Engineer"),
            (ExplanationSegmentKind.Text, ")"))
    ];

    public const string FinanceAppSummary =
        "Jane Smith is not provisioned to Finance App.\n" +
        "Reason: not in scope of the Synchronisation Rule \"Finance App Users Export\".\n" +
        "To come into scope:\n" +
        "- Department must equal \"Finance\" (currently \"Engineering\")\n" +
        "- and either Cost Centre starts with \"FIN\" (currently no value), or Job Title contains \"Accountant\" (currently \"Software Engineer\")\n" +
        "Evaluated 4 Oct 2026 10:41 UTC.";

    public static NotConnectedEntry FinanceApp() => new()
    {
        ConnectedSystemId = 3,
        ConnectedSystemName = "Finance App",
        ConnectedSystemStatus = ConnectedSystemStatus.Active,
        SyncRuleId = 7,
        SyncRuleName = "Finance App Users Export",
        ObjectTypeName = "user",
        Reason = NotConnectedReason.NotInScope,
        Hint = "Fails on Department; Cost Centre or Job Title",
        BulletsTitle = "To come into scope",
        Bullets = FinanceAppBullets(),
        Summary = FinanceAppSummary,
        Scoping = FinanceAppScoping()
    };

    public static NotConnectedEntry LearningPlatform() => new()
    {
        ConnectedSystemId = 4,
        ConnectedSystemName = "Learning Platform",
        ConnectedSystemStatus = ConnectedSystemStatus.Active,
        SyncRuleId = 8,
        SyncRuleName = "Learning Platform Users Export",
        ObjectTypeName = "user",
        Reason = NotConnectedReason.NotYetProvisioned,
        Hint = "In scope; nothing staged yet",
        BulletsTitle = "What happens next",
        Bullets =
        [
            Bullet((ExplanationSegmentKind.Text, "Provisioning is staged the next time this Metaverse Object's attribute values change during synchronisation.")),
            Bullet((ExplanationSegmentKind.Text, "Saving a change to the Synchronisation Rule that can bring objects into its scope also stages it, at the next synchronisation of any Connected System."))
        ],
        Summary = "Jane Smith is not provisioned to Learning Platform.\nReason: in scope of the Synchronisation Rule \"Learning Platform Users Export\", but nothing has been staged yet.\nWhat happens next:\n- Provisioning is staged the next time this Metaverse Object's attribute values change during synchronisation.\n- Saving a change to the Synchronisation Rule that can bring objects into its scope also stages it, at the next synchronisation of any Connected System.\nEvaluated 4 Oct 2026 10:41 UTC.",
        Scoping = InScopeWithoutCriteria(8, "Learning Platform Users Export", SyncRuleDirection.Export)
    };

    /// <summary>Jane Smith's HR System object, projected by "HR Users Import" and in scope of it.</summary>
    public static MetaverseObjectConnectionExplanation HrSystem() => new()
    {
        ConnectedSystemObjectId = Guid.NewGuid(),
        DisplayName = "Jane Smith (E10442)",
        ConnectedSystemId = 1,
        ConnectedSystemName = "HR System",
        ObjectTypeName = "person",
        JoinType = ConnectedSystemObjectJoinType.Projected,
        IsSource = true,
        State = ConnectedSystemObjectConnectionState.InSync,
        LastSynchronised = EvaluatedAt.AddHours(-2),
        Join = new JoinRecord
        {
            JoinType = ConnectedSystemObjectJoinType.Projected,
            Method = ConnectedSystemObjectJoinMethod.Projection,
            DateJoined = new DateTime(2026, 3, 14, 9, 12, 0, DateTimeKind.Utc),
            SyncRuleId = 2,
            SyncRuleName = "HR Users Import",
            Source = JoinRecordSource.Recorded,
            Description = "Projected by the Synchronisation Rule \"HR Users Import\"",
            ActivityId = Guid.NewGuid(),
            RunProfileExecutionItemId = Guid.NewGuid()
        },
        Scoping = [InScopeWithoutCriteria(2, "HR Users Import", SyncRuleDirection.Import)]
    };
}
