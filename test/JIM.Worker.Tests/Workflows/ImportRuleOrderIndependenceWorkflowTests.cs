// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Several import Synchronisation Rules for one Connected System Object Type (#2823). Joining, projecting and leaving
/// the last rule's scope must not depend on the order the rules happen to load in: they load in no defined order, so a
/// decision that takes "the first rule" can differ between two identical runs.
/// <para>
/// Every test runs twice, with the rules created in each order (<c>reverse</c>), so a decision that still depends on
/// order fails one of the two cases.
/// </para>
/// <list type="bullet">
/// <item>Join: every in-scope rule's matching is evaluated. Rules agreeing on one Metaverse Object join it; rules
/// finding different ones fail the object as an ambiguous match naming the rules, with nothing joined or projected.</item>
/// <item>Projection: the object projects once. In-scope projecting rules targeting different Metaverse Object Types fail
/// it with a Projection Type Conflict.</item>
/// <item>Leaving the last rule: the rules that lost the object (those whose values it holds, and the rule that joined it)
/// decide, and Remain Joined wins.</item>
/// </list>
/// </summary>
[TestFixture]
public class ImportRuleOrderIndependenceWorkflowTests : WorkflowTestBase
{
    #region Join

    [TestCase(false)]
    [TestCase(true)]
    public async Task FullSync_TwoRulesMatchTheSameIdentity_JoinsItRecordingTheFirstRuleByNameAsync(bool reverse)
    {
        var ctx = await ArrangeJoinAsync(reverse);
        var cso = await CreateHrCsoAsync(ctx, "Alice", "E1");

        await RunFullSyncAsync(ctx.Hr);

        cso = await ReloadEntityAsync(cso);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cso.MetaverseObjectId, Is.EqualTo(ctx.AliceId), "both rules find Alice, so the object joins her");
            Assert.That(cso.JoinSyncRuleId, Is.EqualTo(ctx.ByEmployeeIdRuleId),
                "the join names the first matching rule by name, whichever order the rules were created in");
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task FullSync_TwoRulesMatchDifferentIdentities_FailsTheObjectNamingBothRulesAsync(bool reverse)
    {
        var ctx = await ArrangeJoinAsync(reverse);
        // Employee ID E1 is Alice's, display name Bob is Bob's: the two rules disagree.
        var cso = await CreateHrCsoAsync(ctx, "Bob", "E1");
        var identitiesBefore = SyncRepo.MetaverseObjects.Count;

        var activity = await RunFullSyncAsync(ctx.Hr);

        cso = await ReloadEntityAsync(cso);
        var item = ItemFor(activity, cso);
        var candidates = SyncRepo.MatchCandidates.Where(c => c.ActivityRunProfileExecutionItemId == item.Id).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cso.MetaverseObjectId, Is.Null, "JIM never chooses between identities the rules disagree on");
            Assert.That(SyncRepo.MetaverseObjects, Has.Count.EqualTo(identitiesBefore), "nothing is projected either");
            Assert.That(item.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.AmbiguousMatch));
            Assert.That(item.ErrorMessage, Does.Contain("HR by Employee ID").And.Contain("HR by Name"), "the message names both rules");
            Assert.That(candidates.Select(c => c.CandidateId), Is.EquivalentTo(new[] { ctx.AliceId, ctx.BobId }),
                "both identities are recorded as candidates");
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task PreviewSyncForCsoAsync_TwoRulesMatchDifferentIdentities_ReportsTheAmbiguityTheRunReportsAsync(bool reverse)
    {
        var ctx = await ArrangeJoinAsync(reverse);
        var cso = await CreateHrCsoAsync(ctx, "Bob", "E1");

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(ctx.Hr.Id, cso.Id);

        var error = preview.Errors.SingleOrDefault(e => e.Code == SyncPreviewMessageCode.AmbiguousMatch);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(error, Is.Not.Null, "the preview predicts the ambiguity the run reports");
            Assert.That(error?.MatchCandidates.Select(c => c.CandidateId), Is.EquivalentTo(new[] { ctx.AliceId, ctx.BobId }));
            Assert.That(preview.OutcomeTree, Is.Empty, "nothing would be joined or projected");
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task FullSync_SimpleModeTwoRules_JoinsRecordingTheFirstRuleByNameAsync(bool reverse)
    {
        var ctx = await ArrangeJoinAsync(reverse, simpleMode: true);
        var cso = await CreateHrCsoAsync(ctx, "Alice", "E1");

        await RunFullSyncAsync(ctx.Hr);

        cso = await ReloadEntityAsync(cso);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cso.MetaverseObjectId, Is.EqualTo(ctx.AliceId));
            Assert.That(cso.JoinSyncRuleId, Is.EqualTo(ctx.ByEmployeeIdRuleId), "the join names the first rule by name");
        }
    }

    #endregion

    #region Projection

    [TestCase(false)]
    [TestCase(true)]
    public async Task FullSync_TwoRulesProjectIntoOneType_ProjectsOnceRecordingTheFirstRuleByNameAsync(bool reverse)
    {
        var ctx = await ArrangeProjectionAsync(reverse, sameType: true);

        await RunFullSyncAsync(ctx.Hr);

        var cso = await ReloadEntityAsync(ctx.Cso);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(SyncRepo.MetaverseObjects, Has.Count.EqualTo(1), "one object projects one Metaverse Object");
            Assert.That(cso.MetaverseObjectId, Is.Not.Null);
            Assert.That(cso.JoinSyncRuleId, Is.EqualTo(ctx.ContactsRuleId), "the projection names the first rule by name");
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task FullSync_TwoRulesProjectIntoDifferentTypes_FailsTheObjectWithProjectionTypeConflictAsync(bool reverse)
    {
        var ctx = await ArrangeProjectionAsync(reverse, sameType: false);

        var activity = await RunFullSyncAsync(ctx.Hr);

        var cso = await ReloadEntityAsync(ctx.Cso);
        var item = ItemFor(activity, cso);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cso.MetaverseObjectId, Is.Null, "JIM never guesses which type the object is");
            Assert.That(SyncRepo.MetaverseObjects, Is.Empty);
            Assert.That(item.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.ProjectionTypeConflict));
            Assert.That(item.ErrorMessage, Does.Contain("HR Contacts").And.Contain("HR People"), "the message names both rules");
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task FullSync_ProjectingRuleOutOfScope_TakesNoPartAsync(bool reverse)
    {
        var ctx = await ArrangeProjectionAsync(reverse, sameType: false, contactsScopedToSales: true);

        var activity = await RunFullSyncAsync(ctx.Hr);

        var cso = await ReloadEntityAsync(ctx.Cso);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cso.MetaverseObjectId, Is.Not.Null, "only the in-scope rule projects");
            Assert.That(SyncRepo.MetaverseObjects[cso.MetaverseObjectId!.Value].Type.Id, Is.EqualTo(ctx.PersonTypeId));
            Assert.That(activity.RunProfileExecutionItems.All(i => i.ErrorType == ActivityRunProfileExecutionItemErrorType.NotSet), Is.True);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task PreviewSyncForCsoAsync_TwoRulesProjectIntoDifferentTypes_ReportsProjectionTypeConflictAsync(bool reverse)
    {
        var ctx = await ArrangeProjectionAsync(reverse, sameType: false);

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(ctx.Hr.Id, ctx.Cso.Id);

        var error = preview.Errors.SingleOrDefault(e => e.Code == SyncPreviewMessageCode.ProjectionTypeConflict);
        Assert.That(error, Is.Not.Null, "the preview predicts the conflict the run reports");
        Assert.That(preview.OutcomeTree.Any(n => n.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.Projected), Is.False);
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_AnotherRuleProjectsIntoAnotherType_WarnsNamingItAsync()
    {
        await ArrangeProjectionAsync(reverse: false, sameType: false);

        var rule = await SavedRuleAsync("HR People");

        Assert.That(rule.SaveWarnings.Where(w => w.Contains("HR Contacts")).ToList(), Has.Count.EqualTo(1),
            "saving one of two rules projecting one Object Type into different Metaverse Object Types warns once, naming the other");
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_AnotherRuleProjectsIntoTheSameType_DoesNotWarnAsync()
    {
        await ArrangeProjectionAsync(reverse: false, sameType: true);

        var rule = await SavedRuleAsync("HR People");

        Assert.That(rule.SaveWarnings.Where(w => w.Contains("HR Contacts")), Is.Empty);
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_TheOtherRuleDoesNotProject_DoesNotWarnAsync()
    {
        await ArrangeProjectionAsync(reverse: false, sameType: false);
        DbContext.SyncRules.Single(r => r.Name == "HR Contacts").ProjectToMetaverse = false;
        await DbContext.SaveChangesAsync();

        var rule = await SavedRuleAsync("HR People");

        Assert.That(rule.SaveWarnings.Where(w => w.Contains("HR Contacts")), Is.Empty);
    }

    #endregion

    #region Leaving the last rule

    [TestCase(false)]
    [TestCase(true)]
    public async Task FullSync_ObjectLeavesTwoContributingRulesOneRemainJoined_KeepsTheJoinNamingThatRuleAsync(bool reverse)
    {
        var ctx = await ArrangeScopeExitAsync(reverse, salesAction: InboundOutOfScopeAction.RemainJoined);

        await LeaveBothRulesAsync(ctx);
        var activity = await RunFullSyncAsync(ctx.Hr);

        var cso = await ReloadEntityAsync(ctx.Cso);
        var item = ItemFor(activity, cso);
        var outcome = item.SyncOutcomes.SingleOrDefault(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.OutOfScopeRetainJoin);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cso.MetaverseObjectId, Is.EqualTo(ctx.MvoId), "a rule that lost the object says Remain Joined, and Remain Joined wins");
            Assert.That(outcome, Is.Not.Null);
            Assert.That(outcome?.SyncRuleId, Is.EqualTo(ctx.SalesRuleId), "the outcome names the rule whose action decided");
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task FullSync_ObjectLeavesTwoContributingRulesBothDisconnect_DisconnectsAsync(bool reverse)
    {
        var ctx = await ArrangeScopeExitAsync(reverse, salesAction: InboundOutOfScopeAction.Disconnect);

        await LeaveBothRulesAsync(ctx);
        var activity = await RunFullSyncAsync(ctx.Hr);

        var cso = await ReloadEntityAsync(ctx.Cso);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cso.MetaverseObjectId, Is.Null);
            Assert.That(activity.RunProfileExecutionItems.Any(i => i.ObjectChangeType == ObjectChangeType.DisconnectedOutOfScope), Is.True);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task FullSync_RemainJoinedRuleNeverHeldTheObject_DisconnectsAsync(bool reverse)
    {
        // "HR Sales" says Remain Joined but contributed nothing and did not make the join: the object was never its to
        // keep, so the rule that did hold it, "HR Active", decides.
        var ctx = await ArrangeScopeExitAsync(reverse, salesAction: InboundOutOfScopeAction.RemainJoined, salesContributes: false);

        await LeaveBothRulesAsync(ctx);
        var activity = await RunFullSyncAsync(ctx.Hr);

        var cso = await ReloadEntityAsync(ctx.Cso);
        var item = ItemFor(activity, cso);
        var outcome = item.SyncOutcomes.SingleOrDefault(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cso.MetaverseObjectId, Is.Null);
            Assert.That(outcome?.SyncRuleId, Is.EqualTo(ctx.ActiveRuleId), "the disconnect names the rule that held the object");
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task PreviewSyncForCsoAsync_ObjectLeavesTwoContributingRulesOneRemainJoined_PredictsTheRunAsync(bool reverse)
    {
        var ctx = await ArrangeScopeExitAsync(reverse, salesAction: InboundOutOfScopeAction.RemainJoined);
        await LeaveBothRulesAsync(ctx);

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(ctx.Hr.Id, ctx.Cso.Id);
        var activity = await RunFullSyncAsync(ctx.Hr);

        var realTree = SyncPreviewFidelityTests.MapRealOutcomeTree(activity);
        Assert.That(SyncPreviewFidelityTests.DescribeTree(preview.OutcomeTree), Is.EqualTo(SyncPreviewFidelityTests.DescribeTree(realTree)));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task FullSync_ObjectDeletedFromTheSourceWithOneContributingRuleRemainJoined_KeepsTheIdentityAsync(bool reverse)
    {
        var ctx = await ArrangeScopeExitAsync(reverse, salesAction: InboundOutOfScopeAction.RemainJoined);

        var cso = await ReloadEntityAsync(ctx.Cso);
        cso.Status = ConnectedSystemObjectStatus.Obsolete;
        cso.LastUpdated = DateTime.UtcNow;
        await RunFullSyncAsync(ctx.Hr);

        Assert.That(SyncRepo.MetaverseObjects[ctx.MvoId].LastConnectorDisconnectedDate, Is.Null,
            "obsoletion applies the same rule: a contributing rule says Remain Joined, so the identity is not disconnected");
    }

    #endregion

    #region Arrangements

    private sealed record JoinArrangement(
        ConnectedSystem Hr,
        ConnectedSystemObjectType HrType,
        Guid AliceId,
        Guid BobId,
        int ByEmployeeIdRuleId,
        int ByNameRuleId);

    /// <summary>
    /// A Directory projects Alice (E1) and Bob (E2). HR has two import rules that project nothing: "HR by Employee ID"
    /// matches on Employee ID and "HR by Name" on display name.
    /// </summary>
    private async Task<JoinArrangement> ArrangeJoinAsync(bool reverse, bool simpleMode = false)
    {
        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvDisplayName = mvType.Attributes.First(a => a.Name == "DisplayName");
        var mvEmployeeId = mvType.Attributes.First(a => a.Name == "EmployeeId");

        var directory = await CreateConnectedSystemAsync("Directory");
        var directoryType = await CreateCsoTypeAsync(directory.Id, "User");
        var directoryRule = await CreateImportSyncRuleAsync(directory.Id, directoryType, mvType, "Directory Import");
        AddFlow(directoryRule, directoryType, "DisplayName", mvDisplayName);
        AddFlow(directoryRule, directoryType, "EmployeeId", mvEmployeeId);

        var hr = await CreateConnectedSystemAsync("HR");
        var hrType = await CreateCsoTypeAsync(hr.Id, "Person");

        var specs = new List<(string Name, string Attribute, MetaverseAttribute Target)>
        {
            ("HR by Employee ID", "EmployeeId", mvEmployeeId),
            ("HR by Name", "DisplayName", mvDisplayName)
        };
        if (reverse)
            specs.Reverse();

        var rules = new Dictionary<string, SyncRule>();
        foreach (var (name, attribute, target) in specs)
        {
            var rule = await CreateImportSyncRuleAsync(hr.Id, hrType, mvType, name, enableProjection: false);
            rules[name] = rule;
            if (!simpleMode)
                rule.ObjectMatchingRules.Add(MatchingRule(hrType, attribute, mvType, target, syncRule: rule));
        }

        if (simpleMode)
        {
            hr.ObjectMatchingRuleMode = ObjectMatchingRuleMode.ConnectedSystem;
            hrType.ObjectMatchingRules.Add(MatchingRule(hrType, "EmployeeId", mvType, mvEmployeeId, syncRule: null));
        }
        await DbContext.SaveChangesAsync();

        var alice = await CreateCsoAsync(directory.Id, directoryType, "Alice", "E1");
        var bob = await CreateCsoAsync(directory.Id, directoryType, "Bob", "E2");
        await RunFullSyncAsync(directory);
        alice = await ReloadEntityAsync(alice);
        bob = await ReloadEntityAsync(bob);
        Assert.That(alice.MetaverseObjectId, Is.Not.Null, "precondition: the Directory projects Alice");
        Assert.That(bob.MetaverseObjectId, Is.Not.Null, "precondition: the Directory projects Bob");

        return new JoinArrangement(await ReloadEntityAsync(hr), hrType, alice.MetaverseObjectId!.Value, bob.MetaverseObjectId!.Value,
            rules["HR by Employee ID"].Id, rules["HR by Name"].Id);
    }

    private Task<ConnectedSystemObject> CreateHrCsoAsync(JoinArrangement ctx, string displayName, string employeeId) =>
        CreateCsoAsync(ctx.Hr.Id, ctx.HrType, displayName, employeeId);

    private sealed record ProjectionArrangement(ConnectedSystem Hr, ConnectedSystemObject Cso, int PersonTypeId, int ContactsRuleId, int PeopleRuleId);

    /// <summary>
    /// HR has two projecting rules: "HR People" into Person, and "HR Contacts" into Person (<paramref name="sameType"/>)
    /// or Contact. Optionally "HR Contacts" is scoped to the Sales department, which the object is not in.
    /// </summary>
    private async Task<ProjectionArrangement> ArrangeProjectionAsync(bool reverse, bool sameType, bool contactsScopedToSales = false)
    {
        var person = await CreateMvObjectTypeAsync("Person");
        var contact = sameType ? person : await CreateMvObjectTypeAsync("Contact");

        var hr = await CreateConnectedSystemAsync("HR");
        var hrType = await CreateCsoTypeAsync(hr.Id, "Person", HrAttributes());

        var specs = new List<(string Name, MetaverseObjectType Target)> { ("HR People", person), ("HR Contacts", contact) };
        if (reverse)
            specs.Reverse();

        var rules = new Dictionary<string, SyncRule>();
        foreach (var (name, target) in specs)
        {
            var rule = await CreateImportSyncRuleAsync(hr.Id, hrType, target, name);
            AddFlow(rule, hrType, "DisplayName", target.Attributes.First(a => a.Name == "DisplayName"));
            rules[name] = rule;
        }
        if (contactsScopedToSales)
            Scope(rules["HR Contacts"], hrType, "Department", "Sales");
        await DbContext.SaveChangesAsync();

        var cso = await CreateCsoAsync(hr.Id, hrType, "Jane", "E1");
        SetValue(cso, hrType, "Department", "HR");

        return new ProjectionArrangement(await ReloadEntityAsync(hr), cso, person.Id, rules["HR Contacts"].Id, rules["HR People"].Id);
    }

    private sealed record ScopeExitArrangement(
        ConnectedSystem Hr,
        ConnectedSystemObjectType HrType,
        ConnectedSystemObject Cso,
        Guid MvoId,
        int ActiveRuleId,
        int SalesRuleId);

    /// <summary>
    /// HR has two scoped rules: "HR Active" (Active = true; projects; Disconnect; flows display name) and "HR Sales"
    /// (Department = Sales; the given action; flows Employee ID unless <paramref name="salesContributes"/> is false).
    /// Jane is active and in Sales, so both rules cover her and "HR Active" projects her.
    /// </summary>
    private async Task<ScopeExitArrangement> ArrangeScopeExitAsync(bool reverse, InboundOutOfScopeAction salesAction, bool salesContributes = true)
    {
        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvDisplayName = mvType.Attributes.First(a => a.Name == "DisplayName");
        var mvEmployeeId = mvType.Attributes.First(a => a.Name == "EmployeeId");

        var hr = await CreateConnectedSystemAsync("HR");
        var hrType = await CreateCsoTypeAsync(hr.Id, "Person", HrAttributes());

        var names = new List<string> { "HR Active", "HR Sales" };
        if (reverse)
            names.Reverse();

        var rules = new Dictionary<string, SyncRule>();
        foreach (var name in names)
        {
            var isActiveRule = name == "HR Active";
            var rule = await CreateImportSyncRuleAsync(hr.Id, hrType, mvType, name, enableProjection: isActiveRule);
            if (isActiveRule)
            {
                rule.InboundOutOfScopeAction = InboundOutOfScopeAction.Disconnect;
                AddFlow(rule, hrType, "DisplayName", mvDisplayName);
                Scope(rule, hrType, "Active", "true");
            }
            else
            {
                rule.InboundOutOfScopeAction = salesAction;
                if (salesContributes)
                    AddFlow(rule, hrType, "EmployeeId", mvEmployeeId);
                Scope(rule, hrType, "Department", "Sales");
            }
            rules[name] = rule;
        }
        await DbContext.SaveChangesAsync();

        var cso = await CreateCsoAsync(hr.Id, hrType, "Jane", "E1");
        SetValue(cso, hrType, "Department", "Sales");
        SetValue(cso, hrType, "Active", "true");

        await RunFullSyncAsync(hr, "Initial Full Sync");
        cso = await ReloadEntityAsync(cso);
        Assert.That(cso.MetaverseObjectId, Is.Not.Null, "precondition: HR Active projected Jane");

        return new ScopeExitArrangement(await ReloadEntityAsync(hr), hrType, cso, cso.MetaverseObjectId!.Value,
            rules["HR Active"].Id, rules["HR Sales"].Id);
    }

    private async Task LeaveBothRulesAsync(ScopeExitArrangement ctx)
    {
        var cso = await ReloadEntityAsync(ctx.Cso);
        SetValue(cso, ctx.HrType, "Department", "Finance");
        SetValue(cso, ctx.HrType, "Active", "false");
        await ModifyCsoAsync(cso);
    }

    #endregion

    #region Helpers

    private static List<ConnectedSystemObjectTypeAttribute> HrAttributes() =>
    [
        new() { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
        new() { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true },
        new() { Name = "EmployeeId", Type = AttributeDataType.Text, Selected = true },
        new() { Name = "Department", Type = AttributeDataType.Text, Selected = true },
        new() { Name = "Active", Type = AttributeDataType.Text, Selected = true }
    ];

    private static ObjectMatchingRule MatchingRule(
        ConnectedSystemObjectType csoType, string attributeName, MetaverseObjectType mvType, MetaverseAttribute target, SyncRule? syncRule)
    {
        var source = csoType.Attributes.Single(a => a.Name == attributeName);
        var rule = new ObjectMatchingRule
        {
            Order = 0,
            CaseSensitive = true,
            MetaverseObjectType = mvType,
            MetaverseObjectTypeId = mvType.Id,
            TargetMetaverseAttribute = target,
            TargetMetaverseAttributeId = target.Id,
            Sources = [new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttribute = source, ConnectedSystemAttributeId = source.Id }]
        };
        if (syncRule != null)
        {
            rule.SyncRule = syncRule;
            rule.SyncRuleId = syncRule.Id;
        }
        else
        {
            rule.ConnectedSystemObjectType = csoType;
            rule.ConnectedSystemObjectTypeId = csoType.Id;
        }
        return rule;
    }

    private static void Scope(SyncRule rule, ConnectedSystemObjectType csoType, string attributeName, string value) =>
        rule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria = new List<SyncRuleScopingCriteria>
            {
                new()
                {
                    ConnectedSystemAttribute = csoType.Attributes.Single(a => a.Name == attributeName),
                    ComparisonType = SearchComparisonType.Equals,
                    StringValue = value,
                    CaseSensitive = true
                }
            }
        });

    private static void AddFlow(SyncRule rule, ConnectedSystemObjectType csoType, string csAttributeName, MetaverseAttribute target)
    {
        var source = csoType.Attributes.Single(a => a.Name == csAttributeName);
        rule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = rule,
            SyncRuleId = rule.Id,
            TargetMetaverseAttribute = target,
            TargetMetaverseAttributeId = target.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = source, ConnectedSystemAttributeId = source.Id } }
        });
    }

    private static void SetValue(ConnectedSystemObject cso, ConnectedSystemObjectType type, string attributeName, string value)
    {
        var attribute = type.Attributes.Single(a => a.Name == attributeName);
        cso.AttributeValues.RemoveAll(av => av.AttributeId == attribute.Id);
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = attribute.Id,
            Attribute = attribute,
            StringValue = value,
            ConnectedSystemObject = cso
        });
    }

    /// <summary>
    /// Saves the named rule through the one write path the portal, the REST API and PowerShell share, and returns the
    /// instance the save stamped its warnings on.
    /// </summary>
    private async Task<SyncRule> SavedRuleAsync(string name)
    {
        var administrator = new MetaverseObject
        {
            Id = Guid.NewGuid(), Type = DbContext.MetaverseObjectTypes.First(), Created = DateTime.UtcNow, CachedDisplayName = "Test Administrator"
        };
        DbContext.MetaverseObjects.Add(administrator);
        await DbContext.SaveChangesAsync();

        var rule = await Jim.ConnectedSystems.GetSyncRuleAsync(DbContext.SyncRules.Single(r => r.Name == name).Id)
            ?? throw new InvalidOperationException($"arrange: '{name}' loads");
        Assert.That(await Jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, administrator), Is.True, "the save succeeds: a warning, not a refusal");
        return rule;
    }

    private ActivityRunProfileExecutionItem ItemFor(Activity activity, ConnectedSystemObject cso) =>
        SyncRepo.Rpeis.Values.Where(r => r.ActivityId == activity.Id)
            .Concat(activity.RunProfileExecutionItems)
            .DistinctBy(r => r.Id)
            .Single(i => i.ConnectedSystemObjectId == cso.Id);

    private async Task<Activity> RunFullSyncAsync(ConnectedSystem connectedSystem, string profileName = "Full Sync")
    {
        var reloaded = await ReloadEntityAsync(connectedSystem);
        var profile = await CreateRunProfileAsync(reloaded.Id, profileName, ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(reloaded.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, reloaded, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
        return activity;
    }

    #endregion
}
