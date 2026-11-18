// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Reflection;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Worker.Processors;
using Moq;

namespace JIM.Worker.Tests.Processors;

/// <summary>
/// #2986: the reference comparison reads a referenced object's External ID only where the DN a held value kept does not
/// match an imported value exactly. Where every held value's kept DN is among the imported values, and every imported value
/// is among the kept DNs, the External IDs cannot change what it stages, so the import leaves that attribute out of the
/// lookup. A confirming Full Import finds every group in that state; at 500,000 users the lookup cost 20 s.
/// </summary>
[TestFixture]
public class ReferenceAttributesSettledByHeldDnsTests
{
    private const int ObjectGuidAttributeId = 1;
    private const int MemberAttributeId = 7;
    private const int ManagerAttributeId = 8;

    private static readonly MethodInfo UpdateMethod = typeof(SyncImportTaskProcessor).GetMethod(
        "UpdateConnectedSystemObjectFromImportObject", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("UpdateConnectedSystemObjectFromImportObject not found via reflection.");

    private ConnectedSystemObjectType _groupType = null!;
    private List<Guid> _users = null!;

    [SetUp]
    public void SetUp()
    {
        _groupType = new ConnectedSystemObjectType
        {
            Id = 2,
            Name = "group",
            Attributes =
            [
                new ConnectedSystemObjectTypeAttribute { Id = ObjectGuidAttributeId, Name = "objectGUID", Type = AttributeDataType.Guid, IsExternalId = true },
                new ConnectedSystemObjectTypeAttribute { Id = MemberAttributeId, Name = "member", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued },
                new ConnectedSystemObjectTypeAttribute { Id = ManagerAttributeId, Name = "managedBy", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.SingleValued }
            ]
        };
        _users = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToList();
    }

    private static string Dn(int n) => $"uid=user{n},ou=People,dc=glitterband,dc=local";

    private static IEnumerable<string> Dns(int from, int to) => Enumerable.Range(from, to - from + 1).Select(Dn);

    #region Which attributes are settled

    [Test]
    public void FindReferenceAttributesSettledByHeldDns_HeldDnsAreTheImportedOnesInAnotherOrder_IsSettled()
    {
        var group = GroupHolding(members: [0, 1, 2, 3]);

        var settled = SyncImportTaskProcessor.FindReferenceAttributesSettledByHeldDns(GroupImport(Dns(0, 3).Reverse()), group, _groupType);

        Assert.That(settled, Is.EquivalentTo(new[] { MemberAttributeId }));
    }

    [Test]
    public void FindReferenceAttributesSettledByHeldDns_SingleValuedReferenceHeldAsImported_IsSettled()
    {
        var group = GroupHolding(members: [0, 1], manager: 5);

        var settled = SyncImportTaskProcessor.FindReferenceAttributesSettledByHeldDns(GroupImport(Dns(0, 1), manager: Dn(5)), group, _groupType);

        Assert.That(settled, Is.EquivalentTo(new[] { MemberAttributeId, ManagerAttributeId }));
    }

    [Test]
    public void FindReferenceAttributesSettledByHeldDns_HeldMemberStillUnresolved_IsSettledWhereTheResolvedOnesAre()
    {
        var group = GroupHolding(members: [0, 1]);
        AddMember(group, "uid=pending,ou=People,dc=glitterband,dc=local", referenced: null);

        var settled = SyncImportTaskProcessor.FindReferenceAttributesSettledByHeldDns(
            GroupImport(Dns(0, 1).Append("uid=pending,ou=People,dc=glitterband,dc=local")), group, _groupType);

        Assert.That(settled, Is.EquivalentTo(new[] { MemberAttributeId }));
    }

    private static IEnumerable<TestCaseData> NotSettledCases()
    {
        yield return new TestCaseData(Dns(1, 3).ToList()).SetName("FindReferenceAttributesSettledByHeldDns_MemberRemoved_IsNotSettled");
        yield return new TestCaseData(Dns(0, 4).ToList()).SetName("FindReferenceAttributesSettledByHeldDns_MemberAdded_IsNotSettled");
        yield return new TestCaseData(Dns(0, 2).Append(Dn(3).ToUpperInvariant()).ToList()).SetName("FindReferenceAttributesSettledByHeldDns_CaseOnlyDnChange_IsNotSettled");
        yield return new TestCaseData(Dns(0, 3).Append(Dn(3).ToUpperInvariant()).ToList())
            .SetName("FindReferenceAttributesSettledByHeldDns_ImportedValueRepeatedIgnoringCase_IsNotSettled");
        yield return new TestCaseData(new List<string>()).SetName("FindReferenceAttributesSettledByHeldDns_NoMembersImported_IsNotSettled");
    }

    [TestCaseSource(nameof(NotSettledCases))]
    public void FindReferenceAttributesSettledByHeldDns_HeldAndImportedDnsDiffer_IsNotSettled(List<string> imported)
    {
        var group = GroupHolding(members: [0, 1, 2, 3]);

        var settled = SyncImportTaskProcessor.FindReferenceAttributesSettledByHeldDns(GroupImport(imported), group, _groupType);

        Assert.That(settled, Is.Empty);
    }

    [Test]
    public void FindReferenceAttributesSettledByHeldDns_HeldValueKeptNoDn_IsNotSettled()
    {
        var group = GroupHolding(members: [0, 1]);
        group.AttributeValues.First(av => av.AttributeId == MemberAttributeId).UnresolvedReferenceValue = null;

        var settled = SyncImportTaskProcessor.FindReferenceAttributesSettledByHeldDns(GroupImport(Dns(0, 1)), group, _groupType);

        Assert.That(settled, Is.Empty);
    }

    [Test]
    public void FindReferenceAttributesSettledByHeldDns_ImportCarriesTheAttributeTwice_IsNotSettled()
    {
        var group = GroupHolding(members: [0, 1]);
        var import = GroupImport(Dns(0, 1));
        import.Attributes.Add(new ConnectedSystemImportObjectAttribute { Name = "member", Type = AttributeDataType.Reference, ReferenceValues = Dns(0, 1).ToList() });

        var settled = SyncImportTaskProcessor.FindReferenceAttributesSettledByHeldDns(import, group, _groupType);

        Assert.That(settled, Is.Empty);
    }

    [Test]
    public void FindReferenceAttributesSettledByHeldDns_AttributeLoadedInPart_IsNotNamed()
    {
        // Loaded in part, the held values are not all it holds, so they cannot show the attribute settled; it is left out
        // of the lookup for that reason already.
        var group = GroupHolding(members: [0, 1]);
        group.PartiallyLoadedAttributes = new Dictionary<int, PartiallyLoadedAttribute>
        {
            [MemberAttributeId] = new ExportPartiallyLoadedAttribute(MemberAttributeId, "member", AtLeast: 5)
        };

        var settled = SyncImportTaskProcessor.FindReferenceAttributesSettledByHeldDns(GroupImport(Dns(0, 1)), group, _groupType);

        Assert.That(settled, Is.Empty);
    }

    [Test]
    public void FindReferenceAttributesSettledByHeldDns_AttributeHoldsNoResolvedReference_IsNotNamed()
    {
        // Nothing the lookup would read, so nothing to leave out of it.
        var group = GroupHolding(members: []);
        AddMember(group, "uid=pending,ou=People,dc=glitterband,dc=local", referenced: null);

        var settled = SyncImportTaskProcessor.FindReferenceAttributesSettledByHeldDns(
            GroupImport(["uid=pending,ou=People,dc=glitterband,dc=local"]), group, _groupType);

        Assert.That(settled, Is.Empty);
    }

    #endregion

    #region The lookup leaves settled attributes out

    [Test]
    public async Task GetReferenceExternalIdsForComparisonAsync_EveryResolvedReferenceSettled_AsksTheDatabaseNothingAsync()
    {
        var syncRepo = new Mock<ISyncRepository>(MockBehavior.Strict);
        var group = GroupHolding(members: [0, 1, 2]);

        var result = await SyncImportTaskProcessor.GetReferenceExternalIdsForComparisonAsync(syncRepo.Object, [group],
            new Dictionary<Guid, IReadOnlySet<int>> { [group.Id] = new HashSet<int> { MemberAttributeId } });

        Assert.That(result[group.Id], Is.Empty);
    }

    [Test]
    public async Task GetReferenceExternalIdsForComparisonAsync_OneAttributeSettled_AsksWithoutItAsync()
    {
        var group = GroupHolding(members: [0, 1, 2], manager: 5);
        var calls = new List<(List<Guid> Ids, List<int>? Excluded)>();
        var syncRepo = new Mock<ISyncRepository>(MockBehavior.Strict);
        syncRepo.Setup(r => r.GetReferenceExternalIdsForCsosAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<IReadOnlyCollection<int>?>()))
            .Callback<IReadOnlyCollection<Guid>, IReadOnlyCollection<int>?>((ids, excluded) => calls.Add((ids.ToList(), excluded?.ToList())))
            .ReturnsAsync(new Dictionary<Guid, Dictionary<Guid, string>> { [group.Id] = new() { [_users[5]] = Dn(5) } });

        var result = await SyncImportTaskProcessor.GetReferenceExternalIdsForComparisonAsync(syncRepo.Object, [group],
            new Dictionary<Guid, IReadOnlySet<int>> { [group.Id] = new HashSet<int> { MemberAttributeId } });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(calls, Has.Count.EqualTo(1));
            Assert.That(calls[0].Ids, Is.EqualTo(new[] { group.Id }));
            Assert.That(calls[0].Excluded, Is.EqualTo(new[] { MemberAttributeId }));
            Assert.That(result[group.Id], Is.EquivalentTo(new Dictionary<Guid, string> { [_users[5]] = Dn(5) }));
        }
    }

    [Test]
    public async Task GetReferenceExternalIdsForComparisonAsync_ObjectsLeaveOutDifferentAttributes_AsksForEachSetSeparatelyAsync()
    {
        var settledGroup = GroupHolding(members: [0, 1], manager: 5);
        var changedGroup = GroupHolding(members: [2, 3], manager: 6);
        var calls = new List<(List<Guid> Ids, List<int> Excluded)>();
        var syncRepo = new Mock<ISyncRepository>(MockBehavior.Strict);
        syncRepo.Setup(r => r.GetReferenceExternalIdsForCsosAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<IReadOnlyCollection<int>?>()))
            .Callback<IReadOnlyCollection<Guid>, IReadOnlyCollection<int>?>((ids, excluded) => calls.Add((ids.ToList(), excluded?.ToList() ?? [])))
            .ReturnsAsync((IReadOnlyCollection<Guid> ids, IReadOnlyCollection<int>? _) => ids.ToDictionary(id => id, _ => new Dictionary<Guid, string>()));

        await SyncImportTaskProcessor.GetReferenceExternalIdsForComparisonAsync(syncRepo.Object, [settledGroup, changedGroup],
            new Dictionary<Guid, IReadOnlySet<int>> { [settledGroup.Id] = new HashSet<int> { MemberAttributeId } });

        Assert.That(calls.Select(c => (c.Ids.Single(), string.Join(",", c.Excluded))),
            Is.EquivalentTo(new[] { (settledGroup.Id, MemberAttributeId.ToString()), (changedGroup.Id, "") }));
    }

    #endregion

    #region Leaving a settled attribute out changes nothing the comparison stages

    private static IEnumerable<TestCaseData> SettledCases()
    {
        yield return new TestCaseData(new[] { 0, 1, 2, 3 }, Dns(0, 3).ToList(), false).SetName("Comparison_SettledAttributeLeftOutOfTheLookup_HeldAsImported_StagesTheSame");
        yield return new TestCaseData(new[] { 0, 1, 2, 3 }, Dns(0, 3).Reverse().ToList(), false).SetName("Comparison_SettledAttributeLeftOutOfTheLookup_InAnotherOrder_StagesTheSame");
        yield return new TestCaseData(new[] { 0, 1, 1, 2 }, Dns(0, 2).ToList(), false).SetName("Comparison_SettledAttributeLeftOutOfTheLookup_MemberHeldTwice_StagesTheSame");
        yield return new TestCaseData(new[] { 0, 1 }, Dns(0, 1).Append("uid=pending,ou=People,dc=glitterband,dc=local").ToList(), true)
            .SetName("Comparison_SettledAttributeLeftOutOfTheLookup_MemberStillUnresolved_StagesTheSame");
    }

    [TestCaseSource(nameof(SettledCases))]
    public void Comparison_SettledAttributeLeftOutOfTheLookup_StagesTheSame(int[] members, List<string> imported, bool withPending)
    {
        Group Arrange()
        {
            var group = GroupHolding(members, manager: 9);
            if (withPending)
                AddMember(group, "uid=pending,ou=People,dc=glitterband,dc=local", referenced: null);
            return new Group(group, GroupImport(imported, manager: "uid=renamed9,ou=People,dc=glitterband,dc=local"));
        }

        // As before: every referenced object's External ID, the manager's renamed in the directory since it was held.
        var whole = Arrange();
        var settled = SyncImportTaskProcessor.FindReferenceAttributesSettledByHeldDns(whole.Import, whole.Object, _groupType);
        var everyExternalId = _users.Select((id, n) => (id, n)).ToDictionary(p => p.id, p => p.n == 9 ? "uid=renamed9,ou=People,dc=glitterband,dc=local" : Dn(p.n));
        Compare(whole, everyExternalId);

        // With the settled attribute left out of the lookup: only the manager's External ID.
        var trimmed = Arrange();
        Compare(trimmed, new Dictionary<Guid, string> { [_users[9]] = "uid=renamed9,ou=People,dc=glitterband,dc=local" });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(settled, Is.EquivalentTo(new[] { MemberAttributeId }), "precondition: the members are settled, the manager is not");
            // Each arrangement builds its own value ids, so values are compared by what they hold.
            Assert.That(trimmed.Object.PendingAttributeValueRemovals.Select(Describe), Is.EquivalentTo(whole.Object.PendingAttributeValueRemovals.Select(Describe)),
                "The same values removed");
            Assert.That(trimmed.Object.PendingAttributeValueAdditions.Select(Describe), Is.EqualTo(whole.Object.PendingAttributeValueAdditions.Select(Describe)),
                "The same values added, in the same order");
            Assert.That(whole.Object.PendingAttributeValueRemovals.Concat(whole.Object.PendingAttributeValueAdditions).All(v => v.AttributeId != MemberAttributeId && v.Attribute?.Id != MemberAttributeId),
                Is.True, "No member staged either way");
        }
    }

    /// <summary>
    /// The comparison warns of a resolved reference the lookup has no External ID for, which it could remove and add again.
    /// A value whose kept DN matches an imported value exactly is never removed that way, so it is no cause to warn: a
    /// settled attribute left out of the lookup must not warn of every member.
    /// </summary>
    [Test]
    [NonParallelizable]
    public void Comparison_SettledAttributeLeftOutOfTheLookup_WarnsOfNoMissingExternalId()
    {
        var group = new Group(GroupHolding(members: [0, 1, 2, 3]), GroupImport(Dns(0, 3)));
        var sink = new CapturingSink();
        var original = Serilog.Log.Logger;
        Serilog.Log.Logger = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        try
        {
            Compare(group, new Dictionary<Guid, string>());
        }
        finally
        {
            Serilog.Log.Logger = original;
        }

        Assert.That(sink.Messages.Where(m => m.Contains("reference external id dictionary")), Is.Empty);
    }

    private sealed class CapturingSink : Serilog.Core.ILogEventSink
    {
        public List<string> Messages { get; } = new();

        public void Emit(Serilog.Events.LogEvent logEvent) => Messages.Add(logEvent.RenderMessage());
    }

    private sealed record Group(ConnectedSystemObject Object, ConnectedSystemImportObject Import);

    private void Compare(Group group, IReadOnlyDictionary<Guid, string> lookup) =>
        UpdateMethod.Invoke(null, [group.Import, group.Object, _groupType, new ActivityRunProfileExecutionItem(), lookup]);

    private static string Describe(ConnectedSystemObjectAttributeValue value) =>
        $"{value.Attribute?.Id ?? value.AttributeId} '{value.UnresolvedReferenceValue}' {value.ReferenceValueId}";

    #endregion

    #region Helpers

    private ConnectedSystemObject GroupHolding(IEnumerable<int> members, int? manager = null)
    {
        var group = new ConnectedSystemObject { Id = Guid.NewGuid(), Type = _groupType, TypeId = _groupType.Id, ExternalIdAttributeId = ObjectGuidAttributeId };
        group.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), AttributeId = ObjectGuidAttributeId, Attribute = _groupType.Attributes[0], GuidValue = Guid.NewGuid(), ConnectedSystemObject = group
        });
        foreach (var n in members)
            AddMember(group, Dn(n), _users[n]);
        if (manager is { } m)
        {
            group.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(), AttributeId = ManagerAttributeId, Attribute = _groupType.Attributes[2], ReferenceValueId = _users[m],
                UnresolvedReferenceValue = Dn(m), ConnectedSystemObject = group
            });
        }

        return group;
    }

    private void AddMember(ConnectedSystemObject group, string dn, Guid? referenced) =>
        group.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), AttributeId = MemberAttributeId, Attribute = _groupType.Attributes[1], ReferenceValueId = referenced,
            UnresolvedReferenceValue = dn, ConnectedSystemObject = group
        });

    private static ConnectedSystemImportObject GroupImport(IEnumerable<string> members, string? manager = null)
    {
        var import = new ConnectedSystemImportObject
        {
            ObjectType = "group",
            ChangeType = ObjectChangeType.Updated,
            Attributes = [new ConnectedSystemImportObjectAttribute { Name = "member", Type = AttributeDataType.Reference, ReferenceValues = members.ToList() }]
        };
        if (manager != null)
            import.Attributes.Add(new ConnectedSystemImportObjectAttribute { Name = "managedBy", Type = AttributeDataType.Reference, ReferenceValues = [manager] });
        return import;
    }

    #endregion
}
