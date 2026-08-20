// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers.Preview;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Exceptions;
using JIM.Models.Logic;
using JIM.Models.Preview;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;
using SyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// The Object Matching adapter (#1457): what changing the rules that decide which Metaverse Object an account joins
/// to would do.
///
/// The failures worth testing are the ones an administrator could not detect afterwards. A matching change that
/// merges an account into the wrong identity does not fail: it succeeds, quietly, and takes every attribute the
/// account contributes with it. So the adapter has to say which objects move, and it has to be equally clear about
/// the objects it cannot move: an account already joined to a Metaverse Object never re-runs matching, so a
/// matching edit cannot touch it, and a count that included the joined population would be a confident number
/// about a change that does nothing to them.
/// </summary>
[TestFixture]
public class ObjectMatchingPreviewAdapterTests
{
    private const int SystemId = 5;
    private const int CsoTypeId = 9;
    private const int MvoTypeId = 3;
    private const int EmployeeIdAttributeId = 101;
    private const int MailAttributeId = 102;
    private const int CreatedAttributeId = 103;
    private const int EmployeeIdMetaverseAttributeId = 201;
    private const int MailMetaverseAttributeId = 202;
    private const int ImportRuleId = 42;

    private Mock<IRepository> _repo = null!;
    private Mock<IConnectedSystemRepository> _connectedSystemRepo = null!;
    private Mock<IMetaverseRepository> _metaverseRepo = null!;
    private JimApplication _jim = null!;
    private ObjectMatchingPreviewAdapter _adapter = null!;

    private ConnectedSystem _connectedSystem = null!;
    private ConnectedSystemObjectType _csoType = null!;
    private MetaverseObjectType _mvoType = null!;
    private SyncRule _importRule = null!;
    private List<SyncRule> _syncRules = null!;
    private List<ConnectedSystemObject> _csos = null!;

    private SyncRepository _syncRepo = null!;
    private MetaverseObject _alice = null!;
    private MetaverseObject _bob = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _repo = new Mock<IRepository>();
        _connectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _metaverseRepo = new Mock<IMetaverseRepository>();
        _repo.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepo.Object);
        _repo.Setup(r => r.Metaverse).Returns(_metaverseRepo.Object);

        _csoType = new ConnectedSystemObjectType
        {
            Id = CsoTypeId,
            Name = "User",
            Attributes =
            [
                new ConnectedSystemObjectTypeAttribute { Id = EmployeeIdAttributeId, Name = "employeeID", Type = AttributeDataType.Text },
                new ConnectedSystemObjectTypeAttribute { Id = MailAttributeId, Name = "mail", Type = AttributeDataType.Text },
                new ConnectedSystemObjectTypeAttribute { Id = CreatedAttributeId, Name = "whenCreated", Type = AttributeDataType.DateTime }
            ],
            ObjectMatchingRules = []
        };

        _mvoType = new MetaverseObjectType
        {
            Id = MvoTypeId,
            Name = "Person",
            Attributes =
            [
                new MetaverseAttribute { Id = EmployeeIdMetaverseAttributeId, Name = "Employee ID", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued },
                new MetaverseAttribute { Id = MailMetaverseAttributeId, Name = "Email", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued }
            ]
        };

        _connectedSystem = new ConnectedSystem
        {
            Id = SystemId,
            Name = "HR",
            ObjectMatchingRuleMode = ObjectMatchingRuleMode.ConnectedSystem
        };

        _importRule = new SyncRule
        {
            Id = ImportRuleId,
            Name = "HR Import",
            ConnectedSystemId = SystemId,
            ConnectedSystemObjectTypeId = CsoTypeId,
            ConnectedSystemObjectType = _csoType,
            MetaverseObjectTypeId = MvoTypeId,
            MetaverseObjectType = _mvoType,
            Direction = SyncRuleDirection.Import,
            Enabled = true
        };
        _syncRules = [_importRule];

        // Real Metaverse Objects put to the real matching implementation, rather than a stub answering by rule.
        // The adapter's whole design is that it asks the matching engine instead of reimplementing it, so a test
        // that stubbed the engine would be testing nothing but its own arithmetic.
        _alice = MetaverseObjectWith("Alice Smith", employeeId: "E1", email: "alice.smith@example.com");
        _bob = MetaverseObjectWith("Bob Jones", employeeId: "E9", email: "alice@example.com");
        _csos = [];

        _syncRepo = new SyncRepository();
        _syncRepo.SeedMetaverseObject(_alice);
        _syncRepo.SeedMetaverseObject(_bob);

        _connectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(SystemId, It.IsAny<bool>())).ReturnsAsync(() => _connectedSystem);
        _connectedSystemRepo.Setup(r => r.GetObjectTypesAsync(SystemId)).ReturnsAsync(() => [_csoType]);
        _connectedSystemRepo.Setup(r => r.GetSyncRulesAsync(SystemId, It.IsAny<bool>(), It.IsAny<bool>())).ReturnsAsync(() => _syncRules);
        // The population query is what excludes the joined and the obsolete, exactly as the real one does; the
        // adapter's own guards over it are belt and braces, and the database fixture proves the SQL.
        _connectedSystemRepo.Setup(r => r.GetUnjoinedConnectedSystemObjectIdsOfTypeAsync(SystemId, CsoTypeId))
            .ReturnsAsync(() => [.. Unjoined().Select(cso => cso.Id)]);
        _connectedSystemRepo.Setup(r => r.GetUnjoinedConnectedSystemObjectCountOfTypeAsync(SystemId, CsoTypeId))
            .ReturnsAsync(() => Unjoined().Count());
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemObjectsByIdsNoTrackingAsync(SystemId, It.IsAny<IEnumerable<Guid>>()))
            .ReturnsAsync((int _, IEnumerable<Guid> ids) => [.. _csos.Where(cso => ids.Contains(cso.Id))]);
        _metaverseRepo.Setup(r => r.GetMetaverseObjectTypesAsync(It.IsAny<bool>())).ReturnsAsync(() => [_mvoType]);

        _jim = new JimApplication(_repo.Object, syncRepository: _syncRepo);
        _adapter = new ObjectMatchingPreviewAdapter(_jim);
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    #region contract

    [Test]
    public void Surface_IsObjectMatching()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_adapter.Surface, Is.EqualTo(ConfigurationChangePreviewSurface.ObjectMatching));
            Assert.That(_adapter.ProducesDeltas, Is.True);
            Assert.That(_adapter.ProposalType, Is.EqualTo(typeof(ObjectMatchingProposal)));
        }
    }

    #endregion

    #region validation

    [Test]
    public async Task ValidateAsync_ProposalMatchesStoredRules_ReportsNoChange()
    {
        _csoType.ObjectMatchingRules = [StoredRule()];

        var findings = await _adapter.ValidateAsync(Context(CurrentProposal()));

        Assert.That(findings.Select(f => f.Message),
            Has.Some.Contains("already"), "a proposal identical to the stored rules must say so rather than count nothing silently");
    }

    [Test]
    public async Task ValidateAsync_RuleWithMoreThanOneSource_IsBlocking()
    {
        _csoType.ObjectMatchingRules = [StoredRule()];
        var proposal = ProposalWith(RuleProposal() with
        {
            Sources =
            [
                new ObjectMatchingRuleSourceProposal(0, EmployeeIdAttributeId),
                new ObjectMatchingRuleSourceProposal(1, MailAttributeId)
            ]
        });

        var findings = await _adapter.ValidateAsync(Context(proposal));

        Assert.That(findings.Where(f => f.Severity == PreviewValidationSeverity.Blocking).Select(f => f.Message),
            Has.Some.Contains("more than one source"));
    }

    [Test]
    public async Task ValidateAsync_ExpressionSource_IsBlocking()
    {
        _csoType.ObjectMatchingRules = [StoredRule()];
        var proposal = ProposalWith(RuleProposal() with
        {
            Sources = [new ObjectMatchingRuleSourceProposal(0, null, "Lower(cs[\"mail\"])")]
        });

        var findings = await _adapter.ValidateAsync(Context(proposal));

        Assert.That(findings.Where(f => f.Severity == PreviewValidationSeverity.Blocking).Select(f => f.Message),
            Has.Some.Contains("expression"));
    }

    [Test]
    public async Task ValidateAsync_UnsupportedSourceAttributeType_IsBlocking()
    {
        _csoType.ObjectMatchingRules = [StoredRule()];
        var proposal = ProposalWith(RuleProposal() with
        {
            Sources = [new ObjectMatchingRuleSourceProposal(0, CreatedAttributeId)]
        });

        var findings = await _adapter.ValidateAsync(Context(proposal));

        Assert.That(findings.Where(f => f.Severity == PreviewValidationSeverity.Blocking).Select(f => f.Message),
            Has.Some.Contains("whenCreated"));
    }

    [Test]
    public async Task ValidateAsync_RuleWithNoTargetMetaverseAttribute_IsBlocking()
    {
        _csoType.ObjectMatchingRules = [StoredRule()];
        var proposal = ProposalWith(RuleProposal() with { TargetMetaverseAttributeId = null });

        var findings = await _adapter.ValidateAsync(Context(proposal));

        Assert.That(findings.Where(f => f.Severity == PreviewValidationSeverity.Blocking).Select(f => f.Message),
            Has.Some.Contains("Metaverse Attribute"));
    }

    [Test]
    public async Task ValidateAsync_EveryRuleRemoved_WarnsThatNothingWouldEverJoin()
    {
        _csoType.ObjectMatchingRules = [StoredRule()];
        var proposal = new ObjectMatchingProposal(ObjectMatchingRuleMode.ConnectedSystem, []);

        var findings = await _adapter.ValidateAsync(Context(proposal));

        Assert.That(findings.Where(f => f.Severity == PreviewValidationSeverity.Warning).Select(f => f.Message),
            Has.Some.Contains("project"));
    }

    [Test]
    public async Task ValidateAsync_AlwaysStatesThatJoinedObjectsCannotMove()
    {
        // The most important sentence the preview says. Matching runs only for objects with no Metaverse Object, so
        // an administrator fearing that a rule edit will re-home their existing joins needs telling that it cannot.
        _csoType.ObjectMatchingRules = [StoredRule()];
        var proposal = ProposalWith(RuleProposal() with { TargetMetaverseAttributeId = MailMetaverseAttributeId });

        var findings = await _adapter.ValidateAsync(Context(proposal));

        Assert.That(findings.Select(f => f.Message), Has.Some.Contains("already joined"));
    }

    [Test]
    public async Task ValidateAsync_ModeSwitch_IsWarned()
    {
        _csoType.ObjectMatchingRules = [StoredRule()];
        var proposal = new ObjectMatchingProposal(ObjectMatchingRuleMode.SyncRule, CurrentProposal().Rules);

        var findings = await _adapter.ValidateAsync(Context(proposal));

        Assert.That(findings.Where(f => f.Severity == PreviewValidationSeverity.Warning).Select(f => f.Message),
            Has.Some.Contains("Advanced"));
    }

    #endregion

    #region evaluation

    [Test]
    public async Task EvaluateDeltasAsync_NoChange_ReadsNoObjects()
    {
        _csoType.ObjectMatchingRules = [StoredRule()];
        _csos = [UnjoinedCso("E1", "alice@example.com")];

        var deltas = await _adapter.EvaluateDeltasAsync(Context(CurrentProposal()), CancellationToken.None).ToListAsync();

        Assert.That(deltas, Is.Empty);
    }

    [Test]
    public async Task EvaluateDeltasAsync_ProposalMatchesADifferentMetaverseObject_ReportsTheSwap()
    {
        // employeeID E1 matches Alice on Employee ID today; mail alice@example.com matches Bob on Email.
        _csoType.ObjectMatchingRules = [StoredRule()];
        _csos = [UnjoinedCso("E1", "alice@example.com")];

        var proposal = ProposalWith(MailRuleProposal());
        var deltas = await _adapter.EvaluateDeltasAsync(Context(proposal), CancellationToken.None).ToListAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deltas, Has.Count.EqualTo(1));
            Assert.That(deltas[0].TransitionType,
                Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.WouldJoinDifferentMetaverseObject));
            Assert.That(deltas[0].MetaverseObjectId, Is.EqualTo(_bob.Id), "the delta names the identity the object would end up on");
            Assert.That(deltas[0].OldValue, Is.EqualTo("Alice Smith"));
            Assert.That(deltas[0].NewValue, Is.EqualTo("Bob Jones"));
        }
    }

    [Test]
    public async Task EvaluateDeltasAsync_ProposalMatchesWhereNothingDidBefore_ReportsJoinInsteadOfProject()
    {
        // No Metaverse Object carries employee id E7, so the object projects today; its mail matches Bob.
        _csoType.ObjectMatchingRules = [StoredRule()];
        _csos = [UnjoinedCso("E7", "alice@example.com")];

        var proposal = ProposalWith(MailRuleProposal());
        var deltas = await _adapter.EvaluateDeltasAsync(Context(proposal), CancellationToken.None).ToListAsync();

        Assert.That(deltas.Select(d => d.TransitionType),
            Is.EqualTo(new[] { ActivityRunProfileExecutionItemSyncOutcomeType.WouldJoinInsteadOfProject }));
    }

    [Test]
    public async Task EvaluateDeltasAsync_ProposalStopsMatching_ReportsProjectInsteadOfJoin()
    {
        // E1 matches Alice today; no Metaverse Object carries this mail, so the proposal matches nothing and the
        // next synchronisation would project a second identity beside the one it should have joined.
        _csoType.ObjectMatchingRules = [StoredRule()];
        _csos = [UnjoinedCso("E1", "nobody@example.com")];

        var proposal = ProposalWith(MailRuleProposal());
        var deltas = await _adapter.EvaluateDeltasAsync(Context(proposal), CancellationToken.None).ToListAsync();

        Assert.That(deltas.Select(d => d.TransitionType),
            Is.EqualTo(new[] { ActivityRunProfileExecutionItemSyncOutcomeType.WouldProjectInsteadOfJoin }));
    }

    [Test]
    public async Task EvaluateDeltasAsync_ProposalMatchesMoreThanOneMetaverseObject_ReportsAmbiguity()
    {
        // A third identity sharing Bob's email makes the proposed rule ambiguous for this object alone: exactly the
        // failure a rule that looks unique in the editor hides until the population is evaluated.
        _syncRepo.SeedMetaverseObject(MetaverseObjectWith("Bobby Jones", employeeId: "E8", email: "alice@example.com"));
        _csoType.ObjectMatchingRules = [StoredRule()];
        _csos = [UnjoinedCso("E7", "alice@example.com")];

        var proposal = ProposalWith(MailRuleProposal());
        var deltas = await _adapter.EvaluateDeltasAsync(Context(proposal), CancellationToken.None).ToListAsync();

        Assert.That(deltas.Select(d => d.TransitionType),
            Is.EqualTo(new[] { ActivityRunProfileExecutionItemSyncOutcomeType.WouldMatchAmbiguously }));
    }

    [Test]
    public async Task EvaluateDeltasAsync_AlreadyJoinedObject_IsNeverEvaluated()
    {
        // The honest negative. A joined object never re-runs matching, so a matching change cannot move it, and
        // reporting one would send an administrator looking for a re-home that will not happen.
        _csoType.ObjectMatchingRules = [StoredRule()];
        var joined = UnjoinedCso("E1", "alice@example.com");
        joined.MetaverseObjectId = _alice.Id;
        joined.MetaverseObject = _alice;
        _csos = [joined];

        var proposal = ProposalWith(MailRuleProposal());
        var deltas = await _adapter.EvaluateDeltasAsync(Context(proposal), CancellationToken.None).ToListAsync();

        Assert.That(deltas, Is.Empty);
    }

    [Test]
    public async Task EvaluateDeltasAsync_ObsoleteObject_IsNeverEvaluated()
    {
        _csoType.ObjectMatchingRules = [StoredRule()];
        var obsolete = UnjoinedCso("E1", "alice@example.com");
        obsolete.Status = ConnectedSystemObjectStatus.Obsolete;
        _csos = [obsolete];

        var proposal = ProposalWith(MailRuleProposal());
        var deltas = await _adapter.EvaluateDeltasAsync(Context(proposal), CancellationToken.None).ToListAsync();

        Assert.That(deltas, Is.Empty);
    }

    [Test]
    public async Task EvaluateDeltasAsync_ObjectOutOfScopeOfEveryImportRule_IsNeverEvaluated()
    {
        // Matching runs only for objects a synchronisation would process, and an object out of scope of every
        // import rule carrying criteria never reaches the join step at all.
        _csoType.ObjectMatchingRules = [StoredRule()];
        _importRule.ObjectScopingCriteriaGroups =
        [
            new SyncRuleScopingCriteriaGroup
            {
                Type = JIM.Models.Search.SearchGroupType.All,
                Criteria =
                [
                    new SyncRuleScopingCriteria
                    {
                        ConnectedSystemAttribute = _csoType.Attributes.First(a => a.Id == MailAttributeId),
                        ConnectedSystemAttributeId = MailAttributeId,
                        ComparisonType = JIM.Models.Search.SearchComparisonType.Equals,
                        StringValue = "never-matches@example.com"
                    }
                ]
            }
        ];
        _csos = [UnjoinedCso("E1", "alice@example.com")];

        var proposal = ProposalWith(MailRuleProposal());
        var deltas = await _adapter.EvaluateDeltasAsync(Context(proposal), CancellationToken.None).ToListAsync();

        Assert.That(deltas, Is.Empty);
    }

    [Test]
    public async Task CountImpactAsync_CountsEachTransitionSeparately()
    {
        _csoType.ObjectMatchingRules = [StoredRule()];
        _csos = [UnjoinedCso("E1", "alice@example.com"), UnjoinedCso("E7", "alice@example.com")];

        var proposal = ProposalWith(MailRuleProposal());
        var counts = await _adapter.CountImpactAsync(Context(proposal));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(counts.Single(c => c.TransitionType == ActivityRunProfileExecutionItemSyncOutcomeType.WouldJoinDifferentMetaverseObject).ObjectCount,
                Is.EqualTo(1));
            Assert.That(counts.Single(c => c.TransitionType == ActivityRunProfileExecutionItemSyncOutcomeType.WouldJoinInsteadOfProject).ObjectCount,
                Is.EqualTo(1));
        }
    }

    [Test]
    public async Task EstimateCostAsync_NoChange_CostsNothing()
    {
        _csoType.ObjectMatchingRules = [StoredRule()];
        _csos = [UnjoinedCso("E1", "alice@example.com")];

        var estimate = await _adapter.EstimateCostAsync(Context(CurrentProposal()));

        Assert.That(estimate.AffectedObjects, Is.Zero);
    }

    #endregion

    #region helpers

    private PreviewContext Context(ObjectMatchingProposal proposal) => new()
    {
        Surface = ConfigurationChangePreviewSurface.ObjectMatching,
        ActivityId = Guid.NewGuid(),
        TargetId = SystemId,
        ProposedConfiguration = proposal
    };

    private ObjectMatchingRule StoredRule() => new()
    {
        Id = 1,
        Order = 0,
        ConnectedSystemObjectTypeId = CsoTypeId,
        MetaverseObjectTypeId = MvoTypeId,
        TargetMetaverseAttributeId = EmployeeIdMetaverseAttributeId,
        Sources = [new ObjectMatchingRuleSource { Id = 1, Order = 0, ConnectedSystemAttributeId = EmployeeIdAttributeId }]
    };

    private ObjectMatchingProposal CurrentProposal() =>
        ObjectMatchingProposal.FromCurrentConfiguration(_connectedSystem, [_csoType], _syncRules);

    private ObjectMatchingRuleProposal RuleProposal() => ObjectMatchingRuleProposal.FromRule(StoredRule());

    /// <summary>The same rule matching on mail against Email instead of employeeID against Employee ID.</summary>
    private ObjectMatchingRuleProposal MailRuleProposal() => RuleProposal() with
    {
        TargetMetaverseAttributeId = MailMetaverseAttributeId,
        Sources = [new ObjectMatchingRuleSourceProposal(0, MailAttributeId)]
    };

    private MetaverseObject MetaverseObjectWith(string displayName, string employeeId, string email) => new()
    {
        Id = Guid.NewGuid(),
        Type = _mvoType,
        CachedDisplayName = displayName,
        AttributeValues =
        [
            new MetaverseObjectAttributeValue
            {
                AttributeId = EmployeeIdMetaverseAttributeId,
                Attribute = _mvoType.Attributes.First(a => a.Id == EmployeeIdMetaverseAttributeId),
                StringValue = employeeId
            },
            new MetaverseObjectAttributeValue
            {
                AttributeId = MailMetaverseAttributeId,
                Attribute = _mvoType.Attributes.First(a => a.Id == MailMetaverseAttributeId),
                StringValue = email
            }
        ]
    };

    private ObjectMatchingProposal ProposalWith(params ObjectMatchingRuleProposal[] rules) =>
        new(_connectedSystem.ObjectMatchingRuleMode, rules);

    private IEnumerable<ConnectedSystemObject> Unjoined() =>
        _csos.Where(cso => cso.MetaverseObjectId == null && cso.Status == ConnectedSystemObjectStatus.Normal);

    private ConnectedSystemObject UnjoinedCso(string employeeId, string email) => new()
    {
        Id = Guid.NewGuid(),
        ConnectedSystemId = SystemId,
        TypeId = CsoTypeId,
        Type = _csoType,
        Status = ConnectedSystemObjectStatus.Normal,
        AttributeValues =
        [
            new ConnectedSystemObjectAttributeValue
            {
                AttributeId = EmployeeIdAttributeId,
                Attribute = _csoType.Attributes.First(a => a.Id == EmployeeIdAttributeId),
                StringValue = employeeId
            },
            new ConnectedSystemObjectAttributeValue
            {
                AttributeId = MailAttributeId,
                Attribute = _csoType.Attributes.First(a => a.Id == MailAttributeId),
                StringValue = email
            }
        ]
    };

    #endregion
}
