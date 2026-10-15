// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Models.Sync;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Synchronisation;

/// <summary>
/// Temporal Scope Reconciliation for an expression compared against a relative date (#2054). Nothing stored can narrow
/// such a criterion to the objects whose date the clock crossed, so the rule's whole object type is evaluated, in
/// batches rather than loaded at once, and the sweep says how many objects that cost. An object whose scope cannot be
/// decided is left as it is and counted, never flagged on a guess.
/// </summary>
[TestFixture]
public class ScopeReconciliationExpressionTests
{
    private static readonly DateTime Now = new(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
    private const int SystemId = 3;

    private Mock<IRepository> _repo = null!;
    private Mock<IConnectedSystemRepository> _csRepo = null!;
    private Mock<IMetaverseRepository> _mvRepo = null!;
    private JIM.InMemoryData.SyncRepository _syncRepo = null!;
    private List<SyncRule> _rules = null!;
    private List<ConnectedSystemObject> _csos = null!;
    private List<List<Guid>> _csoBatches = null!;
    private List<Guid> _flaggedCsos = null!;

    private readonly ConnectedSystemObjectTypeAttribute _start = new() { Id = 11, Name = "start", Type = AttributeDataType.Text };
    private ConnectedSystemObjectType _person = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();
        _person = new ConnectedSystemObjectType { Id = 7, Name = "person", Attributes = [_start] };
        _repo = new Mock<IRepository>();
        _csRepo = new Mock<IConnectedSystemRepository>();
        _mvRepo = new Mock<IMetaverseRepository>();
        _syncRepo = new JIM.InMemoryData.SyncRepository();
        _repo.Setup(r => r.ConnectedSystems).Returns(_csRepo.Object);
        _repo.Setup(r => r.Metaverse).Returns(_mvRepo.Object);

        _rules = [];
        _csos = [];
        _csoBatches = [];
        _flaggedCsos = [];
        _csRepo.Setup(r => r.GetSyncRulesAsync(It.IsAny<bool>())).ReturnsAsync(() => _rules);
        _csRepo.Setup(r => r.GetLiveConnectedSystemObjectIdsOfTypeAsync(SystemId, _person.Id))
            .ReturnsAsync(() => _csos.Select(c => c.Id).ToList());
        _csRepo.Setup(r => r.GetConnectedSystemObjectsByIdsNoTrackingAsync(SystemId, It.IsAny<IEnumerable<Guid>>()))
            .ReturnsAsync((int _, IEnumerable<Guid> ids) =>
            {
                var batch = ids.ToList();
                _csoBatches.Add(batch);
                return _csos.Where(c => batch.Contains(c.Id)).ToList();
            });
        _csRepo.Setup(r => r.MarkConnectedSystemObjectsScopeEvaluatedAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<IReadOnlyCollection<Guid>>(), Now))
            .Callback((IReadOnlyCollection<Guid> _, IReadOnlyCollection<Guid> flagged, DateTime _) => _flaggedCsos.AddRange(flagged))
            .Returns(Task.CompletedTask);
    }

    private JimApplication BuildApplication() => new(_repo.Object, syncRepository: _syncRepo);

    [Test]
    public async Task ReconcileAsync_ExpressionAgainstARelativeDate_EvaluatesTheWholeTypeAndFlagsWhatFlippedAsync()
    {
        // In scope while the start date (held as text) is at most 14 days away.
        _rules.Add(ImportRule(RelativeExpression()));
        var joinedButNowOut = Cso("2027-06-01", joined: true);   // far off: leaves scope
        var unjoinedButNowIn = Cso("2026-10-20", joined: false); // six days away: enters scope
        Cso("2026-10-18", joined: true);                         // in scope and joined: no change

        var result = await BuildApplication().ScopeReconciliation.ReconcileAsync(Now.AddHours(-1), Now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.RulesEvaluated, Is.EqualTo(1));
            Assert.That(result.FullPassObjectsEvaluated, Is.EqualTo(3));
            Assert.That(_flaggedCsos, Is.EquivalentTo(new[] { joinedButNowOut.Id, unjoinedButNowIn.Id }));
        }
        _csRepo.Verify(r => r.GetConnectedSystemObjectIdsByDateAttributeRangeAsync(It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<DateTime>()), Times.Never,
            "no stored date can narrow an expression's computed one");
    }

    [Test]
    public async Task ReconcileAsync_FullPass_LoadsTheTypeInBatchesAsync()
    {
        _rules.Add(ImportRule(RelativeExpression()));
        for (var i = 0; i < ScopeReconciliationServer.FullPassBatchSize + 1; i++)
            Cso("2026-10-18", joined: true);

        var result = await BuildApplication().ScopeReconciliation.ReconcileAsync(Now.AddHours(-1), Now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_csoBatches, Has.Count.EqualTo(2));
            Assert.That(_csoBatches.Max(b => b.Count), Is.LessThanOrEqualTo(ScopeReconciliationServer.FullPassBatchSize));
            Assert.That(result.FullPassObjectsEvaluated, Is.EqualTo(ScopeReconciliationServer.FullPassBatchSize + 1));
        }
    }

    [Test]
    public async Task ReconcileAsync_ObjectWhoseScopeCannotBeDecided_IsLeftAloneCountedAndTheSweepContinuesAsync()
    {
        _rules.Add(ImportRule(RelativeExpression()));
        var undecidable = Cso("not a date", joined: true);
        var flipped = Cso("2026-10-20", joined: false);

        var result = await BuildApplication().ScopeReconciliation.ReconcileAsync(Now.AddHours(-1), Now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.UndecidableObjects, Is.EqualTo(1));
            Assert.That(_flaggedCsos, Does.Not.Contain(undecidable.Id));
            Assert.That(_flaggedCsos, Does.Contain(flipped.Id));
        }
    }

    [Test]
    public void Describe_FullPass_SaysHowManyObjectsWereEvaluatedAndWhy()
    {
        var result = new ScopeReconciliationResult { RulesEvaluated = 1, FullPassRules = 1, FullPassObjectsEvaluated = 12000, UndecidableObjects = 2 };

        var message = result.Describe();

        Assert.That(message, Does.Contain("12,000").And.Contain("expression").And.Contain("2 object"));
    }

    [Test]
    public void Describe_OnlyWindowedRules_IsNull()
    {
        Assert.That(new ScopeReconciliationResult { RulesEvaluated = 2, InboundCandidatesEvaluated = 5 }.Describe(), Is.Null);
    }

    private static SyncRuleScopingCriteria RelativeExpression() => new()
    {
        Expression = "ParseDate(cs[\"start\"], \"yyyy-MM-dd\")",
        CompareAs = AttributeDataType.DateTime,
        ComparisonType = SearchComparisonType.LessThanOrEquals,
        ValueMode = DateCriteriaValueMode.Relative,
        RelativeCount = 14,
        RelativeUnit = RelativeDateUnit.Days,
        RelativeDirection = RelativeDateDirection.FromNow
    };

    private SyncRule ImportRule(SyncRuleScopingCriteria criterion)
    {
        var rule = new SyncRule
        {
            Id = 1, Name = "HR Import", Enabled = true, Direction = SyncRuleDirection.Import,
            ConnectedSystemId = SystemId, ConnectedSystemObjectType = _person, ConnectedSystemObjectTypeId = _person.Id
        };
        rule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All, Criteria = [criterion] });
        return rule;
    }

    private ConnectedSystemObject Cso(string start, bool joined)
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(), ConnectedSystemId = SystemId, Type = _person, TypeId = _person.Id,
            MetaverseObjectId = joined ? Guid.NewGuid() : null
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Attribute = _start, AttributeId = _start.Id, StringValue = start, ConnectedSystemObject = cso });
        _csos.Add(cso);
        return cso;
    }
}
