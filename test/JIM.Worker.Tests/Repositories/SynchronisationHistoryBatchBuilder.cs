// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Transactional;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// A synchronisation flush's history as the processor leaves it before preparing it (#2874): ids unassigned wherever
/// the run leaves them so, references held as navigations, buffers full. Every column of every history table is given a
/// value, so a write of it can be checked column by column.
/// </summary>
internal sealed class SynchronisationHistoryBatchBuilder
{
    public Guid ActivityId { get; }

    public ConnectedSystemObject ProjectedObject { get; } = new()
    {
        Id = Guid.NewGuid(),
        Type = new ConnectedSystemObjectType { Id = 7, Name = "person" }
    };

    public ActivityRunProfileExecutionItem ProjectedItem { get; }
    public ActivityRunProfileExecutionItemSyncOutcome RootOutcome { get; }
    public ActivityRunProfileExecutionItemSyncOutcome ChildOutcome { get; }
    public ConnectedSystemObjectChange OutcomeChangeRecord { get; }
    public ConnectedSystemObjectChange ItemChangeRecord { get; }
    public CausalEdge Edge { get; }
    public ActivityRunProfileExecutionItemMatchCandidate Candidate { get; }
    public ActivityRunProfileExecutionItem AmbiguousItem { get; }
    public MetaverseObjectChange MetaverseObjectChange { get; }

    /// <param name="activityId">The Activity the items belong to.</param>
    /// <param name="connectedSystemId">The Connected System the change records and edges name.</param>
    /// <param name="syncRuleId">The Synchronisation Rule the outcomes, change records and edges name.</param>
    /// <param name="connectedSystemAttributeId">The Connected System attribute the change records' rows name.</param>
    /// <param name="metaverseAttributeId">The Metaverse attribute the Metaverse Object change record's row names.</param>
    public SynchronisationHistoryBatchBuilder(Guid activityId, int connectedSystemId = 1, int syncRuleId = 3,
        int connectedSystemAttributeId = 11, int metaverseAttributeId = 12)
    {
        ActivityId = activityId;
        var connectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Id = connectedSystemAttributeId, Name = "displayName" };
        var metaverseAttribute = new MetaverseAttribute { Id = metaverseAttributeId, Name = "Display Name" };

        ProjectedItem = new ActivityRunProfileExecutionItem
        {
            ActivityId = activityId,
            ObjectChangeType = ObjectChangeType.Projected,
            NoChangeReason = NoChangeReason.MvoNoAttributeChanges,
            ConnectedSystemObject = ProjectedObject,
            ExternalIdSnapshot = "E1007",
            DisplayNameSnapshot = "Jo Bloggs",
            ObjectTypeSnapshot = "snapshotted type",
            ErrorType = ActivityRunProfileExecutionItemErrorType.NotSet,
            ErrorMessage = "a message",
            ErrorStackTrace = "at Foo.Bar()",
            AttributeFlowCount = 3,
            UnresolvedReferenceCount = 1,
            OutcomeSummary = "Projected:1,AttributeFlow:1",
            PendingExportId = Guid.NewGuid(),
            DeletionPolicySnapshotJson = """{"deletionRule":"WhenLastConnectorDisconnected"}""",
            MetaverseObjectId = Guid.NewGuid(),
            AttributeValueErrorsJson = """{"total": 1}""",
            SourceLocation = "hr.csv, row 2"
        };

        RootOutcome = new ActivityRunProfileExecutionItemSyncOutcome
        {
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.Projected,
            TargetEntityId = Guid.NewGuid(),
            TargetEntityDescription = "Jo Bloggs",
            DetailCount = 2,
            DetailMessage = "projected",
            Ordinal = 0,
            SyncRuleId = syncRuleId,
            SyncRuleName = "HR Import",
            StagedChangeType = PendingExportChangeType.Update,
            ObjectMatchingRuleId = 4,
            ObjectMatchingRuleDescriptionSnapshot = "Employee Id equals employeeId",
            MatchedValue = "E1007"
        };
        OutcomeChangeRecord = new ConnectedSystemObjectChange
        {
            ConnectedSystemId = connectedSystemId,
            ConnectedSystemObjectId = Guid.NewGuid(),
            ChangeTime = new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc),
            ChangeType = ObjectChangeType.Updated,
            InitiatedByType = ActivityInitiatorType.User,
            InitiatedById = Guid.NewGuid(),
            InitiatedByName = "Jane Admin",
            DeletedObjectExternalId = "gone",
            DeletedObjectDisplayName = "Gone Object",
            DeletedConnectedSystemObjectId = Guid.NewGuid()
        };
        var outcomeAttribute = new ConnectedSystemObjectChangeAttribute
        {
            ConnectedSystemChange = OutcomeChangeRecord, Attribute = connectedSystemAttribute, AttributeName = "displayName", AttributeType = AttributeDataType.Text
        };
        outcomeAttribute.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue
        {
            ConnectedSystemObjectChangeAttribute = outcomeAttribute, ValueChangeType = ValueChangeType.Add, StringValue = "Jo Bloggs",
            IntValue = 5, LongValue = 6, DecimalValue = 7.5m, ByteValueLength = 8, GuidValue = Guid.NewGuid(), BoolValue = true,
            DateTimeValue = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), IsPendingExportStub = true, SyncRuleId = syncRuleId, SyncRuleName = "HR Import"
        });
        OutcomeChangeRecord.AttributeChanges.Add(outcomeAttribute);

        ChildOutcome = new ActivityRunProfileExecutionItemSyncOutcome
        {
            ParentSyncOutcome = RootOutcome,
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated,
            TargetEntityDescription = "Directory",
            Ordinal = 0,
            ConnectedSystemObjectChange = OutcomeChangeRecord
        };
        RootOutcome.Children.Add(ChildOutcome);
        ProjectedItem.SyncOutcomes.Add(RootOutcome);
        ProjectedItem.SyncOutcomes.Add(ChildOutcome);

        ItemChangeRecord = new ConnectedSystemObjectChange
        {
            ConnectedSystemId = connectedSystemId,
            ConnectedSystemObjectId = ProjectedObject.Id,
            ChangeTime = new DateTime(2026, 11, 9, 10, 0, 1, DateTimeKind.Utc),
            ChangeType = ObjectChangeType.Added,
            InitiatedByType = ActivityInitiatorType.System,
            InitiatedByName = "System"
        };
        var itemAttribute = new ConnectedSystemObjectChangeAttribute
        {
            ConnectedSystemChange = ItemChangeRecord, Attribute = connectedSystemAttribute, AttributeName = "cn", AttributeType = AttributeDataType.Text
        };
        itemAttribute.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue
        {
            ConnectedSystemObjectChangeAttribute = itemAttribute, ValueChangeType = ValueChangeType.Add, StringValue = "Jo"
        });
        ItemChangeRecord.AttributeChanges.Add(itemAttribute);
        ProjectedItem.ConnectedSystemObjectChange = ItemChangeRecord;

        Edge = new CausalEdge
        {
            EffectSyncOutcome = ChildOutcome,
            CauseMetaverseObjectId = Guid.NewGuid(),
            CauseConnectedSystemObjectId = Guid.NewGuid(),
            CausePendingExportId = Guid.NewGuid(),
            CauseDisplayName = "Lee Leaver",
            CauseObjectTypeName = "Person",
            CauseObjectTypePluralName = "People",
            EffectAttributeName = "manager",
            EdgeType = CausalEdgeType.MetaverseObjectDeletionCausedReferenceRemoval,
            ReasonCode = CausalReasonCode.AllAuthoritativeSourcesDisconnected,
            ConnectedSystemId = connectedSystemId,
            ConnectedSystemName = "HR",
            SyncRuleId = syncRuleId,
            SyncRuleName = "HR Import",
            Created = new DateTime(2026, 11, 9, 10, 0, 2, DateTimeKind.Utc)
        };
        ProjectedItem.CausalEdges.Add(Edge);

        AmbiguousItem = new ActivityRunProfileExecutionItem
        {
            ActivityId = activityId,
            ObjectChangeType = ObjectChangeType.NotSet,
            ErrorType = ActivityRunProfileExecutionItemErrorType.AmbiguousMatch,
            ErrorMessage = "matches two",
            ConnectedSystemObjectId = Guid.NewGuid(),
            DisplayNameSnapshot = "Amy"
        };
        Candidate = new ActivityRunProfileExecutionItemMatchCandidate
        {
            CandidateKind = MatchCandidateKind.MetaverseObject,
            CandidateId = Guid.NewGuid(),
            DisplayNameSnapshot = "Amy",
            TypeNameSnapshot = "Person",
            ConnectedSystemId = connectedSystemId,
            ConnectedSystemNameSnapshot = "HR",
            MatchedValue = "E30",
            ObjectMatchingRuleId = 4,
            ObjectMatchingRuleDescriptionSnapshot = "Employee Id equals employeeId",
            Ordinal = 1,
            CandidateTotal = 2
        };
        AmbiguousItem.MatchCandidates.Add(Candidate);

        MetaverseObjectChange = new MetaverseObjectChange
        {
            MetaverseObjectId = Guid.NewGuid(),
            ActivityRunProfileExecutionItem = ProjectedItem,
            ActivityId = activityId,
            ChangeTime = new DateTime(2026, 11, 9, 10, 0, 3, DateTimeKind.Utc),
            ChangeType = ObjectChangeType.Projected,
            ChangeInitiatorType = MetaverseObjectChangeInitiatorType.SynchronisationRule,
            InitiatedByType = ActivityInitiatorType.User,
            InitiatedById = Guid.NewGuid(),
            InitiatedByName = "Jane Admin",
            SyncRuleId = syncRuleId,
            SyncRuleName = "HR Import",
            DeletedMetaverseObjectId = Guid.NewGuid(),
            DeletedObjectDisplayName = "Old",
            DeletedByRunProfileExecutionItemId = Guid.NewGuid()
        };
        var mvoAttribute = new MetaverseObjectChangeAttribute
        {
            MetaverseObjectChange = MetaverseObjectChange, Attribute = metaverseAttribute, AttributeName = "Display Name", AttributeType = AttributeDataType.Text
        };
        mvoAttribute.ValueChanges.Add(new MetaverseObjectChangeAttributeValue
        {
            MetaverseObjectChangeAttribute = mvoAttribute, ValueChangeType = ValueChangeType.Add, StringValue = "Jo Bloggs",
            IntValue = 1, LongValue = 2, DecimalValue = 3.25m, ByteValueLength = 4, GuidValue = Guid.NewGuid(), BoolValue = false,
            DateTimeValue = new DateTime(2025, 5, 6, 7, 8, 9, DateTimeKind.Utc), ReferenceValueId = Guid.NewGuid(),
            ContributedBySyncRuleId = syncRuleId, ContributedBySyncRuleName = "HR Import", ContributedByKind = ValueContributorKind.SynchronisationRule,
            ContributedByClaimType = "claim"
        });
        MetaverseObjectChange.AttributeChanges.Add(mvoAttribute);
    }

    public List<ActivityRunProfileExecutionItem> Items => [ProjectedItem, AmbiguousItem];

    /// <summary>
    /// Attribute rows appended to an existing Metaverse Object change record, as the cross-page reference step queues
    /// them: the record's id set, its new rows' ids not.
    /// </summary>
    public static MetaverseObjectChange AppendTo(Guid existingChangeId)
    {
        var append = new MetaverseObjectChange { Id = existingChangeId };
        var attribute = new MetaverseObjectChangeAttribute
        {
            // History names its attribute by plain id with no foreign key (#2931); none needs to exist behind it here.
            MetaverseObjectChange = append, AttributeId = 1, AttributeName = "Manager", AttributeType = AttributeDataType.Reference
        };
        attribute.ValueChanges.Add(new MetaverseObjectChangeAttributeValue
        {
            MetaverseObjectChangeAttribute = attribute, ValueChangeType = ValueChangeType.Add, ReferenceValueId = Guid.NewGuid()
        });
        append.AttributeChanges.Add(attribute);
        return append;
    }
}
