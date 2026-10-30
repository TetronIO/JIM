// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Application.Services;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Security;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// When a Synchronisation Rule starts, stops or changes how it contributes to a Metaverse Attribute, JIM renumbers
/// the Priority of every contributor's Attribute Flow so the list stays dense. Those other rules have changed in
/// the configuration history's terms (their Priority column moved), so each must get a configuration version for it,
/// on a child Activity of the change that caused it (#2630). Without one, a later unrelated edit of such a rule
/// shows a row nobody made in it ("Priority: not set to 1").
/// <para>
/// The renumbering is order-preserving (the contributors keep the (Priority, Id) order the engine resolves by), so
/// no rule's precedence over another changes and no value changes. Those child Activities therefore record nothing
/// as awaiting synchronisation and judge the change to need no Full Synchronisation, which is what keeps the
/// configuration drift indicator from counting a Sync-affecting Priority edit nobody made.
/// </para>
/// </summary>
[TestFixture]
public class AttributePriorityRenumberingHistoryTests
{
    private const int MetaverseObjectTypeId = 7;
    private const int AttributeId = 42;
    private static readonly byte[] HashKey = new byte[32];

    // Three import rules contributing the same Metaverse Attribute, each on its own Connected System and Object Type so a
    // row recorded as awaiting synchronisation names the rule it came from.
    private static readonly Contributor RuleA = new(RuleId: 1000, MappingId: 10, ConnectedSystemId: 30, ObjectTypeId: 70);
    private static readonly Contributor RuleB = new(RuleId: 2000, MappingId: 20, ConnectedSystemId: 40, ObjectTypeId: 80);
    private static readonly Contributor RuleC = new(RuleId: 3000, MappingId: 30, ConnectedSystemId: 50, ObjectTypeId: 90);

    private sealed record Contributor(int RuleId, int MappingId, int ConnectedSystemId, int ObjectTypeId);

    private Mock<IRepository> _repo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<IServiceSettingsRepository> _settingsRepo = null!;
    private Mock<IConnectedSystemRepository> _csRepo = null!;
    private FakeProtection _protection = null!;
    private JimApplication _jim = null!;
    private List<ConfigurationChangeSynchronisation> _recorded = null!;
    private List<Activity> _createdActivities = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _recorded = [];
        _createdActivities = [];

        _repo = new Mock<IRepository>();
        _repo.Setup(r => r.ConfigurationTests).Returns(TestUtilities.CreateEmptyConfigurationTestRepository());
        _activityRepo = new Mock<IActivityRepository>();
        _settingsRepo = new Mock<IServiceSettingsRepository>();
        _csRepo = new Mock<IConnectedSystemRepository>();
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _repo.Setup(r => r.ServiceSettings).Returns(_settingsRepo.Object);
        _repo.Setup(r => r.ConnectedSystems).Returns(_csRepo.Object);

        // The deletion-choice impact summary is read on every delete; empty means no contributed values, which is the
        // synchronous delete path.
        var metaverseRepo = new Mock<IMetaverseRepository>();
        metaverseRepo.Setup(r => r.GetContributedValuesSummaryAsync(It.IsAny<int>(), It.IsAny<int?>())).ReturnsAsync(new ContributedValuesSummary());
        _repo.Setup(r => r.Metaverse).Returns(metaverseRepo.Object);

        // The repository gives an Activity its id on create; a child links to its parent through it.
        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a =>
            {
                if (a.Id == Guid.Empty)
                    a.Id = Guid.NewGuid();
                _createdActivities.Add(a);
            })
            .Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.GetMaxConfigurationChangeVersionAsync(It.IsAny<ActivityTargetType>(), It.IsAny<int>())).ReturnsAsync(6);

        _csRepo.Setup(r => r.CreateConfigurationChangeSynchronisationAsync(It.IsAny<ConfigurationChangeSynchronisation>()))
            .Callback<ConfigurationChangeSynchronisation>(c => _recorded.Add(c))
            .Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.UpdateSyncRuleAsync(It.IsAny<SyncRule>())).Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.CreateSyncRuleAsync(It.IsAny<SyncRule>())).Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.CreateSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>())).Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.DeleteSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>())).Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.DeleteSyncRuleAsync(It.IsAny<SyncRule>())).Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.UpdateSyncRuleMappingsAsync(It.IsAny<IReadOnlyCollection<SyncRuleMapping>>())).Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.GetSyncRuleMappingsAsync(It.IsAny<int>())).ReturnsAsync(new List<SyncRuleMapping>());
        _csRepo.Setup(r => r.GetSyncRuleInitialPasswordAsync(It.IsAny<int>())).ReturnsAsync((SyncRuleInitialPassword?)null);
        _csRepo.Setup(r => r.GetImportSyncRulesForMetaverseObjectTypeAsync(It.IsAny<int>())).ReturnsAsync(() => []);
        _csRepo.Setup(r => r.GetImportMappingTargetMetaverseAttributesAsync(It.IsAny<int>())).ReturnsAsync(new Dictionary<int, int>());
        SetupContributors();

        _protection = new FakeProtection();
        _jim = new JimApplication(_repo.Object, syncRepository: TestUtilities.QueuedChangeWithdrawalSyncRepository()) { CredentialProtection = _protection };

        SetupTrackingSetting();
        SetupHashKeySetting();
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    // -- Attribute Flow created -----------------------------------------------------------------------------------------

    [Test]
    public async Task CreateSyncRuleMappingAsync_SecondContributor_VersionsTheIncumbentsRuleOnAChildActivityAsync()
    {
        // Rule A is the sole contributor at the sentinel. Rule B joins, so A's Attribute Flow goes from "no explicit
        // priority" to 1: a change to A's configuration that A's history must show.
        var incumbent = StoreRule(RuleA, Mapping(RuleA, int.MaxValue));
        var created = NewMapping(RuleB);
        StoreRule(RuleB, created);
        SetupContributors(incumbent, created);
        var user = NewUser();

        await _jim.ConnectedSystems.CreateSyncRuleMappingAsync(created, user);

        var parent = ParentActivity();
        var child = ChildOf(parent, RuleA);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(incumbent.Priority, Is.EqualTo(1), "precondition: the renumbering still happens");
            Assert.That(child.TargetType, Is.EqualTo(ActivityTargetType.SynchronisationRule));
            Assert.That(child.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Update));
            Assert.That(child.TargetName, Is.EqualTo("Rule 1000"));
            Assert.That(child.ConfigurationChangeVersion, Is.EqualTo(7), "the incumbent's rule consumes its own next version");
            Assert.That(child.ConfigurationChangeSnapshot, Is.Not.Null);
            Assert.That(child.Message, Does.Contain("Rule 2000"), "the child says which change renumbered the order");
            Assert.That(child.Status, Is.EqualTo(ActivityStatus.Complete));
        }
    }

    [Test]
    public async Task CreateSyncRuleMappingAsync_SecondContributor_ChildCarriesTheUsersInitiatorAsync()
    {
        var incumbent = StoreRule(RuleA, Mapping(RuleA, int.MaxValue));
        var created = NewMapping(RuleB);
        StoreRule(RuleB, created);
        SetupContributors(incumbent, created);
        var user = NewUser();

        await _jim.ConnectedSystems.CreateSyncRuleMappingAsync(created, user);

        var parent = ParentActivity();
        var child = ChildOf(parent, RuleA);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(parent.InitiatedByType, Is.EqualTo(ActivityInitiatorType.User), "precondition");
            Assert.That(child.InitiatedByType, Is.EqualTo(ActivityInitiatorType.User));
            Assert.That(child.InitiatedById, Is.EqualTo(user.Id));
            Assert.That(child.InitiatedByName, Is.EqualTo(parent.InitiatedByName));
        }
    }

    [Test]
    public async Task CreateSyncRuleMappingAsync_SecondContributorByApiKey_ChildCarriesTheApiKeyInitiatorAsync()
    {
        var incumbent = StoreRule(RuleA, Mapping(RuleA, int.MaxValue));
        var created = NewMapping(RuleB);
        StoreRule(RuleB, created);
        SetupContributors(incumbent, created);
        var apiKey = NewApiKey();

        await _jim.ConnectedSystems.CreateSyncRuleMappingAsync(created, apiKey);

        var child = ChildOf(ParentActivity(), RuleA);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(child.InitiatedByType, Is.EqualTo(ActivityInitiatorType.ApiKey));
            Assert.That(child.InitiatedById, Is.EqualTo(apiKey.Id));
            Assert.That(child.InitiatedByName, Is.EqualTo(apiKey.Name));
        }
    }

    [Test]
    public async Task CreateSyncRuleMappingAsync_SecondContributor_VersionsTheEditedRuleOnceAsync()
    {
        // The rule being edited is versioned by its own Activity, with its mapping at the priority it ends up with. The
        // helper that versions the other rules must not version it a second time.
        var incumbent = StoreRule(RuleA, Mapping(RuleA, int.MaxValue));
        var created = NewMapping(RuleB);
        StoreRule(RuleB, created);
        SetupContributors(incumbent, created);

        await _jim.ConnectedSystems.CreateSyncRuleMappingAsync(created, NewUser());

        var versionedForB = _createdActivities.Where(a => a.SyncRuleId == RuleB.RuleId && a.ConfigurationChangeVersion != null).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(versionedForB, Has.Count.EqualTo(1), "one version for the edited rule, not two");
            Assert.That(versionedForB.Single().ParentActivityId, Is.Null, "and it is the mapping's own Activity");
            Assert.That(_createdActivities, Has.Count.EqualTo(2), "the mapping's Activity and one child for the incumbent's rule");
        }
    }

    [Test]
    public async Task CreateSyncRuleMappingAsync_SecondContributor_ChildRecordsNothingAwaitingSynchronisationAndNeedsNoFullSynchronisationAsync()
    {
        // The Priority change is classified Sync-affecting, but the renumbering keeps every contributor in the order the
        // engine resolves by, so it moves no value. The rule's Object Type must not be queued for a synchronisation, and
        // the child must say so explicitly: a null judgement is counted by the drift indicator, only a false is not.
        var incumbent = StoreRule(RuleA, Mapping(RuleA, int.MaxValue), baselineSnapshot: true);
        var created = NewMapping(RuleB);
        StoreRule(RuleB, created);
        SetupContributors(incumbent, created);

        await _jim.ConnectedSystems.CreateSyncRuleMappingAsync(created, NewUser());

        var child = ChildOf(ParentActivity(), RuleA);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(child.ConfigurationChangeClass, Is.EqualTo(ConfigurationChangeClass.SyncAffecting), "precondition: the Priority edit is graded");
            Assert.That(child.ConfigurationChangeNeedsFullSynchronisation, Is.False, "an explicit false, not a null the drift indicator would count");
            Assert.That(_recorded.Where(r => r.SyncRuleId == RuleA.RuleId || r.ConnectedSystemObjectTypeId == RuleA.ObjectTypeId), Is.Empty,
                "nothing is queued for the incumbent's Object Type");
            Assert.That(_recorded.Select(r => r.ConfigurationChangeActivityId), Is.All.EqualTo(ParentActivity().Id),
                "only the edited rule's own Activity records a change awaiting synchronisation");
        }
    }

    [Test]
    public async Task CreateSyncRuleMappingAsync_FirstContributor_VersionsNoOtherRuleAsync()
    {
        var created = NewMapping(RuleB);
        StoreRule(RuleB, created);
        SetupContributors(created);

        await _jim.ConnectedSystems.CreateSyncRuleMappingAsync(created, NewUser());

        Assert.That(_createdActivities, Has.Count.EqualTo(1), "only the mapping's own Activity: a sole contributor renumbers nothing");
    }

    [Test]
    public async Task CreateSyncRuleMappingAsync_ThirdContributorOnADenseList_VersionsNoOtherRuleAsync()
    {
        // The list is already 1..2, so the newcomer takes 3 and nobody else's number moves.
        var first = StoreRule(RuleA, Mapping(RuleA, 1));
        var second = StoreRule(RuleC, Mapping(RuleC, 2));
        var created = NewMapping(RuleB);
        StoreRule(RuleB, created);
        SetupContributors(first, second, created);

        await _jim.ConnectedSystems.CreateSyncRuleMappingAsync(created, NewUser());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(created.Priority, Is.EqualTo(3), "precondition: the newcomer lands last");
            Assert.That(_createdActivities, Has.Count.EqualTo(1), "only the mapping's own Activity; no other rule's numbers moved");
        }
    }

    // -- Attribute Flow deleted -----------------------------------------------------------------------------------------

    [Test]
    public async Task DeleteSyncRuleMappingAsync_LeavesOneContributor_VersionsTheRemainingContributorsRuleAsync()
    {
        // Rule B's flow goes; rule A is the sole contributor again, so its explicit priority resets to the sentinel.
        var remaining = StoreRule(RuleA, Mapping(RuleA, 1));
        StoreRule(RuleB, Mapping(RuleB, 2));
        SetupContributors(remaining);
        var user = NewUser();

        await _jim.ConnectedSystems.DeleteSyncRuleMappingAsync(Mapping(RuleB, 2, withRule: true), user);

        var parent = ParentActivity();
        var child = ChildOf(parent, RuleA);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(remaining.Priority, Is.EqualTo(int.MaxValue), "precondition: the reset still happens");
            Assert.That(child.ConfigurationChangeVersion, Is.EqualTo(7));
            Assert.That(child.ConfigurationChangeNeedsFullSynchronisation, Is.False);
            Assert.That(child.InitiatedByType, Is.EqualTo(ActivityInitiatorType.User));
            Assert.That(child.InitiatedById, Is.EqualTo(user.Id));
            Assert.That(_recorded.Where(r => r.ConnectedSystemObjectTypeId == RuleA.ObjectTypeId), Is.Empty);
        }
    }

    [Test]
    public async Task DeleteSyncRuleMappingAsync_ApiKeyInitiated_ChildCarriesTheApiKeyInitiatorAsync()
    {
        var remaining = StoreRule(RuleA, Mapping(RuleA, 1));
        SetupContributors(remaining);
        var apiKey = NewApiKey();

        await _jim.ConnectedSystems.DeleteSyncRuleMappingAsync(Mapping(RuleB, 2, withRule: true), apiKey);

        var child = ChildOf(ParentActivity(), RuleA);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(child.InitiatedByType, Is.EqualTo(ActivityInitiatorType.ApiKey));
            Assert.That(child.InitiatedById, Is.EqualTo(apiKey.Id));
            Assert.That(child.InitiatedByName, Is.EqualTo(apiKey.Name));
        }
    }

    [Test]
    public async Task DeleteSyncRuleMappingAsync_RemovesAMiddleContributor_VersionsOnlyTheRuleWhoseNumberMovedAsync()
    {
        // [A:1, B:2, C:3] loses B: C closes the gap to 2 and A, which keeps 1, is untouched.
        var first = StoreRule(RuleA, Mapping(RuleA, 1));
        var last = StoreRule(RuleC, Mapping(RuleC, 3));
        SetupContributors(first, last);

        await _jim.ConnectedSystems.DeleteSyncRuleMappingAsync(Mapping(RuleB, 2, withRule: true), NewUser());

        var parent = ParentActivity();
        var children = _createdActivities.Where(a => a.ParentActivityId == parent.Id).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(last.Priority, Is.EqualTo(2), "precondition: the gap closes");
            Assert.That(children.Select(c => c.SyncRuleId), Is.EqualTo(new int?[] { RuleC.RuleId }), "only the rule whose number moved");
        }
    }

    [Test]
    public async Task DeleteSyncRuleMappingAsync_NoContributorRemains_VersionsNoOtherRuleAsync()
    {
        SetupContributors();

        await _jim.ConnectedSystems.DeleteSyncRuleMappingAsync(Mapping(RuleB, int.MaxValue, withRule: true), NewUser());

        Assert.That(_createdActivities, Has.Count.EqualTo(1), "only the deletion's own Activity");
    }

    // -- whole-rule save ------------------------------------------------------------------------------------------------

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_ImportMappingAdded_VersionsTheIncumbentsRuleOnAChildActivityAsync()
    {
        var incumbent = StoreRule(RuleA, Mapping(RuleA, int.MaxValue));
        var added = Mapping(RuleB, int.MaxValue);
        var rule = SaveableRule(RuleB, added);
        SetupContributors(incumbent, added);
        var user = NewUser();

        var saved = await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, user);

        var parent = ParentActivity();
        var child = ChildOf(parent, RuleA);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(saved, Is.True);
            Assert.That(incumbent.Priority, Is.EqualTo(1), "precondition: the renumbering still happens");
            Assert.That(child.ConfigurationChangeVersion, Is.EqualTo(7));
            Assert.That(child.ConfigurationChangeNeedsFullSynchronisation, Is.False);
            Assert.That(child.InitiatedByType, Is.EqualTo(ActivityInitiatorType.User));
            Assert.That(child.InitiatedById, Is.EqualTo(user.Id));
            Assert.That(_recorded.Where(r => r.ConnectedSystemObjectTypeId == RuleA.ObjectTypeId), Is.Empty);
        }
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_ImportMappingAddedByApiKey_ChildCarriesTheApiKeyInitiatorAsync()
    {
        var incumbent = StoreRule(RuleA, Mapping(RuleA, int.MaxValue));
        var added = Mapping(RuleB, int.MaxValue);
        var rule = SaveableRule(RuleB, added);
        SetupContributors(incumbent, added);
        var apiKey = NewApiKey();

        await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, apiKey);

        var child = ChildOf(ParentActivity(), RuleA);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(child.InitiatedByType, Is.EqualTo(ActivityInitiatorType.ApiKey));
            Assert.That(child.InitiatedById, Is.EqualTo(apiKey.Id));
            Assert.That(child.ConfigurationChangeNeedsFullSynchronisation, Is.False);
        }
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_ImportMappingAdded_VersionsTheSavedRuleOnceAsync()
    {
        var incumbent = StoreRule(RuleA, Mapping(RuleA, int.MaxValue));
        var added = Mapping(RuleB, int.MaxValue);
        var rule = SaveableRule(RuleB, added);
        SetupContributors(incumbent, added);

        await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, NewUser());

        var versionedForB = _createdActivities.Where(a => a.SyncRuleId == RuleB.RuleId && a.ConfigurationChangeVersion != null).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(versionedForB, Has.Count.EqualTo(1), "one version for the saved rule, not two");
            Assert.That(versionedForB.Single().ParentActivityId, Is.Null);
            Assert.That(_createdActivities, Has.Count.EqualTo(2), "the save's Activity and one child for the incumbent's rule");
        }
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_ImportMappingRemoved_VersionsTheRuleWhoseNumberMovedAsync()
    {
        // The saved rule drops its flow, leaving [A:1, C:3]: C closes the gap, A is unchanged.
        var first = StoreRule(RuleA, Mapping(RuleA, 1));
        var last = StoreRule(RuleC, Mapping(RuleC, 3));
        var rule = SaveableRule(RuleB);
        _csRepo.Setup(r => r.GetImportMappingTargetMetaverseAttributesAsync(RuleB.RuleId))
            .ReturnsAsync(new Dictionary<int, int> { [RuleB.MappingId] = AttributeId });
        SetupContributors(first, last);

        await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, NewUser());

        var children = _createdActivities.Where(a => a.ParentActivityId == ParentActivity().Id).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(last.Priority, Is.EqualTo(2), "precondition: the gap closes");
            Assert.That(children.Select(c => c.SyncRuleId), Is.EqualTo(new int?[] { RuleC.RuleId }));
        }
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_ImportMappingRetargeted_VersionsTheRuleLeftWithASoleContributorAsync()
    {
        // Rule B's flow moves to another attribute, where it lands last. On the attribute it left, rule A is the sole
        // contributor again, so its explicit priority resets to the sentinel.
        const int otherAttributeId = 43;
        var leftBehind = StoreRule(RuleA, Mapping(RuleA, 1));
        var retargeted = Mapping(RuleB, 1);
        retargeted.TargetMetaverseAttributeId = otherAttributeId;
        retargeted.TargetMetaverseAttribute = new MetaverseAttribute { Id = otherAttributeId, Name = "title" };
        var rule = SaveableRule(RuleB, retargeted);
        _csRepo.Setup(r => r.GetImportMappingTargetMetaverseAttributesAsync(RuleB.RuleId))
            .ReturnsAsync(new Dictionary<int, int> { [RuleB.MappingId] = AttributeId });
        SetupContributors(leftBehind);
        _csRepo.Setup(r => r.GetImportSyncRuleMappingsForMetaverseAttributeAsync(MetaverseObjectTypeId, otherAttributeId))
            .ReturnsAsync(() => [retargeted]);

        await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, NewUser());

        var children = _createdActivities.Where(a => a.ParentActivityId == ParentActivity().Id).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(leftBehind.Priority, Is.EqualTo(int.MaxValue), "precondition: the reset still happens");
            Assert.That(children.Select(c => c.SyncRuleId), Is.EqualTo(new int?[] { RuleA.RuleId }));
        }
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_NoMappingChanges_VersionsNoOtherRuleAsync()
    {
        var unchanged = Mapping(RuleB, 2);
        var rule = SaveableRule(RuleB, unchanged);
        _csRepo.Setup(r => r.GetImportMappingTargetMetaverseAttributesAsync(RuleB.RuleId))
            .ReturnsAsync(new Dictionary<int, int> { [RuleB.MappingId] = AttributeId });

        await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, NewUser());

        Assert.That(_createdActivities, Has.Count.EqualTo(1), "only the save's own Activity");
    }

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_OnlyTheSavedRulesOwnNumberMoves_VersionsNoOtherRuleAsync()
    {
        // Dense [A:1, C:2] plus the saved rule's new flow at the end: only the saved rule's own mapping is renumbered.
        var first = StoreRule(RuleA, Mapping(RuleA, 1));
        var second = StoreRule(RuleC, Mapping(RuleC, 2));
        var added = Mapping(RuleB, int.MaxValue);
        var rule = SaveableRule(RuleB, added);
        SetupContributors(first, second, added);

        await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, NewUser());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(added.Priority, Is.EqualTo(3), "precondition: the newcomer lands last");
            Assert.That(_createdActivities, Has.Count.EqualTo(1), "only the save's own Activity");
        }
    }

    // -- rule deletion --------------------------------------------------------------------------------------------------

    [Test]
    public async Task DeleteSyncRuleAsync_ImportRule_VersionsTheRuleWhoseNumberMovedOnAChildOfTheDeletionAsync()
    {
        // [A:1, B:2, C:3] loses rule B: C closes the gap to 2 and A is untouched.
        var first = StoreRule(RuleA, Mapping(RuleA, 1));
        var last = StoreRule(RuleC, Mapping(RuleC, 3));
        SetupContributors(first, last);
        var user = NewUser();

        await _jim.ConnectedSystems.DeleteSyncRuleAsync(DeletableRule(RuleB), user);

        var parent = ParentActivity();
        var children = _createdActivities.Where(a => a.ParentActivityId == parent.Id).ToList();
        var child = children.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(parent.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Delete), "precondition");
            Assert.That(last.Priority, Is.EqualTo(2), "precondition: the gap closes");
            Assert.That(child.SyncRuleId, Is.EqualTo(RuleC.RuleId));
            Assert.That(child.ConfigurationChangeVersion, Is.EqualTo(7));
            Assert.That(child.ConfigurationChangeNeedsFullSynchronisation, Is.False);
            Assert.That(child.InitiatedByType, Is.EqualTo(ActivityInitiatorType.User));
            Assert.That(child.InitiatedById, Is.EqualTo(user.Id));
            Assert.That(_recorded, Is.Empty, "a deletion queues nothing for the rules whose numbers moved");
        }
    }

    [Test]
    public async Task DeleteSyncRuleAsync_ApiKeyInitiated_LeavesOneContributor_ChildCarriesTheApiKeyInitiatorAsync()
    {
        var remaining = StoreRule(RuleA, Mapping(RuleA, 1));
        SetupContributors(remaining);
        var apiKey = NewApiKey();

        await _jim.ConnectedSystems.DeleteSyncRuleAsync(DeletableRule(RuleB), apiKey);

        var child = ChildOf(ParentActivity(), RuleA);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(remaining.Priority, Is.EqualTo(int.MaxValue), "precondition: the sole contributor resets to the sentinel");
            Assert.That(child.InitiatedByType, Is.EqualTo(ActivityInitiatorType.ApiKey));
            Assert.That(child.InitiatedById, Is.EqualTo(apiKey.Id));
            Assert.That(child.InitiatedByName, Is.EqualTo(apiKey.Name));
        }
    }

    [Test]
    public async Task ExecuteSyncRuleDeletionRecallAsync_FinalDeleteStep_ChildCarriesTheTasksSystemInitiatorAsync()
    {
        // The worker's final step has no principal object, only the task's initiator triad; a task queued by an internal
        // caller is attributed to the system. The child must carry what the delete Activity carries.
        var remaining = StoreRule(RuleA, Mapping(RuleA, 1));
        SetupContributors(remaining);
        var ruleToDelete = DeletableRule(RuleB);
        _csRepo.Setup(r => r.GetSyncRuleAsync(RuleB.RuleId)).ReturnsAsync(ruleToDelete);
        var task = new DeleteSyncRuleWorkerTask(RuleB.RuleId, recallContributedValues: false)
        {
            InitiatedByType = ActivityInitiatorType.System,
            InitiatedByName = "System",
            Activity = new Activity
            {
                Id = Guid.NewGuid(),
                TargetName = ruleToDelete.Name,
                TargetType = ActivityTargetType.SynchronisationRule,
                TargetOperationType = ActivityTargetOperationType.RecallAttributeValues
            }
        };

        await _jim.ConnectedSystems.ExecuteSyncRuleDeletionRecallAsync(task);

        var deleteActivity = _createdActivities.Single(a => a.ParentActivityId == task.Activity.Id);
        var child = ChildOf(deleteActivity, RuleA);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(deleteActivity.InitiatedByType, Is.EqualTo(ActivityInitiatorType.System), "precondition");
            Assert.That(child.InitiatedByType, Is.EqualTo(ActivityInitiatorType.System));
            Assert.That(child.InitiatedById, Is.Null);
            Assert.That(child.InitiatedByName, Is.EqualTo("System"));
            Assert.That(child.ConfigurationChangeNeedsFullSynchronisation, Is.False);
        }
    }

    // -- helpers --------------------------------------------------------------------------------------------------------

    private Activity ParentActivity() => _createdActivities.Single(a => a.ParentActivityId == null);

    private Activity ChildOf(Activity parent, Contributor rule)
    {
        var children = _createdActivities.Where(a => a.ParentActivityId == parent.Id && a.SyncRuleId == rule.RuleId).ToList();
        Assert.That(children, Has.Count.EqualTo(1), $"expected one child Activity of '{parent.TargetName}' versioning Synchronisation Rule {rule.RuleId}");
        return children[0];
    }

    private void SetupContributors(params SyncRuleMapping[] mappings) =>
        _csRepo.Setup(r => r.GetImportSyncRuleMappingsForMetaverseAttributeAsync(MetaverseObjectTypeId, AttributeId))
            .ReturnsAsync(() => mappings.ToList());

    // The rule as a reload finds it, carrying the very mapping instance the renumbering rewrites, so the snapshot a
    // child captures reflects the priority the rule ends up with. With a baseline, the change history last saw the rule
    // as it stands now (before the renumbering), so the capture has something to diff against.
    private SyncRuleMapping StoreRule(Contributor contributor, SyncRuleMapping mapping, bool baselineSnapshot = false)
    {
        var rule = RuleOf(contributor);
        rule.AttributeFlowRules = [mapping];

        if (baselineSnapshot)
        {
            _activityRepo.Setup(r => r.GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType.SynchronisationRule, contributor.RuleId))
                .ReturnsAsync(ConfigurationSnapshotService.Serialise(_jim.ConfigurationSnapshots.CreateSnapshot(rule, HashKey)));
        }

        _csRepo.Setup(r => r.GetSyncRuleAsync(contributor.RuleId)).ReturnsAsync(rule);
        return mapping;
    }

    private static SyncRule RuleOf(Contributor contributor) => new()
    {
        Id = contributor.RuleId,
        Name = $"Rule {contributor.RuleId}",
        Direction = SyncRuleDirection.Import,
        Enabled = true,
        ProjectToMetaverse = true,
        ConnectedSystemId = contributor.ConnectedSystemId,
        ConnectedSystem = new ConnectedSystem
        {
            Id = contributor.ConnectedSystemId,
            Name = $"System {contributor.ConnectedSystemId}",
            ObjectMatchingRuleMode = ObjectMatchingRuleMode.ConnectedSystem
        },
        ConnectedSystemObjectTypeId = contributor.ObjectTypeId,
        ConnectedSystemObjectType = new ConnectedSystemObjectType { Id = contributor.ObjectTypeId, Name = "user", ConnectedSystemId = contributor.ConnectedSystemId },
        MetaverseObjectTypeId = MetaverseObjectTypeId,
        MetaverseObjectType = new MetaverseObjectType { Id = MetaverseObjectTypeId, Name = "Person" }
    };

    /// <summary>
    /// A persisted import mapping as the contributor query returns it: its rule carried by the scalar and the navigation.
    /// </summary>
    private static SyncRuleMapping Mapping(Contributor contributor, int priority, bool withRule = false) => new()
    {
        Id = contributor.MappingId,
        Priority = priority,
        TargetMetaverseAttributeId = AttributeId,
        TargetMetaverseAttribute = new MetaverseAttribute { Id = AttributeId, Name = "department", Type = AttributeDataType.Text },
        SyncRuleId = contributor.RuleId,
        SyncRule = withRule ? RuleOf(contributor) : new SyncRule { Id = contributor.RuleId, Name = $"Rule {contributor.RuleId}" }
    };

    /// <summary>A mapping being created: no explicit priority yet, its rule's object type scoping the priority list.</summary>
    private static SyncRuleMapping NewMapping(Contributor contributor) => Mapping(contributor, int.MaxValue, withRule: true);

    /// <summary>A valid, already-persisted import rule as the portal or REST API hands it over for a whole-rule save.</summary>
    private static SyncRule SaveableRule(Contributor contributor, params SyncRuleMapping[] mappings)
    {
        var rule = RuleOf(contributor);
        foreach (var mapping in mappings)
            rule.AttributeFlowRules.Add(mapping);
        return rule;
    }

    /// <summary>The rule being deleted, carrying the flow whose attribute's list is re-densified.</summary>
    private static SyncRule DeletableRule(Contributor contributor) =>
        SaveableRule(contributor, Mapping(contributor, 2));

    private void SetupTrackingSetting() =>
        _settingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled))
            .ReturnsAsync(new ServiceSetting
            {
                Key = Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled,
                DisplayName = "Track configuration changes",
                ValueType = ServiceSettingValueType.Boolean,
                Value = "true"
            });

    private void SetupHashKeySetting() =>
        _settingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ConfigurationChangeHashKey))
            .ReturnsAsync(new ServiceSetting
            {
                Key = Constants.SettingKeys.ConfigurationChangeHashKey,
                DisplayName = "Configuration change hash key",
                ValueType = ServiceSettingValueType.StringEncrypted,
                Value = _protection.Protect(Convert.ToBase64String(HashKey))
            });

    private static ApiKey NewApiKey() => new() { Id = Guid.NewGuid(), Name = "test-key" };

    private static MetaverseObject NewUser() => new() { Id = Guid.NewGuid(), CachedDisplayName = "Admin User" };

    /// <summary>A round-trip credential-protection test double using a recognisable encrypted-value prefix.</summary>
    private sealed class FakeProtection : ICredentialProtectionService
    {
        private const string Prefix = "$JIM$v1$";

        public string? Protect(string? plainText) =>
            string.IsNullOrEmpty(plainText) ? plainText : Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(plainText));

        public string? Unprotect(string? protectedData) =>
            string.IsNullOrEmpty(protectedData) || !IsProtected(protectedData)
                ? protectedData
                : Encoding.UTF8.GetString(Convert.FromBase64String(protectedData[Prefix.Length..]));

        public bool IsProtected(string? value) =>
            !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);
    }
}
