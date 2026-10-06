// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Logic;
using JIM.Models.Logic.Scoping;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Transactional;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for <see cref="JIM.Application.Servers.MetaverseServer.GetMetaverseObjectConnectionExplanationsAsync"/> (#348):
/// joined connections with how they were joined and their scoping evaluated now, and the enabled export rules whose
/// Connected System holds no joined object, each with exactly one reason.
/// </summary>
[TestFixture]
public class MetaverseObjectConnectionExplanationsTests
{
    private const int PersonTypeId = 3;
    private const int FinanceAppId = 10;
    private const int PayrollId = 20;
    private const int AccountTypeId = 100;
    private const int GroupTypeId = 101;
    private const int EmployeeTypeId = 200;

    private static readonly MetaverseAttribute Department = new() { Id = 1, Name = "Department", Type = AttributeDataType.Text };
    private static readonly MetaverseAttribute CostCentre = new() { Id = 2, Name = "Cost Centre", Type = AttributeDataType.Text };
    private static readonly MetaverseAttribute StartDate = new() { Id = 3, Name = "Start Date", Type = AttributeDataType.DateTime };
    private static readonly ConnectedSystemObjectTypeAttribute PayrollDepartment = new() { Id = 50, Name = "dept", Type = AttributeDataType.Text };

    private Mock<IRepository> _repository = null!;
    private Mock<IConnectedSystemRepository> _connectedSystems = null!;
    private Mock<IMetaverseRepository> _metaverse = null!;
    private JimApplication _application = null!;

    private Guid _mvoId;

    private MetaverseObjectHeader _header = null!;
    private List<ConnectedSystemObject> _joined = null!;
    private List<SyncRule> _rules = null!;
    private List<MetaverseObjectAttributeValue> _mvoValues = null!;
    private List<ConnectedSystemObjectAttributeValue> _csoValues = null!;
    private Dictionary<Guid, PendingExport> _pendingExports = null!;
    private List<JoinHistoryEntry> _history = null!;
    private int _nextId = 1000;

    [SetUp]
    public void SetUp()
    {
        _repository = new Mock<IRepository>();
        _connectedSystems = new Mock<IConnectedSystemRepository>();
        _metaverse = new Mock<IMetaverseRepository>();
        _repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystems.Object);
        _repository.Setup(r => r.Metaverse).Returns(_metaverse.Object);
        _application = new JimApplication(_repository.Object);

        _mvoId = Guid.NewGuid();
        _joined = [];
        _rules = [];
        _mvoValues = [];
        _csoValues = [];
        _pendingExports = [];
        _history = [];

        _header = new MetaverseObjectHeader
        {
            Id = _mvoId, TypeId = PersonTypeId, TypeName = "Person", TypePluralName = "People", CachedDisplayName = "Jane Smith"
        };
        _metaverse.Setup(r => r.GetMetaverseObjectHeaderAsync(_mvoId)).ReturnsAsync(() => _header);
        _connectedSystems.Setup(r => r.GetConnectedSystemObjectsCoreByMetaverseObjectIdAsync(_mvoId)).ReturnsAsync(() => _joined);
        _connectedSystems.Setup(r => r.GetSyncRulesForScopingExplanationAsync(PersonTypeId)).ReturnsAsync(() => _rules);
        _metaverse.Setup(r => r.GetMetaverseObjectAttributeValuesAsync(_mvoId, It.IsAny<IReadOnlyCollection<int>>()))
            .ReturnsAsync((Guid _, IReadOnlyCollection<int> ids) => _mvoValues.Where(v => ids.Contains(v.AttributeId)).ToList());
        _connectedSystems.Setup(r => r.GetConnectedSystemObjectAttributeValuesAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<IReadOnlyCollection<int>>()))
            .ReturnsAsync((IReadOnlyCollection<Guid> csoIds, IReadOnlyCollection<int> ids) =>
                _csoValues.Where(v => csoIds.Contains(v.ConnectedSystemObject.Id) && ids.Contains(v.AttributeId)).ToList());
        _connectedSystems.Setup(r => r.GetPendingExportsLightweightByConnectedSystemObjectIdsAsync(It.IsAny<IEnumerable<Guid>>()))
            .ReturnsAsync(() => _pendingExports);
        _connectedSystems.Setup(r => r.GetJoinHistoryAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<IReadOnlyDictionary<Guid, Guid>>()))
            .ReturnsAsync(() => _history);
    }

    #region Builders

    private SyncRule ExportRule(string name, int connectedSystemId = FinanceAppId, string systemName = "Finance App", int objectTypeId = AccountTypeId,
        string objectTypeName = "account", bool? provision = true, ConnectedSystemStatus status = ConnectedSystemStatus.Active,
        params SyncRuleScopingCriteriaGroup[] groups) =>
        Rule(name, SyncRuleDirection.Export, connectedSystemId, systemName, objectTypeId, objectTypeName, provision, status, groups);

    private SyncRule Rule(string name, SyncRuleDirection direction, int connectedSystemId, string systemName, int objectTypeId, string objectTypeName,
        bool? provision, ConnectedSystemStatus status, SyncRuleScopingCriteriaGroup[] groups)
    {
        var rule = new SyncRule
        {
            Id = _nextId++,
            Name = name,
            Direction = direction,
            Enabled = true,
            ConnectedSystemId = connectedSystemId,
            ConnectedSystem = new ConnectedSystem { Id = connectedSystemId, Name = systemName, Status = status },
            ConnectedSystemObjectTypeId = objectTypeId,
            ConnectedSystemObjectType = new ConnectedSystemObjectType { Id = objectTypeId, Name = objectTypeName },
            MetaverseObjectTypeId = PersonTypeId,
            ProvisionToConnectedSystem = provision
        };
        rule.ObjectScopingCriteriaGroups.AddRange(groups);
        _rules.Add(rule);
        return rule;
    }

    private SyncRuleScopingCriteriaGroup All(params SyncRuleScopingCriteria[] criteria)
    {
        var group = new SyncRuleScopingCriteriaGroup { Id = _nextId++, Type = SearchGroupType.All };
        group.Criteria.AddRange(criteria);
        return group;
    }

    private SyncRuleScopingCriteria Equals(MetaverseAttribute attribute, string value) =>
        new() { Id = _nextId++, MetaverseAttribute = attribute, MetaverseAttributeId = attribute.Id, ComparisonType = SearchComparisonType.Equals, StringValue = value };

    private ConnectedSystemObject Joined(int connectedSystemId, string systemName, int objectTypeId, string objectTypeName,
        ConnectedSystemObjectJoinType joinType = ConnectedSystemObjectJoinType.Joined,
        ConnectedSystemObjectStatus status = ConnectedSystemObjectStatus.Normal, DateTime? dateJoined = null)
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystemId,
            ConnectedSystem = new ConnectedSystem { Id = connectedSystemId, Name = systemName },
            TypeId = objectTypeId,
            Type = new ConnectedSystemObjectType { Id = objectTypeId, Name = objectTypeName },
            MetaverseObjectId = _mvoId,
            Status = status,
            JoinType = joinType,
            DateJoined = dateJoined ?? new DateTime(2026, 3, 14, 9, 12, 0, DateTimeKind.Utc),
            AttributeValues = []
        };
        _joined.Add(cso);
        return cso;
    }

    private void MvoHas(MetaverseAttribute attribute, string value) =>
        _mvoValues.Add(new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = attribute.Id, StringValue = value });

    private Task<MetaverseObjectConnectionExplanations?> ExplainAsync(bool includeNotConnected = true) =>
        _application.Metaverse.GetMetaverseObjectConnectionExplanationsAsync(_mvoId, includeNotConnected);

    #endregion

    [Test]
    public async Task GetMetaverseObjectConnectionExplanationsAsync_UnknownObject_ReturnsNullAsync()
    {
        var result = await _application.Metaverse.GetMetaverseObjectConnectionExplanationsAsync(Guid.NewGuid(), includeNotConnected: true);

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetMetaverseObjectConnectionExplanationsAsync_NothingJoinedNoRules_ReturnsEmptyListsAndDisplayNameAsync()
    {
        var result = (await ExplainAsync())!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.DisplayName, Is.EqualTo("Jane Smith"));
            Assert.That(result.Connections, Is.Empty);
            Assert.That(result.NotConnected, Is.Empty, "an empty list, not null, so \"connected everywhere it could be\" is an answer");
        }
    }

    [Test]
    public async Task GetMetaverseObjectConnectionExplanationsAsync_NotConnectedNotRequested_IsNullAndNotEvaluatedAsync()
    {
        ExportRule("Finance App Users Export", groups: All(Equals(Department, "Finance")));

        var result = (await ExplainAsync(includeNotConnected: false))!;

        Assert.That(result.NotConnected, Is.Null);
        _metaverse.Verify(r => r.GetMetaverseObjectAttributeValuesAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<int>>()), Times.Never,
            "nothing needs the Metaverse Object's values when no rule is evaluated");
    }

    #region Joined connections

    [Test]
    public async Task GetMetaverseObjectConnectionExplanationsAsync_JoinedConnection_EvaluatesEachRelevantRuleAgainstTheRightObjectAsync()
    {
        var cso = Joined(PayrollId, "Payroll", EmployeeTypeId, "employee");
        _csoValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), ConnectedSystemObject = new ConnectedSystemObject { Id = cso.Id }, AttributeId = PayrollDepartment.Id, StringValue = "Sales" });
        MvoHas(Department, "Finance");
        Rule("Payroll Import", SyncRuleDirection.Import, PayrollId, "Payroll", EmployeeTypeId, "employee", null, ConnectedSystemStatus.Active,
        [
            new SyncRuleScopingCriteriaGroup
            {
                Type = SearchGroupType.All,
                Criteria = [new SyncRuleScopingCriteria { MetaverseAttribute = null, ConnectedSystemAttribute = PayrollDepartment, ComparisonType = SearchComparisonType.Equals, StringValue = "Finance" }]
            }
        ]);
        ExportRule("Payroll Export", PayrollId, "Payroll", EmployeeTypeId, "employee", groups: All(Equals(Department, "Finance")));
        ExportRule("Payroll Contractors Export", PayrollId, "Payroll", EmployeeTypeId, "employee", groups: All(Equals(Department, "Contractors")));

        var result = (await ExplainAsync())!;

        var row = result.Connections.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(row.ConnectedSystemObjectId, Is.EqualTo(cso.Id));
            Assert.That(row.IsSource, Is.True);
            Assert.That(row.IsTarget, Is.True);
            Assert.That(row.Scoping.Select(s => (s.SyncRuleName, s.Outcome)), Is.EqualTo(new[]
            {
                ("Payroll Contractors Export", ScopingRuleOutcome.OutOfScope),
                ("Payroll Export", ScopingRuleOutcome.InScope),
                ("Payroll Import", ScopingRuleOutcome.OutOfScope)
            }), "import rules read the object's values (Sales), export rules the Metaverse Object's (Finance); in rule name order");
            Assert.That(row.Scoping.Select(s => s.EvaluatedAt).Distinct().Single(), Is.EqualTo(result.EvaluatedAt),
                "every evaluation resolves against the one instant the result reports");
            Assert.That(row.Scoping.Single(s => s.SyncRuleName == "Payroll Import").Hint, Is.EqualTo("Fails on dept"));
        }
    }

    [Test]
    public async Task GetMetaverseObjectConnectionExplanationsAsync_ExportRuleForAnotherTypeOnTheSameSystem_ReportsAnObjectTypeConflictAsync()
    {
        Joined(FinanceAppId, "Finance App", AccountTypeId, "account");
        ExportRule("Finance App Users Export");
        ExportRule("Finance App Groups Export", objectTypeId: GroupTypeId, objectTypeName: "group");

        var result = (await ExplainAsync())!;

        var row = result.Connections.Single();
        var conflict = row.Conflicts.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(conflict.SyncRuleName, Is.EqualTo("Finance App Groups Export"));
            Assert.That(conflict.TargetObjectTypeName, Is.EqualTo("group"));
            Assert.That(conflict.ExistingObjectTypeName, Is.EqualTo("account"));
            Assert.That(conflict.Description, Does.Contain("cannot connect it"));
            Assert.That(conflict.Scoping.Outcome, Is.EqualTo(ScopingRuleOutcome.InScope));
            Assert.That(row.Scoping.Select(s => s.SyncRuleName), Is.EqualTo(new[] { "Finance App Users Export" }),
                "the conflicting rule does not explain this connection");
            Assert.That(result.NotConnected, Is.Empty, "the system holds a joined object, so the rule is not a not-connected entry");
        }
    }

    [Test]
    public async Task GetMetaverseObjectConnectionExplanationsAsync_JoinRecordedOnObject_ReportsItAsRecordedWithItsActivityAsync()
    {
        var cso = Joined(FinanceAppId, "Finance App", AccountTypeId, "account", ConnectedSystemObjectJoinType.Joined);
        cso.JoinMethod = ConnectedSystemObjectJoinMethod.ExportMatching;
        cso.JoinSyncRuleId = 7;
        cso.JoinSyncRuleName = "Finance App Users Export";
        var activityId = Guid.NewGuid();
        _history.Add(new JoinHistoryEntry
        {
            ConnectedSystemObjectId = cso.Id, JoinType = ConnectedSystemObjectJoinType.Joined, ActivityId = activityId,
            ActivityCreated = cso.DateJoined!.Value.AddMinutes(-5), ActivityDuration = TimeSpan.FromMinutes(10), RunProfileExecutionItemId = Guid.NewGuid()
        });

        var join = (await ExplainAsync())!.Connections.Single().Join;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(join.Source, Is.EqualTo(JoinRecordSource.Recorded));
            Assert.That(join.Method, Is.EqualTo(ConnectedSystemObjectJoinMethod.ExportMatching));
            Assert.That(join.Description, Does.StartWith("Joined by the Synchronisation Rule \"Finance App Users Export\""));
            Assert.That(join.SyncRuleId, Is.EqualTo(7));
            Assert.That(join.SyncRuleName, Is.EqualTo("Finance App Users Export"));
            Assert.That(join.JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.Joined));
            Assert.That(join.DateJoined, Is.EqualTo(cso.DateJoined));
            Assert.That(join.ActivityId, Is.EqualTo(activityId));
        }
    }

    [Test]
    public async Task GetMetaverseObjectConnectionExplanationsAsync_RuleDeletedSinceJoining_KeepsTheRecordedNameAsync()
    {
        var cso = Joined(FinanceAppId, "Finance App", AccountTypeId, "account", ConnectedSystemObjectJoinType.Projected);
        cso.JoinMethod = ConnectedSystemObjectJoinMethod.Projection;
        cso.JoinSyncRuleName = "HR Users Import";

        var join = (await ExplainAsync())!.Connections.Single().Join;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(join.Source, Is.EqualTo(JoinRecordSource.Recorded));
            Assert.That(join.SyncRuleId, Is.Null);
            Assert.That(join.SyncRuleName, Is.EqualTo("HR Users Import"));
        }
    }

    /// <summary>
    /// A Connected System with no import rule joins on its own Object Matching Rules: recorded, with no rule to name,
    /// and worded as such rather than as "not recorded".
    /// </summary>
    [Test]
    public async Task GetMetaverseObjectConnectionExplanationsAsync_JoinedByConnectedSystemMatching_IsRecordedWithNoRuleAsync()
    {
        var cso = Joined(FinanceAppId, "Finance App", AccountTypeId, "account", ConnectedSystemObjectJoinType.Joined);
        cso.JoinMethod = ConnectedSystemObjectJoinMethod.InboundMatching;

        var join = (await ExplainAsync())!.Connections.Single().Join;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(join.Source, Is.EqualTo(JoinRecordSource.Recorded));
            Assert.That(join.SyncRuleName, Is.Null);
            Assert.That(join.Description, Is.EqualTo("Joined by matching on the Connected System's Object Matching Rules"));
        }
    }

    [Test]
    public async Task GetMetaverseObjectConnectionExplanationsAsync_JoinedBeforeRecordingWithHistory_DerivesTheRuleAsync()
    {
        var cso = Joined(PayrollId, "Payroll", EmployeeTypeId, "employee", ConnectedSystemObjectJoinType.Projected);
        _history.Add(new JoinHistoryEntry
        {
            ConnectedSystemObjectId = cso.Id, JoinType = ConnectedSystemObjectJoinType.Projected, ActivityId = Guid.NewGuid(),
            ActivityCreated = cso.DateJoined!.Value.AddMinutes(-1), ActivityDuration = TimeSpan.FromMinutes(5),
            RunProfileExecutionItemId = Guid.NewGuid(), SyncRuleId = 9, SyncRuleName = "Payroll Import"
        });

        var join = (await ExplainAsync())!.Connections.Single().Join;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(join.Source, Is.EqualTo(JoinRecordSource.Derived));
            Assert.That(join.Method, Is.EqualTo(ConnectedSystemObjectJoinMethod.Projection), "history of a projection evidences a projection");
            Assert.That(join.SyncRuleId, Is.EqualTo(9));
            Assert.That(join.SyncRuleName, Is.EqualTo("Payroll Import"));
        }
    }

    /// <summary>
    /// History from an earlier join of the same object (an Activity that had finished before this join was made) is
    /// not evidence of this join; saying so would guess.
    /// </summary>
    [Test]
    public async Task GetMetaverseObjectConnectionExplanationsAsync_HistoryOnlyFromAnEarlierJoin_IsNotRecordedAsync()
    {
        var cso = Joined(PayrollId, "Payroll", EmployeeTypeId, "employee", ConnectedSystemObjectJoinType.Projected);
        _history.Add(new JoinHistoryEntry
        {
            ConnectedSystemObjectId = cso.Id, JoinType = ConnectedSystemObjectJoinType.Projected, ActivityId = Guid.NewGuid(),
            ActivityCreated = cso.DateJoined!.Value.AddDays(-30), ActivityDuration = TimeSpan.FromMinutes(5),
            RunProfileExecutionItemId = Guid.NewGuid(), SyncRuleId = 9, SyncRuleName = "Payroll Import"
        });

        var join = (await ExplainAsync())!.Connections.Single().Join;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(join.Source, Is.EqualTo(JoinRecordSource.NotRecorded));
            Assert.That(join.Method, Is.Null);
            Assert.That(join.Description, Is.EqualTo("Projected; the Synchronisation Rule was not recorded"));
            Assert.That(join.SyncRuleName, Is.Null);
            Assert.That(join.ActivityId, Is.Null);
        }
    }

    [Test]
    public async Task GetMetaverseObjectConnectionExplanationsAsync_InboundJoinBeforeRecording_LinksTheActivityButNamesNoRuleAsync()
    {
        var cso = Joined(PayrollId, "Payroll", EmployeeTypeId, "employee", ConnectedSystemObjectJoinType.Joined);
        var activityId = Guid.NewGuid();
        _history.Add(new JoinHistoryEntry
        {
            ConnectedSystemObjectId = cso.Id, JoinType = ConnectedSystemObjectJoinType.Joined, ActivityId = activityId,
            ActivityCreated = cso.DateJoined!.Value, RunProfileExecutionItemId = Guid.NewGuid()
        });

        var join = (await ExplainAsync())!.Connections.Single().Join;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(join.Source, Is.EqualTo(JoinRecordSource.NotRecorded), "an inbound join's history names no rule");
            Assert.That(join.Method, Is.EqualTo(ConnectedSystemObjectJoinMethod.InboundMatching), "but it does say how the object joined");
            Assert.That(join.ActivityId, Is.EqualTo(activityId));
        }
    }

    [Test]
    public async Task GetMetaverseObjectConnectionExplanationsAsync_ProvisioningPendingBeforeRecording_DerivesTheRuleFromThePendingExportAsync()
    {
        var rule = ExportRule("Finance App Users Export");
        var cso = Joined(FinanceAppId, "Finance App", AccountTypeId, "account", ConnectedSystemObjectJoinType.Provisioned,
            ConnectedSystemObjectStatus.PendingProvisioning);
        var queuedBy = Guid.NewGuid();
        _pendingExports[cso.Id] = new PendingExport
        {
            Id = Guid.NewGuid(), ConnectedSystemObjectId = cso.Id, ChangeType = PendingExportChangeType.Create,
            ProvisioningSyncRuleId = rule.Id, QueuedByRunProfileExecutionItemId = queuedBy
        };

        var result = (await ExplainAsync())!;

        var join = result.Connections.Single().Join;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(join.Source, Is.EqualTo(JoinRecordSource.Derived));
            Assert.That(join.Method, Is.EqualTo(ConnectedSystemObjectJoinMethod.Provisioning));
            Assert.That(join.SyncRuleName, Is.EqualTo("Finance App Users Export"));
            Assert.That(result.NotConnected, Is.Empty, "provisioning under way is a joined row, never not connected");
        }
        _connectedSystems.Verify(r => r.GetJoinHistoryAsync(It.IsAny<IReadOnlyCollection<Guid>>(),
            It.Is<IReadOnlyDictionary<Guid, Guid>>(d => d.Count == 1 && d[cso.Id] == queuedBy)));
    }

    #endregion

    #region Not connected

    [Test]
    public async Task GetMetaverseObjectConnectionExplanationsAsync_OutOfScope_IsNotInScopeWithBulletsAndSummaryAsync()
    {
        MvoHas(Department, "Engineering");
        ExportRule("Finance App Users Export", groups: All(Equals(Department, "Finance")));

        var entry = (await ExplainAsync())!.NotConnected!.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entry.Reason, Is.EqualTo(NotConnectedReason.NotInScope));
            Assert.That(entry.Hint, Is.EqualTo("Fails on Department"));
            Assert.That(entry.BulletsTitle, Is.EqualTo("To come into scope"));
            Assert.That(entry.Bullets.Single().PlainText, Is.EqualTo("Department must equal \"Finance\" (currently \"Engineering\")"));
            Assert.That(entry.Summary, Does.StartWith("Jane Smith is not provisioned to Finance App.\n"));
            Assert.That(entry.ConnectedSystemName, Is.EqualTo("Finance App"));
            Assert.That(entry.SyncRuleName, Is.EqualTo("Finance App Users Export"));
            Assert.That(entry.ObjectTypeName, Is.EqualTo("account"));
            Assert.That(entry.Scoping.Outcome, Is.EqualTo(ScopingRuleOutcome.OutOfScope));
        }
    }

    [TestCase(true, NotConnectedReason.NotYetProvisioned)]
    [TestCase(false, NotConnectedReason.ProvisioningDisabled)]
    [TestCase(null, NotConnectedReason.ProvisioningDisabled)]
    public async Task GetMetaverseObjectConnectionExplanationsAsync_InScope_ReasonFollowsWhetherTheRuleProvisionsAsync(bool? provision, NotConnectedReason expected)
    {
        ExportRule("Finance App Users Export", provision: provision);

        var entry = (await ExplainAsync())!.NotConnected!.Single();

        Assert.That(entry.Reason, Is.EqualTo(expected));
    }

    /// <summary>
    /// Whether the Metaverse Object is marked for an export scope review (#1925) decides what a Not yet provisioned entry
    /// says happens next: the next synchronisation stages it, or nothing does until its values or the rule change.
    /// </summary>
    [TestCase(true, "In scope; staged at the next synchronisation")]
    [TestCase(false, "In scope; nothing staged yet")]
    public async Task GetMetaverseObjectConnectionExplanationsAsync_NotYetProvisioned_SaysWhetherAScopeReviewIsPendingAsync(bool scopeReviewPending,
        string expectedHint)
    {
        _header.ScopeReviewPending = scopeReviewPending;
        ExportRule("Finance App Users Export", provision: true);

        var entry = (await ExplainAsync())!.NotConnected!.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entry.Reason, Is.EqualTo(NotConnectedReason.NotYetProvisioned));
            Assert.That(entry.Hint, Is.EqualTo(expectedHint));
        }
    }

    [Test]
    public async Task GetMetaverseObjectConnectionExplanationsAsync_InvalidCriterionReached_IsRuleMisconfiguredAsync()
    {
        ExportRule("Finance App Users Export", groups: All(new SyncRuleScopingCriteria
        {
            Id = 99, MetaverseAttribute = StartDate, ComparisonType = SearchComparisonType.StartsWith, StringValue = "2026"
        }));

        var entry = (await ExplainAsync())!.NotConnected!.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entry.Reason, Is.EqualTo(NotConnectedReason.RuleMisconfigured));
            Assert.That(entry.Hint, Is.EqualTo("Invalid criterion on Start Date"));
        }
    }

    [Test]
    public async Task GetMetaverseObjectConnectionExplanationsAsync_DisabledAndDeletingSystems_DisabledIncludedWithQualifierDeletingExcludedAsync()
    {
        ExportRule("Payroll Export", PayrollId, "Payroll", EmployeeTypeId, "employee", status: ConnectedSystemStatus.Disabled);
        ExportRule("Archive Export", 30, "Archive", 300, "record", status: ConnectedSystemStatus.Deleting);

        var entry = (await ExplainAsync())!.NotConnected!.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entry.ConnectedSystemName, Is.EqualTo("Payroll"));
            Assert.That(entry.ConnectedSystemStatus, Is.EqualTo(ConnectedSystemStatus.Disabled));
            Assert.That(entry.Hint, Does.EndWith("Connected System disabled"));
        }
    }

    [Test]
    public async Task GetMetaverseObjectConnectionExplanationsAsync_SeveralRules_OneEntryEachBySystemThenRuleAsync()
    {
        ExportRule("Payroll Export", PayrollId, "Payroll", EmployeeTypeId, "employee");
        ExportRule("Finance App Users Export");
        ExportRule("Finance App Admins Export");
        Rule("Payroll Import", SyncRuleDirection.Import, PayrollId, "Payroll", EmployeeTypeId, "employee", null, ConnectedSystemStatus.Active, []);

        var entries = (await ExplainAsync())!.NotConnected!;

        Assert.That(entries.Select(e => (e.ConnectedSystemName, e.SyncRuleName)), Is.EqualTo(new[]
        {
            ("Finance App", "Finance App Admins Export"),
            ("Finance App", "Finance App Users Export"),
            ("Payroll", "Payroll Export")
        }), "export rules only; two rules on one system are two entries");
    }

    #endregion

    #region Reads

    /// <summary>
    /// The page's cost must not grow with the number of rules, criteria or connections: each read happens once.
    /// </summary>
    [Test]
    public async Task GetMetaverseObjectConnectionExplanationsAsync_ManyRulesAndConnections_EachReadHappensOnceAsync()
    {
        MvoHas(Department, "Finance");
        MvoHas(CostCentre, "FIN1");
        var payroll = Joined(PayrollId, "Payroll", EmployeeTypeId, "employee");
        Joined(FinanceAppId, "Finance App", AccountTypeId, "account");
        _csoValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), ConnectedSystemObject = new ConnectedSystemObject { Id = payroll.Id }, AttributeId = PayrollDepartment.Id, StringValue = "Finance" });
        for (var i = 0; i < 5; i++)
        {
            ExportRule($"Finance App Export {i}", groups: All(Equals(Department, "Finance"), Equals(CostCentre, "FIN1")));
            ExportRule($"Elsewhere Export {i}", 40 + i, $"Elsewhere {i}", 400 + i, "user", groups: All(Equals(Department, "Finance")));
            Rule($"Payroll Import {i}", SyncRuleDirection.Import, PayrollId, "Payroll", EmployeeTypeId, "employee", null, ConnectedSystemStatus.Active,
            [
                new SyncRuleScopingCriteriaGroup
                {
                    Type = SearchGroupType.All,
                    Criteria = [new SyncRuleScopingCriteria { ConnectedSystemAttribute = PayrollDepartment, ComparisonType = SearchComparisonType.Equals, StringValue = "Finance" }]
                }
            ]);
        }

        await ExplainAsync();

        _metaverse.Verify(r => r.GetMetaverseObjectHeaderAsync(_mvoId), Times.Once);
        _connectedSystems.Verify(r => r.GetConnectedSystemObjectsCoreByMetaverseObjectIdAsync(_mvoId), Times.Once);
        _connectedSystems.Verify(r => r.GetSyncRulesForScopingExplanationAsync(PersonTypeId), Times.Once);
        _connectedSystems.Verify(r => r.GetPendingExportsLightweightByConnectedSystemObjectIdsAsync(It.IsAny<IEnumerable<Guid>>()), Times.Once);
        _connectedSystems.Verify(r => r.GetJoinHistoryAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<IReadOnlyDictionary<Guid, Guid>>()), Times.Once);
        _metaverse.Verify(r => r.GetMetaverseObjectAttributeValuesAsync(_mvoId,
            It.Is<IReadOnlyCollection<int>>(ids => ids.Count == 2 && ids.Contains(Department.Id) && ids.Contains(CostCentre.Id))), Times.Once);
        _connectedSystems.Verify(r => r.GetConnectedSystemObjectAttributeValuesAsync(
            It.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 1 && ids.Contains(payroll.Id)),
            It.Is<IReadOnlyCollection<int>>(ids => ids.Count == 1 && ids.Contains(PayrollDepartment.Id))), Times.Once,
            "only objects that import rules read, and only the attributes their criteria compare");
    }

    #endregion
}
