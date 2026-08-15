// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
using JIM.Application.Servers.Preview;
using JIM.Connectors;
using JIM.Connectors.LDAP;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Interfaces;
using JIM.Models.Preview;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// The partition and container deselection adapter (#1251): what deselecting a partition or container would do to
/// the objects JIM already holds from a Connected System.
///
/// The failure that matters is a preview that under-states a destructive change, because it is the one an
/// administrator would approve. A container deselection quietly obsoletes everything beneath it, disconnects
/// whatever those objects were joined to, and can leave Metaverse Objects with no connectors at all; a preview
/// reporting fewer objects than that, or none, is worse than no preview. Over-stating is the next worst: a preview
/// that reports impact from a selection that moved nothing stops being read.
/// </summary>
[TestFixture]
public class ConnectedSystemScopeSelectionPreviewAdapterTests
{
    private const int ConnectedSystemId = 3;
    private const int PartitionId = 11;
    private const int UsersContainerId = 21;
    private const int ContractorsContainerId = 22;
    private const int ServiceAccountsContainerId = 23;
    private const int App1ContainerId = 24;
    private const int UserObjectTypeId = 31;

    private const string UsersDn = "OU=Users,DC=example,DC=com";
    private const string ContractorsDn = "OU=Contractors,DC=example,DC=com";
    private const string ServiceAccountsDn = "OU=Service Accounts,OU=Users,DC=example,DC=com";
    private const string App1Dn = "OU=App1,OU=Service Accounts,OU=Users,DC=example,DC=com";

    private Mock<IRepository> _repo = null!;
    private Mock<IConnectedSystemRepository> _connectedSystemRepo = null!;
    private Mock<IMetaverseRepository> _metaverseRepo = null!;
    private Mock<IConnectorFactory> _connectorFactory = null!;
    private LdapConnector _connector = null!;
    private JimApplication _jim = null!;
    private ConnectedSystem _connectedSystem = null!;
    private List<ConnectedSystemObjectScopeCandidate> _candidates = null!;
    private List<MetaverseObjectDisconnectionCandidate> _disconnectionCandidates = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _repo = new Mock<IRepository>();
        _connectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _metaverseRepo = new Mock<IMetaverseRepository>();
        _repo.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepo.Object);
        _repo.Setup(r => r.Metaverse).Returns(_metaverseRepo.Object);

        // A real LDAP Connector, because containment is the thing under test and a mock of it would be a second
        // implementation of the very rule this adapter exists to defer to.
        _connector = new LdapConnector();
        _connectorFactory = new Mock<IConnectorFactory>();
        _connectorFactory.Setup(f => f.Create(It.IsAny<string>(), It.IsAny<ICredentialProtection>(), It.IsAny<ICertificateProvider>()))
            .Returns(() => _connector);

        _connectedSystem = BuildConnectedSystem();
        _candidates = [];
        _disconnectionCandidates = [];

        _connectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(ConnectedSystemId))
            .ReturnsAsync(() => _connectedSystem);
        _connectedSystemRepo.Setup(r => r.StreamConnectedSystemObjectScopeCandidates(ConnectedSystemId))
            .Returns(() => _candidates.ToAsyncEnumerable());
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemObjectCountAsync(ConnectedSystemId, It.IsAny<int?>()))
            .ReturnsAsync(() => _candidates.Count);
        _metaverseRepo.Setup(r => r.GetMetaverseObjectDisconnectionCandidatesAsync(It.IsAny<IReadOnlyCollection<Guid>>()))
            .ReturnsAsync((IReadOnlyCollection<Guid> ids) =>
                _disconnectionCandidates.Where(c => ids.Contains(c.Id)).ToList());

        _jim = new JimApplication(_repo.Object, connectorFactory: _connectorFactory.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _jim?.Dispose();
        _connector?.Dispose();
    }

    // ─── Scope transitions ───

    [Test]
    public async Task EvaluateDeltas_DeselectingAContainer_ReportsItsUnjoinedObjectsAsLeavingScopeAsync()
    {
        GivenObject($"CN=Ann,{UsersDn}");
        GivenObject($"CN=Bob,{ContractorsDn}");

        var deltas = await EvaluateAsync(SelectionOf(UsersContainerId));

        Assert.That(deltas, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(deltas[0].TransitionType,
                Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallOutOfScope));
            Assert.That(deltas[0].ObjectDisplayName, Is.EqualTo($"CN=Bob,{ContractorsDn}"));
        }
    }

    [Test]
    public async Task EvaluateDeltas_DeselectingAContainer_ReportsItsJoinedObjectsAsDisconnectingAsync()
    {
        // The distinction the confirmation turns on: an unjoined object leaving scope costs JIM nothing, a joined
        // one takes its contributed attribute values out of the Metaverse with it.
        GivenObject($"CN=Bob,{ContractorsDn}", joinedTo: Guid.CreateVersion7());

        var deltas = await EvaluateAsync(SelectionOf(UsersContainerId));

        Assert.That(deltas.Select(d => d.TransitionType), Is.EquivalentTo(new[]
        {
            ActivityRunProfileExecutionItemSyncOutcomeType.WouldDisconnectFromMetaverseObject
        }));
    }

    [Test]
    public async Task EvaluateDeltas_DeselectingAContainer_IncludesObjectsNestedBeneathItAsync()
    {
        // Selecting a container selects its subtree, so deselecting one takes the whole subtree out. An object
        // several levels down is exactly the one a suffix-blind implementation would miss.
        GivenObject($"CN=Ann,OU=Finance,OU=Contractors,DC=example,DC=com");

        var deltas = await EvaluateAsync(SelectionOf(UsersContainerId));

        Assert.That(deltas, Has.Count.EqualTo(1));
        Assert.That(deltas[0].TransitionType,
            Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallOutOfScope));
    }

    [Test]
    public async Task EvaluateDeltas_SelectingAPreviouslyDeselectedContainer_ReportsHeldObjectsAsEnteringScopeAsync()
    {
        // JIM cannot count what it has never imported, but it can count what it still holds from a scope that was
        // deselected earlier, and reporting that as newly destructive would be plainly wrong.
        GivenContractorsIsNotCurrentlySelected();
        GivenObject($"CN=Bob,{ContractorsDn}");

        var deltas = await EvaluateAsync(SelectionOf(UsersContainerId, ContractorsContainerId));

        Assert.That(deltas, Has.Count.EqualTo(1));
        Assert.That(deltas[0].TransitionType,
            Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallInScope));
    }

    // ─── Exclusions (#1255) ───

    [Test]
    public async Task EvaluateDeltas_ExcludingABranch_ReportsItsObjectsAsLeavingScopeAsync()
    {
        // The whole point of previewing an exclusion. The parent stays selected, so nothing about the tick boxes
        // says anything is leaving; the carve-out is the only thing taking these objects out, and an administrator
        // consenting to it is consenting to the obsoletion that follows.
        GivenObject($"CN=Ann,{UsersDn}");
        GivenObject($"CN=Svc,{ServiceAccountsDn}");

        var deltas = await EvaluateAsync(SelectionWithExclusions(
            [UsersContainerId, ContractorsContainerId], [ServiceAccountsContainerId]));

        Assert.That(deltas, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(deltas[0].TransitionType,
                Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallOutOfScope));
            Assert.That(deltas[0].ObjectDisplayName, Is.EqualTo($"CN=Svc,{ServiceAccountsDn}"),
                "only the object inside the carve-out leaves; its selected parent still holds everything else");
        }
    }

    [Test]
    public async Task EvaluateDeltas_ExcludingABranch_ReportsItsJoinedObjectsAsDisconnectingAsync()
    {
        GivenObject($"CN=Svc,{ServiceAccountsDn}", joinedTo: Guid.CreateVersion7());

        var deltas = await EvaluateAsync(SelectionWithExclusions(
            [UsersContainerId, ContractorsContainerId], [ServiceAccountsContainerId]));

        Assert.That(deltas.Select(d => d.TransitionType), Is.EquivalentTo(new[]
        {
            ActivityRunProfileExecutionItemSyncOutcomeType.WouldDisconnectFromMetaverseObject
        }));
    }

    [Test]
    public async Task EvaluateDeltas_LiftingAnExclusion_ReportsHeldObjectsAsEnteringScopeAsync()
    {
        // The reverse direction, and the reason an omitted exclusion list must not be read as "lift them all":
        // this is what that mistake would report, for a change nobody proposed.
        GivenServiceAccountsIsCurrentlyExcluded();
        GivenObject($"CN=Svc,{ServiceAccountsDn}");

        var deltas = await EvaluateAsync(SelectionOf(UsersContainerId, ContractorsContainerId));

        Assert.That(deltas, Has.Count.EqualTo(1));
        Assert.That(deltas[0].TransitionType,
            Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallInScope));
    }

    [Test]
    public async Task EvaluateDeltas_ReIncludingABranchInsideAnExclusion_LeavesItInScopeAsync()
    {
        // Most-specific-match, all the way down: App1 is selected beneath an excluded Service Accounts beneath a
        // selected Users. A preview that subtracted the exclusion after resolving the selections would report the
        // App1 object as leaving, and the next import would then import it.
        GivenObject($"CN=Svc,{ServiceAccountsDn}");
        GivenObject($"CN=App1Svc,{App1Dn}");

        var deltas = await EvaluateAsync(SelectionWithExclusions(
            [UsersContainerId, ContractorsContainerId, App1ContainerId], [ServiceAccountsContainerId]));

        Assert.That(deltas, Has.Count.EqualTo(1));
        Assert.That(deltas[0].ObjectDisplayName, Is.EqualTo($"CN=Svc,{ServiceAccountsDn}"),
            "the re-included branch is the most specific statement over its own objects, so they stay");
    }

    [Test]
    public async Task EvaluateDeltas_ExclusionUnchanged_ReportsNothingAsync()
    {
        // Restating the stored exclusion is not a change, and a preview that reported one would make every save
        // from the Partitions tab look destructive.
        GivenServiceAccountsIsCurrentlyExcluded();
        GivenObject($"CN=Ann,{UsersDn}");
        GivenObject($"CN=Svc,{ServiceAccountsDn}");

        var deltas = await EvaluateAsync(SelectionWithExclusions(
            [UsersContainerId, ContractorsContainerId], [ServiceAccountsContainerId]));

        Assert.That(deltas, Is.Empty);
    }

    [Test]
    public async Task EvaluateDeltas_SelectionUnchanged_ReportsNothingAsync()
    {
        GivenObject($"CN=Ann,{UsersDn}");
        GivenObject($"CN=Bob,{ContractorsDn}");

        var deltas = await EvaluateAsync(SelectionOf(UsersContainerId));

        // Ann is in scope before and after; only Bob moves, and re-proposing the current selection moves nobody.
        var unchanged = await EvaluateAsync(SelectionOf(UsersContainerId, ContractorsContainerId));
        Assert.That(unchanged.Select(d => d.TransitionType),
            Does.Not.Contain(ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallOutOfScope));
        Assert.That(deltas, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task EvaluateDeltas_DeselectingThePartition_TakesEveryObjectInItOutOfScopeAsync()
    {
        GivenObject($"CN=Ann,{UsersDn}");
        GivenObject($"CN=Bob,{ContractorsDn}");

        var deltas = await EvaluateAsync(new ConnectedSystemScopeSelectionProposal([], []));

        Assert.That(deltas.Where(d =>
            d.TransitionType == ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallOutOfScope), Has.Exactly(2).Items);
    }

    [Test]
    public async Task EvaluateDeltas_ObjectWithNoPartition_IsNotCountedEitherWayAsync()
    {
        // Rows predating partition tracking cannot be attributed to any selection. Counting them as leaving would
        // overstate a destructive change; counting them as staying would hide one.
        _candidates.Add(new ConnectedSystemObjectScopeCandidate(
            Guid.CreateVersion7(), "User", PartitionId: null, $"CN=Ann,{UsersDn}", MetaverseObjectId: null));

        var deltas = await EvaluateAsync(new ConnectedSystemScopeSelectionProposal([], []));

        Assert.That(deltas, Is.Empty);
    }

    [Test]
    public async Task EvaluateDeltas_ObjectBeneathAOneLevelContainer_IsNotCountedAsLeavingScopeAsync()
    {
        // Container Scope (#351): a One Level container imports only what sits directly within it, so an object a
        // level deeper is already out of scope and deselecting the container takes nothing further away. Counting
        // it would overstate a destructive change on the strength of a Distinguished Name that merely looks nested.
        GivenUsersIsOneLevel();
        GivenObject($"CN=Ann,OU=Finance,{UsersDn}");

        var deltas = await EvaluateAsync(new ConnectedSystemScopeSelectionProposal([PartitionId], []));

        Assert.That(deltas, Is.Empty);
    }

    [Test]
    public async Task EvaluateDeltas_ObjectDirectlyWithinAOneLevelContainer_IsCountedAsLeavingScopeAsync()
    {
        GivenUsersIsOneLevel();
        GivenObject($"CN=Ann,{UsersDn}");

        var deltas = await EvaluateAsync(new ConnectedSystemScopeSelectionProposal([PartitionId], []));

        Assert.That(deltas, Has.Count.EqualTo(1));
        Assert.That(deltas[0].TransitionType,
            Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallOutOfScope));
    }

    // ─── Metaverse consequences ───

    [Test]
    public async Task EvaluateDeltas_DisconnectingTheLastConnector_ReportsTheMetaverseObjectAsDeletionEligibleAsync()
    {
        var metaverseObjectId = Guid.CreateVersion7();
        GivenObject($"CN=Bob,{ContractorsDn}", joinedTo: metaverseObjectId);
        GivenMetaverseObject(metaverseObjectId, "Bob Smith", joinedSystemIds: [ConnectedSystemId]);

        var deltas = await EvaluateAsync(SelectionOf(UsersContainerId));

        var eligible = deltas.Single(d =>
            d.TransitionType == ActivityRunProfileExecutionItemSyncOutcomeType.WouldBecomeDeletionEligible);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(eligible.MetaverseObjectId, Is.EqualTo(metaverseObjectId));
            Assert.That(eligible.ObjectDisplayName, Is.EqualTo("Bob Smith"));
        }
    }

    [Test]
    public async Task EvaluateDeltas_MetaverseObjectKeepsAnotherConnector_IsNotReportedAsDeletionEligibleAsync()
    {
        var metaverseObjectId = Guid.CreateVersion7();
        GivenObject($"CN=Bob,{ContractorsDn}", joinedTo: metaverseObjectId);
        GivenMetaverseObject(metaverseObjectId, "Bob Smith", joinedSystemIds: [ConnectedSystemId, 99]);

        var deltas = await EvaluateAsync(SelectionOf(UsersContainerId));

        Assert.That(deltas.Select(d => d.TransitionType),
            Does.Not.Contain(ActivityRunProfileExecutionItemSyncOutcomeType.WouldBecomeDeletionEligible));
    }

    [Test]
    public async Task EvaluateDeltas_MetaverseObjectKeepsASecondConnectorInTheSameSystem_IsNotReportedAsDeletionEligibleAsync()
    {
        // The subtle one. Both objects are in this system, but only the one in the deselected container leaves, so
        // the Metaverse Object still has a connector here. Removing the system wholesale from the remaining list
        // would report a deletion that would not happen.
        var metaverseObjectId = Guid.CreateVersion7();
        GivenObject($"CN=Bob,{ContractorsDn}", joinedTo: metaverseObjectId);
        GivenObject($"CN=Bob (admin),{UsersDn}", joinedTo: metaverseObjectId);
        GivenMetaverseObject(metaverseObjectId, "Bob Smith", joinedSystemIds: [ConnectedSystemId, ConnectedSystemId]);

        var deltas = await EvaluateAsync(SelectionOf(UsersContainerId));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deltas.Count(d =>
                d.TransitionType == ActivityRunProfileExecutionItemSyncOutcomeType.WouldDisconnectFromMetaverseObject),
                Is.EqualTo(1));
            Assert.That(deltas.Select(d => d.TransitionType),
                Does.Not.Contain(ActivityRunProfileExecutionItemSyncOutcomeType.WouldBecomeDeletionEligible));
        }
    }

    [Test]
    public async Task EvaluateDeltas_MetaverseObjectTypeDeletesManually_IsNotReportedAsDeletionEligibleAsync()
    {
        var metaverseObjectId = Guid.CreateVersion7();
        GivenObject($"CN=Bob,{ContractorsDn}", joinedTo: metaverseObjectId);
        GivenMetaverseObject(metaverseObjectId, "Bob Smith", joinedSystemIds: [ConnectedSystemId],
            deletionRule: MetaverseObjectDeletionRule.Manual);

        var deltas = await EvaluateAsync(SelectionOf(UsersContainerId));

        Assert.That(deltas.Select(d => d.TransitionType),
            Does.Not.Contain(ActivityRunProfileExecutionItemSyncOutcomeType.WouldBecomeDeletionEligible));
    }

    // ─── Counts ───

    [Test]
    public async Task CountImpact_AgreesWithTheDeltasItWouldDrillIntoAsync()
    {
        // The invariant that makes a preview trustworthy: the number an administrator consents to and the rows they
        // can inspect are produced by one evaluation, so they cannot disagree.
        GivenObject($"CN=Ann,{ContractorsDn}");
        GivenObject($"CN=Bob,{ContractorsDn}", joinedTo: Guid.CreateVersion7());

        var context = Context(SelectionOf(UsersContainerId));
        var counts = await NewAdapter().CountImpactAsync(context);
        var deltas = await EvaluateAsync(SelectionOf(UsersContainerId));

        foreach (var count in counts)
            Assert.That(deltas.Count(d => d.TransitionType == count.TransitionType), Is.EqualTo(count.ObjectCount),
                $"count for {count.TransitionType} disagrees with the deltas");

        Assert.That(counts.Sum(c => c.ObjectCount), Is.EqualTo(deltas.Count));
    }

    // ─── Validation ───

    [Test]
    public async Task Validate_SelectionLeavingNothingManaged_WarnsAsync()
    {
        var findings = await NewAdapter().ValidateAsync(Context(new ConnectedSystemScopeSelectionProposal([], [])));

        Assert.That(findings.Any(f => f.Severity == PreviewValidationSeverity.Warning &&
                                      f.Message.Contains("nothing for JIM to manage")), Is.True);
    }

    [Test]
    public async Task Validate_RunProfileTargetingADeselectedPartition_IsNamedAsync()
    {
        _connectedSystem.RunProfiles!.Add(new ConnectedSystemRunProfile
        {
            Id = 5,
            Name = "Full Import (Europe)",
            Partition = _connectedSystem.Partitions![0]
        });

        var findings = await NewAdapter().ValidateAsync(Context(new ConnectedSystemScopeSelectionProposal([], [])));

        Assert.That(findings.Any(f => f.Severity == PreviewValidationSeverity.Warning &&
                                      f.Message.Contains("Full Import (Europe)")), Is.True);
    }

    [Test]
    public async Task Validate_SelectingScopeThatIsNotCurrentlyImported_SaysWhatCannotBeCountedAsync()
    {
        // JIM cannot count objects it has never imported, and a preview that quietly reported zero would read as
        // "this change would bring nothing in".
        GivenContractorsIsNotCurrentlySelected();

        var findings = await NewAdapter().ValidateAsync(Context(SelectionOf(UsersContainerId, ContractorsContainerId)));

        Assert.That(findings.Any(f => f.Severity == PreviewValidationSeverity.Information &&
                                      f.Message.Contains("never imported")), Is.True);
    }

    [Test]
    public async Task Validate_LiftingAnExclusion_SaysWhatCannotBeCountedAsync()
    {
        // Lifting a carve-out brings scope in exactly as ticking a Container does, and the same caveat applies:
        // JIM can count what it still holds from the branch, and cannot count what it has never imported. The
        // finding must not turn on whether the new scope arrived by a tick box or by an exclusion being removed.
        GivenServiceAccountsIsCurrentlyExcluded();

        var findings = await NewAdapter().ValidateAsync(Context(SelectionOf(UsersContainerId, ContractorsContainerId)));

        Assert.That(findings.Any(f => f.Severity == PreviewValidationSeverity.Information &&
                                      f.Message.Contains("never imported")), Is.True);
    }

    [Test]
    public async Task Validate_ConnectorWithoutPartitions_BlocksAsync()
    {
        _connectedSystem.ConnectorDefinition.SupportsPartitions = false;

        var findings = await NewAdapter().ValidateAsync(Context(SelectionOf(UsersContainerId)));

        Assert.That(findings.Single().Severity, Is.EqualTo(PreviewValidationSeverity.Blocking));
    }

    // ─── Fixture helpers ───

    private ConnectedSystemScopeSelectionPreviewAdapter NewAdapter() => new(_jim, new SyncEngine());

    private PreviewContext Context(ConnectedSystemScopeSelectionProposal proposal) => new()
    {
        Surface = ConfigurationChangePreviewSurface.ConnectedSystem,
        ActivityId = Guid.CreateVersion7(),
        TargetId = ConnectedSystemId,
        ProposedConfiguration = proposal
    };

    /// <summary>
    /// A proposal keeping the one partition and selecting the named containers. The saved state selects both
    /// containers, so naming fewer of them is a deselection.
    /// </summary>
    private static ConnectedSystemScopeSelectionProposal SelectionOf(params int[] containerIds) =>
        new([PartitionId], containerIds);

    /// <summary>
    /// A proposal keeping the one partition, selecting the named containers and carving out the named exclusions.
    /// </summary>
    private static ConnectedSystemScopeSelectionProposal SelectionWithExclusions(int[] containerIds, int[] excludedContainerIds) =>
        new([PartitionId], containerIds, excludedContainerIds);

    /// <summary>
    /// Takes the Contractors container out of the saved selection, so a proposal naming it is a selection rather
    /// than a deselection. The fixture manages everything by default because that makes a shorter proposal
    /// unambiguously destructive, which is the direction most of these tests care about.
    /// </summary>
    private void GivenContractorsIsNotCurrentlySelected() =>
        _connectedSystem.Partitions![0].Containers!.Single(c => c.Id == ContractorsContainerId).Selected = false;

    /// <summary>
    /// Puts the stored carve-out of Service Accounts in force, so a proposal omitting it is lifting an exclusion
    /// rather than making one.
    /// </summary>
    private void GivenServiceAccountsIsCurrentlyExcluded() =>
        _connectedSystem.Partitions![0].Containers!
            .Single(c => c.Id == UsersContainerId).ChildContainers
            .Single(c => c.Id == ServiceAccountsContainerId).Excluded = true;

    /// <summary>
    /// Narrows the Users container to One Level, so only objects directly within it are in import scope.
    /// </summary>
    private void GivenUsersIsOneLevel() =>
        _connectedSystem.Partitions![0].Containers!.Single(c => c.Id == UsersContainerId).Scope =
            ConnectedSystemContainerScope.OneLevel;

    private void GivenObject(string distinguishedName, Guid? joinedTo = null) =>
        _candidates.Add(new ConnectedSystemObjectScopeCandidate(
            Guid.CreateVersion7(), "User", PartitionId, distinguishedName, joinedTo));

    private void GivenMetaverseObject(Guid id, string displayName, int[] joinedSystemIds,
        MetaverseObjectDeletionRule deletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected) =>
        _disconnectionCandidates.Add(new MetaverseObjectDisconnectionCandidate(
            id, displayName, UserObjectTypeId, "User", MetaverseObjectOrigin.Projected,
            deletionRule, AuthoritativeSourceTriggerMode.AllSourcesDisconnect, TimeSpan.FromDays(7),
            [], joinedSystemIds));

    private async Task<List<PreviewDelta>> EvaluateAsync(ConnectedSystemScopeSelectionProposal proposal)
    {
        var deltas = new List<PreviewDelta>();
        await foreach (var delta in NewAdapter().EvaluateDeltasAsync(Context(proposal), CancellationToken.None))
            deltas.Add(delta);

        return deltas;
    }

    /// <summary>
    /// A directory-shaped Connected System with one partition and two sibling containers, both selected. Every test
    /// starts from "everything is managed" so that a proposal naming fewer containers is unambiguously a
    /// deselection.
    /// </summary>
    private static ConnectedSystem BuildConnectedSystem()
    {
        var partition = new ConnectedSystemPartition
        {
            Id = PartitionId,
            Name = "example.com",
            ExternalId = "DC=example,DC=com",
            Selected = true,
            Containers = []
        };

        var users = new ConnectedSystemContainer
        {
            Id = UsersContainerId,
            Name = "Users",
            ExternalId = UsersDn,
            Selected = true
        };

        // A branch inside the selected one, stating nothing by default, and a branch inside that. Between them
        // they carry the exclusion cases: carving out, lifting the carve-out, and re-including beneath it.
        var serviceAccounts = new ConnectedSystemContainer
        {
            Id = ServiceAccountsContainerId,
            Name = "Service Accounts",
            ExternalId = ServiceAccountsDn
        };

        serviceAccounts.ChildContainers.Add(new ConnectedSystemContainer
        {
            Id = App1ContainerId,
            Name = "App1",
            ExternalId = App1Dn
        });

        users.ChildContainers.Add(serviceAccounts);
        partition.Containers.Add(users);

        partition.Containers.Add(new ConnectedSystemContainer
        {
            Id = ContractorsContainerId,
            Name = "Contractors",
            ExternalId = ContractorsDn,
            Selected = true
        });

        return new ConnectedSystem
        {
            Id = ConnectedSystemId,
            Name = "Example Directory",
            ConnectorDefinition = new ConnectorDefinition
            {
                Name = "JIM LDAP Connector",
                SupportsPartitions = true,
                SupportsPartitionContainers = true
            },
            Partitions = [partition]
        };
    }
}
