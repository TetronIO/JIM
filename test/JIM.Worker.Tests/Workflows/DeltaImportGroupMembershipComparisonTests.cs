// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// #2817: a Delta Import of a group that lost a few members loads, of its member attribute, only the values the database
/// says the comparison could still decide either way, instead of every member (450,000 at the 500,000-user scale). What it
/// stages, saves and records must be exactly what comparing the whole group gave: the same values held afterwards, the
/// same change record, the same execution item, and no removal and re-addition of a member it did not load.
/// </summary>
[TestFixture]
public class DeltaImportGroupMembershipComparisonTests : WorkflowTestBase
{
    private const int MemberCount = 50;

    /// <summary>Records what each import asks of the repository.</summary>
    private sealed class ComparisonSpySyncRepository : JIM.InMemoryData.SyncRepository
    {
        public List<HeldReferenceValueQuestion> Questions { get; } = new();
        public List<(IReadOnlyCollection<int> Excluded, IReadOnlyCollection<Guid> Included)> PartialLoads { get; } = new();
        public bool FoldsCaseLikeIdentifierKey { get; set; } = true;

        public override Task<List<HeldReferenceValueAnswer>> GetHeldReferenceValueAnswersAsync(IReadOnlyList<HeldReferenceValueQuestion> questions)
        {
            Questions.AddRange(questions);
            return base.GetHeldReferenceValueAnswersAsync(questions);
        }

        /// <summary>The attributes found held exactly as imported (#2836), which were then not asked about.</summary>
        public List<(Guid ConnectedSystemObjectId, int AttributeId)> HeldAsImported { get; } = new();

        /// <summary>False to name nothing as held as imported, so every question is asked: the import as it was before #2836.</summary>
        public bool RecognisesAttributesHeldAsImported { get; set; } = true;

        public override async Task<HashSet<(Guid ConnectedSystemObjectId, int AttributeId)>> GetReferenceAttributesHeldAsImportedAsync(
            IReadOnlyList<HeldReferenceValueQuestion> questions)
        {
            if (!RecognisesAttributesHeldAsImported)
                return new HashSet<(Guid ConnectedSystemObjectId, int AttributeId)>();

            var heldAsImported = await base.GetReferenceAttributesHeldAsImportedAsync(questions);
            HeldAsImported.AddRange(heldAsImported);
            return heldAsImported;
        }

        public override Task<List<ConnectedSystemObject>> GetConnectedSystemObjectsByIdsAsync(int connectedSystemId, IEnumerable<Guid> csoIds,
            IReadOnlyCollection<int> excludedAttributeIds, IReadOnlyCollection<Guid> includedValueIds)
        {
            PartialLoads.Add((excludedAttributeIds.ToList(), includedValueIds.ToList()));
            return base.GetConnectedSystemObjectsByIdsAsync(connectedSystemId, csoIds, excludedAttributeIds, includedValueIds);
        }

        public override Task<bool> DoesDatabaseFoldCaseLikeIdentifierKeyAsync() => Task.FromResult(FoldsCaseLikeIdentifierKey);

        /// <summary>The change record each execution item carried when it was written, which the import releases after.</summary>
        public Dictionary<ActivityRunProfileExecutionItem, ConnectedSystemObjectChange?> WrittenChanges { get; } = new();

        public override Task PersistRpeiCsoChangesAsync(List<ActivityRunProfileExecutionItem> rpeis)
        {
            foreach (var rpei in rpeis)
                WrittenChanges[rpei] = rpei.ConnectedSystemObjectChange;
            return base.PersistRpeiCsoChangesAsync(rpeis);
        }
    }

    private ComparisonSpySyncRepository _spy = null!;

    [SetUp]
    public void SetUpSpy()
    {
        _spy = new ComparisonSpySyncRepository();
        _spy.SetSyncOutcomeTrackingLevel(ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.Detailed);
        SyncRepo = _spy;
        Jim = new JimApplication(Repository, syncRepository: SyncRepo);
    }

    [Test]
    public async Task DeltaImport_GroupLosesMembersAndGainsOne_LoadsOnlyTheUndecidedMembersAndStagesTheSameChangesAsync()
    {
        var lab = await ArrangeAsync();

        var activity = await RunImportAsync(lab, [[GroupImport(lab, Members(2, MemberCount - 1).Append(UserDn(MemberCount)))]]);

        var stored = await StoredMembersAsync(lab);
        var item = activity.RunProfileExecutionItems.Single();
        var valueChanges = _spy.WrittenChanges[item]!.AttributeChanges.Single().ValueChanges;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Errors(activity), Is.Empty);
            Assert.That(stored.Select(av => av.UnresolvedReferenceValue), Is.EquivalentTo(Members(2, MemberCount)), "Every member the directory holds, and nothing else");
            Assert.That(stored.Single(av => av.UnresolvedReferenceValue == UserDn(MemberCount)).ReferenceValueId, Is.EqualTo(lab.Users[MemberCount].Id),
                "The new member is resolved to the user JIM holds");
            Assert.That(stored.Where(av => av.UnresolvedReferenceValue != UserDn(MemberCount)).Select(av => av.Id),
                Is.EquivalentTo(lab.MemberValueIds.Skip(2)), "Members kept are the values JIM held, not removed and added again");
            Assert.That(item.ObjectChangeType, Is.EqualTo(ObjectChangeType.Updated));
            Assert.That(valueChanges.Where(v => v.ValueChangeType == ValueChangeType.Remove).Select(v => v.StringValue), Is.EquivalentTo(Members(0, 1)));
            Assert.That(valueChanges.Where(v => v.ValueChangeType == ValueChangeType.Add).Select(v => v.StringValue), Is.EqualTo(new[] { UserDn(MemberCount) }));

            Assert.That(_spy.Questions.Select(q => (q.ConnectedSystemObjectId, q.AttributeId)), Is.EqualTo(new[] { (lab.Group.Id, lab.Member.Id) }));
            Assert.That(_spy.PartialLoads, Has.Count.EqualTo(1), "The group is loaded without most of its members");
            Assert.That(_spy.PartialLoads[0].Excluded, Is.EqualTo(new[] { lab.Member.Id }));
            Assert.That(_spy.PartialLoads[0].Included, Is.EquivalentTo(lab.MemberValueIds.Take(2)), "Only the two members it lost are loaded");
        }
    }

    [Test]
    public async Task DeltaImport_GroupUnchanged_StagesNothingAndLoadsNoMemberAsync()
    {
        var lab = await ArrangeAsync();

        var activity = await RunImportAsync(lab, [[GroupImport(lab, Members(0, MemberCount - 1))]]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.RunProfileExecutionItems, Is.Empty, "No change, so no execution item");
            Assert.That((await StoredMembersAsync(lab)).Select(av => av.Id), Is.EquivalentTo(lab.MemberValueIds));
            Assert.That(_spy.PartialLoads.Single().Included, Is.Empty);
        }
    }

    /// <summary>
    /// #2836: a group held exactly as imported (a confirming Delta Import, after an export whose optimistic apply already
    /// wrote the change) is recognised as such and never asked about member by member.
    /// </summary>
    [Test]
    public async Task DeltaImport_GroupHeldAsImported_IsNotAskedAboutMemberByMemberAsync()
    {
        var lab = await ArrangeAsync();

        var activity = await RunImportAsync(lab, [[GroupImport(lab, Members(0, MemberCount - 1).Reverse())]]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.RunProfileExecutionItems, Is.Empty, "No change, so no execution item");
            Assert.That(_spy.HeldAsImported, Is.EqualTo(new[] { (lab.Group.Id, lab.Member.Id) }));
            Assert.That(_spy.Questions, Is.Empty, "Nothing is left to ask");
            Assert.That(_spy.PartialLoads.Single().Excluded, Is.EqualTo(new[] { lab.Member.Id }), "The group is loaded without its members");
            Assert.That(_spy.PartialLoads.Single().Included, Is.Empty, "... and without any of them");
            Assert.That((await StoredMembersAsync(lab)).Select(av => av.Id), Is.EquivalentTo(lab.MemberValueIds));
        }
    }

    /// <summary>
    /// #2836: whether or not the import recognises a group held exactly as imported, it holds the same values afterwards
    /// and records the same execution items and change records. Where the group is held as imported, recognising it is
    /// what spares the import asking; anywhere else the group is asked about as before.
    /// </summary>
    [TestCase(Scenario.HeldAsImported, true)]
    [TestCase(Scenario.HeldAsImportedInAnotherOrder, true)]
    [TestCase(Scenario.MembersLostAndOneGained, false)]
    [TestCase(Scenario.MemberReplacedKeepingTheCount, false)]
    [TestCase(Scenario.MemberDnCaseChanged, false)]
    [TestCase(Scenario.MemberHeldTwiceWhereAnotherIsImported, false)]
    [TestCase(Scenario.MemberStillUnresolvedHeldAsImported, true)]
    public async Task DeltaImport_RecognisingGroupsHeldAsImported_ChangesNoOutcomeAsync(Scenario scenario, bool recognised)
    {
        var without = await RunScenarioAsync(scenario, recognisesAttributesHeldAsImported: false);

        ResetLab();
        var with = await RunScenarioAsync(scenario, recognisesAttributesHeldAsImported: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(with.Outcome, Is.EqualTo(without.Outcome), "The same values held, execution items and change records");
            Assert.That(with.HeldAsImported, Is.EqualTo(recognised ? 1 : 0), "Recognised as held as imported");
            Assert.That(with.Asked, Is.EqualTo(recognised ? 0 : 1), "Asked about member by member");
            Assert.That(without.Asked, Is.EqualTo(1), "Without recognising it, always asked");
        }
    }

    public enum Scenario
    {
        HeldAsImported,
        HeldAsImportedInAnotherOrder,
        MembersLostAndOneGained,
        MemberReplacedKeepingTheCount,
        MemberDnCaseChanged,
        MemberHeldTwiceWhereAnotherIsImported,
        MemberStillUnresolvedHeldAsImported
    }

    private sealed record ScenarioRun(List<string> Outcome, int HeldAsImported, int Asked);

    private const string PendingDn = "uid=pending,ou=People,dc=glitterband,dc=local";

    /// <summary>A fresh lab, as the base fixture's set-up builds one, for a second run within one test.</summary>
    private void ResetLab()
    {
        BaseTearDown();
        BaseSetUp();
        SetUpSpy();
    }

    private async Task<ScenarioRun> RunScenarioAsync(Scenario scenario, bool recognisesAttributesHeldAsImported)
    {
        var lab = await ArrangeAsync();
        _spy.RecognisesAttributesHeldAsImported = recognisesAttributesHeldAsImported;

        var imported = scenario switch
        {
            Scenario.HeldAsImported => Members(0, MemberCount - 1).ToList(),
            Scenario.HeldAsImportedInAnotherOrder => Members(0, MemberCount - 1).Reverse().ToList(),
            Scenario.MembersLostAndOneGained => Members(2, MemberCount).ToList(),
            Scenario.MemberReplacedKeepingTheCount => Members(1, MemberCount).ToList(),
            Scenario.MemberDnCaseChanged => Members(0, MemberCount - 1).Select((dn, i) => i == 7 ? dn.ToUpperInvariant() : dn).ToList(),
            Scenario.MemberHeldTwiceWhereAnotherIsImported => Members(0, MemberCount).ToList(),
            Scenario.MemberStillUnresolvedHeldAsImported => Members(0, MemberCount - 1).Append(PendingDn).ToList(),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null)
        };

        // The group holds user 0 twice, where the directory names user 50 too: as many members as imported, not the same.
        if (scenario == Scenario.MemberHeldTwiceWhereAnotherIsImported)
            AddMemberValue(lab, UserDn(0), lab.Users[0]);

        // The group names an object JIM has not imported yet, by the DN it was imported as.
        if (scenario == Scenario.MemberStillUnresolvedHeldAsImported)
            AddMemberValue(lab, PendingDn, referenced: null);

        var activity = await RunImportAsync(lab, [[GroupImport(lab, imported)]]);
        return new ScenarioRun(DescribeOutcome(lab, activity, await StoredMembersAsync(lab)), _spy.HeldAsImported.Count, _spy.Questions.Count);
    }

    private static void AddMemberValue(Lab lab, string dn, ConnectedSystemObject? referenced)
    {
        var value = new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), AttributeId = lab.Member.Id, Attribute = lab.Member, ConnectedSystemObject = lab.Group,
            ReferenceValue = referenced, ReferenceValueId = referenced?.Id, UnresolvedReferenceValue = dn
        };
        lab.Group.AttributeValues.Add(value);
        lab.MemberValueIds.Add(value.Id);
    }

    /// <summary>
    /// What an import left, in terms two runs over separately built labs can compare: each member value held afterwards
    /// (the seeded value it is, by position, or a new one), the object it is resolved to (by user number), each execution
    /// item, and each change record's value changes.
    /// </summary>
    private List<string> DescribeOutcome(Lab lab, Activity activity, List<ConnectedSystemObjectAttributeValue> stored)
    {
        string Value(ConnectedSystemObjectAttributeValue value)
        {
            var seeded = lab.MemberValueIds.IndexOf(value.Id);
            var referenced = value.ReferenceValueId is { } id ? lab.Users.FindIndex(u => u.Id == id) : -1;
            return $"value {(seeded >= 0 ? $"seeded#{seeded}" : "new")} '{value.UnresolvedReferenceValue}' -> user#{referenced}";
        }

        var outcome = stored.Select(Value).Order(StringComparer.Ordinal).ToList();
        foreach (var item in activity.RunProfileExecutionItems)
        {
            outcome.Add($"item {item.ObjectChangeType} {item.ErrorType}");
            var change = _spy.WrittenChanges.GetValueOrDefault(item);
            outcome.AddRange((change?.AttributeChanges ?? [])
                .SelectMany(c => c.ValueChanges.Select(v => $"change {c.Attribute?.Name ?? c.AttributeName} {v.ValueChangeType} '{v.StringValue}' " +
                    $"-> user#{(v.ReferenceValueId is { } id ? lab.Users.FindIndex(u => u.Id == id) : -1)} unresolved {v.IsUnresolvedReference}"))
                .Order(StringComparer.Ordinal));
        }

        return outcome;
    }

    /// <summary>
    /// The comparison matches a member to an imported DN ignoring case through the member's object, so a DN whose case
    /// changed in the directory is no change at all, as it was when the whole group was loaded.
    /// </summary>
    [Test]
    public async Task DeltaImport_MemberDnCaseChanged_IsNoChangeAsync()
    {
        var lab = await ArrangeAsync();
        var imported = Members(0, MemberCount - 1).Select((dn, i) => i == 7 ? dn.ToUpperInvariant() : dn);

        var activity = await RunImportAsync(lab, [[GroupImport(lab, imported)]]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.RunProfileExecutionItems, Is.Empty);
            Assert.That((await StoredMembersAsync(lab)).Select(av => av.Id), Is.EquivalentTo(lab.MemberValueIds));
            Assert.That(_spy.PartialLoads, Has.Count.EqualTo(1), "Answered without loading the group whole");
        }
    }

    /// <summary>
    /// Where the database cannot be trusted to find every member the comparison would match ignoring case (it folds case
    /// differently, or the unmatched DN is not ASCII), the group is loaded whole and compared as before.
    /// </summary>
    [TestCase(false, "uid=user7,ou=People,dc=glitterband,dc=local", TestName = "DeltaImport_DatabaseFoldsCaseDifferently_LoadsTheGroupWholeAsync")]
    [TestCase(true, "uid=Zoë,ou=People,dc=glitterband,dc=local", TestName = "DeltaImport_NonAsciiDnAdded_LoadsTheGroupWholeAsync")]
    public async Task DeltaImport_AnswerNotCompleteIgnoringCase_LoadsTheGroupWholeAsync(bool databaseFolds, string addedDn)
    {
        var lab = await ArrangeAsync();
        _spy.FoldsCaseLikeIdentifierKey = databaseFolds;
        if (addedDn != UserDn(7))
            Seed(lab.ConnectedSystem, lab.UserType, Guid.NewGuid(), addedDn);
        var imported = Members(0, MemberCount - 1).Select((dn, i) => i == 7 ? dn.ToUpperInvariant() : dn).Append(addedDn).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var activity = await RunImportAsync(lab, [[GroupImport(lab, imported)]]);

        var added = activity.RunProfileExecutionItems.SelectMany(i => _spy.WrittenChanges.GetValueOrDefault(i)?.AttributeChanges ?? [])
            .SelectMany(c => c.ValueChanges).Where(v => v.ValueChangeType == ValueChangeType.Add).Select(v => v.StringValue).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Errors(activity), Is.Empty);
            Assert.That(_spy.Questions, Has.Count.EqualTo(1), "Asked");
            Assert.That(_spy.PartialLoads, Is.Empty, "But loaded whole");
            Assert.That(added, Is.EqualTo(addedDn == UserDn(7) ? Array.Empty<string>() : new[] { addedDn }));
            Assert.That((await StoredMembersAsync(lab)).Count(av => lab.MemberValueIds.Contains(av.Id)), Is.EqualTo(MemberCount), "No member removed");
        }
    }

    /// <summary>
    /// A later page's copy of a group takes the earlier page's place (#2074), on the instance the earlier page changed:
    /// that instance must be compared with the later page's answer, never with the earlier one's.
    /// </summary>
    [Test]
    public async Task DeltaImport_GroupChangedOnTwoPages_TheLaterPageDecidesWithItsOwnAnswerAsync()
    {
        var lab = await ArrangeAsync();

        var activity = await RunImportAsync(lab,
        [
            [GroupImport(lab, Members(1, MemberCount - 1))],
            [GroupImport(lab, Members(2, MemberCount - 1).Prepend(UserDn(0)))]
        ]);

        var stored = await StoredMembersAsync(lab);
        var item = activity.RunProfileExecutionItems.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Errors(activity), Is.Empty);
            Assert.That(stored.Select(av => av.Id), Is.EquivalentTo(lab.MemberValueIds.Where((_, i) => i != 1)), "Only member 1 removed, by the later page");
            Assert.That(_spy.WrittenChanges[item]!.AttributeChanges.Single().ValueChanges.Select(v => (v.ValueChangeType, v.StringValue)),
                Is.EqualTo(new[] { (ValueChangeType.Remove, UserDn(1)) }));
            Assert.That(_spy.PartialLoads, Has.Count.EqualTo(2), "Each page asks about its own copy");
        }
    }

    [Test]
    public async Task FullImport_Group_IsComparedWholeAsBeforeAsync()
    {
        var lab = await ArrangeAsync();

        var activity = await RunImportAsync(lab, [[GroupImport(lab, Members(1, MemberCount - 1)), .. lab.Users.Take(MemberCount + 1).Select(u => UserImport(lab, u))]],
            ConnectedSystemRunType.FullImport);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Errors(activity), Is.Empty);
            Assert.That((await StoredMembersAsync(lab)).Select(av => av.Id), Is.EquivalentTo(lab.MemberValueIds.Skip(1)));
            Assert.That(_spy.Questions, Is.Empty, "A Full Import asks nothing");
            Assert.That(_spy.PartialLoads, Is.Empty);
        }
    }

    #region Helpers

    private sealed record Lab(
        ConnectedSystem ConnectedSystem,
        ConnectedSystemObjectType UserType,
        ConnectedSystemObjectType GroupType,
        ConnectedSystemObjectTypeAttribute Member,
        List<ConnectedSystemObject> Users,
        List<Guid> UserGuids,
        ConnectedSystemObject Group,
        Guid GroupGuid,
        List<Guid> MemberValueIds);

    private static string UserDn(int n) => $"uid=user{n},ou=People,dc=glitterband,dc=local";

    private static IEnumerable<string> Members(int from, int to) => Enumerable.Range(from, to - from + 1).Select(UserDn);

    /// <summary>
    /// A directory of <see cref="MemberCount"/> + 1 users and a group holding the first <see cref="MemberCount"/> of them,
    /// each member resolved to its user and keeping the DN it was imported as.
    /// </summary>
    private async Task<Lab> ArrangeAsync()
    {
        var connectedSystem = await CreateConnectedSystemAsync("Directory");
        var userType = await CreateCsoTypeAsync(connectedSystem.Id, "user",
        [
            new() { Name = "objectGUID", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            new() { Name = "distinguishedName", Type = AttributeDataType.Text, IsSecondaryExternalId = true, Selected = true }
        ]);
        var groupType = await CreateCsoTypeAsync(connectedSystem.Id, "group",
        [
            new() { Name = "objectGUID", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            new() { Name = "distinguishedName", Type = AttributeDataType.Text, IsSecondaryExternalId = true, Selected = true },
            new() { Name = "member", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued, Selected = true }
        ]);

        var users = new List<ConnectedSystemObject>();
        var userGuids = new List<Guid>();
        for (var n = 0; n <= MemberCount; n++)
        {
            var guid = Guid.NewGuid();
            users.Add(Seed(connectedSystem, userType, guid, UserDn(n)));
            userGuids.Add(guid);
        }

        var groupGuid = Guid.NewGuid();
        var group = Seed(connectedSystem, groupType, groupGuid, "cn=AllStaff,ou=Groups,dc=glitterband,dc=local");
        var member = groupType.Attributes.Single(a => a.Name == "member");
        var memberValueIds = new List<Guid>();
        for (var n = 0; n < MemberCount; n++)
        {
            var value = new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(), AttributeId = member.Id, Attribute = member, ConnectedSystemObject = group,
                ReferenceValue = users[n], ReferenceValueId = users[n].Id, UnresolvedReferenceValue = UserDn(n)
            };
            group.AttributeValues.Add(value);
            memberValueIds.Add(value.Id);
        }

        return new Lab(connectedSystem, userType, groupType, member, users, userGuids, group, groupGuid, memberValueIds);
    }

    private ConnectedSystemObject Seed(ConnectedSystem connectedSystem, ConnectedSystemObjectType type, Guid guid, string dn)
    {
        var externalId = type.Attributes.Single(a => a.IsExternalId);
        var secondary = type.Attributes.Single(a => a.IsSecondaryExternalId);
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(), ConnectedSystemId = connectedSystem.Id, ConnectedSystem = connectedSystem, TypeId = type.Id, Type = type,
            ExternalIdAttributeId = externalId.Id, SecondaryExternalIdAttributeId = secondary.Id,
            Status = ConnectedSystemObjectStatus.Normal, Created = DateTime.UtcNow
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            { Id = Guid.NewGuid(), AttributeId = externalId.Id, Attribute = externalId, GuidValue = guid, ConnectedSystemObject = cso });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            { Id = Guid.NewGuid(), AttributeId = secondary.Id, Attribute = secondary, StringValue = dn, ConnectedSystemObject = cso });
        SyncRepo.SeedConnectedSystemObject(cso);
        return cso;
    }

    private static ConnectedSystemImportObject GroupImport(Lab lab, IEnumerable<string> members) => new()
    {
        ObjectType = "group",
        ChangeType = ObjectChangeType.Updated,
        Attributes =
        [
            new ConnectedSystemImportObjectAttribute { Name = "objectGUID", Type = AttributeDataType.Guid, GuidValues = [lab.GroupGuid] },
            new ConnectedSystemImportObjectAttribute { Name = "distinguishedName", Type = AttributeDataType.Text, StringValues = ["cn=AllStaff,ou=Groups,dc=glitterband,dc=local"] },
            new ConnectedSystemImportObjectAttribute { Name = "member", Type = AttributeDataType.Reference, ReferenceValues = members.ToList() }
        ]
    };

    private static ConnectedSystemImportObject UserImport(Lab lab, ConnectedSystemObject user)
    {
        var n = lab.Users.IndexOf(user);
        return new ConnectedSystemImportObject
        {
            ObjectType = "user",
            Attributes =
            [
                new ConnectedSystemImportObjectAttribute { Name = "objectGUID", Type = AttributeDataType.Guid, GuidValues = [lab.UserGuids[n]] },
                new ConnectedSystemImportObjectAttribute { Name = "distinguishedName", Type = AttributeDataType.Text, StringValues = [UserDn(n)] }
            ]
        };
    }

    private async Task<List<ConnectedSystemObjectAttributeValue>> StoredMembersAsync(Lab lab)
    {
        var stored = await SyncRepo.GetConnectedSystemObjectAsync(lab.ConnectedSystem.Id, lab.Group.Id);
        return stored!.AttributeValues.Where(av => av.AttributeId == lab.Member.Id).ToList();
    }

    private async Task<Activity> RunImportAsync(Lab lab, List<List<ConnectedSystemImportObject>> pages,
        ConnectedSystemRunType runType = ConnectedSystemRunType.DeltaImport)
    {
        var runProfile = await CreateRunProfileAsync(lab.ConnectedSystem.Id, runType.ToString(), runType);
        var activity = await CreateActivityAsync(lab.ConnectedSystem.Id, runProfile, runType);
        var workerTask = new SynchronisationWorkerTask(lab.ConnectedSystem.Id, runProfile.Id)
        {
            Id = Guid.NewGuid(),
            Status = WorkerTaskStatus.Processing,
            Activity = activity
        };

        var processor = new SyncImportTaskProcessor(
            Jim, SyncRepo, new SyncServer(Jim), new SyncEngine(),
            new PagedImportConnector(pages), lab.ConnectedSystem, runProfile, workerTask, new CancellationTokenSource());
        await processor.PerformImportAsync();
        return activity;
    }

    private static List<ActivityRunProfileExecutionItem> Errors(Activity activity) =>
        activity.RunProfileExecutionItems
            .Where(item => item.ErrorType != null && item.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet)
            .ToList();

    #endregion
}
