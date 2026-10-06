// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.UniqueValues;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using NUnit.Framework;
using InMemorySyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests.UniqueValues;

/// <summary>
/// The Collision Remediation part of <see cref="UniqueValueGenerationServer"/> (Unique Value Generation, #242,
/// release 4): drawing the next candidate for a value a target rejected, anchoring (plan decision 10), and the Needs
/// Decision lifecycle with its exits (plan decisions 11 and 12).
/// </summary>
[TestFixture]
public class UniqueValueGenerationServerCollisionRemediationTests
{
    // ---- Drawing the next candidate ----

    [Test]
    public async Task RegenerateAsync_RejectedBaseValue_DrawsTheNextCandidateAsync()
    {
        var repo = new InMemorySyncRepository();
        var server = new UniqueValueGenerationServer(repo);
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var request = UniqueValueTestHelpers.ImportRequest(UniqueValueTestHelpers.Generation(), attributeId, Guid.NewGuid(), baseValue: "joe.bloggs")
            with { RejectedValues = ["joe.bloggs"] };

        var outcome = await server.RegenerateAsync(request, UniqueValueTestHelpers.Options());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.Kind, Is.EqualTo(GenerationOutcomeKind.Generated));
            Assert.That(outcome.Value, Is.EqualTo("joe.bloggs1"), "the rejected value is taken, whatever JIM's own records say");
        }
    }

    [Test]
    public async Task RegenerateAsync_RejectedValueDiffersOnlyInCase_IsStillSkippedAsync()
    {
        var repo = new InMemorySyncRepository();
        var server = new UniqueValueGenerationServer(repo);
        var request = UniqueValueTestHelpers.ImportRequest(UniqueValueTestHelpers.Generation(), UniqueValueTestHelpers.NextAttributeId(), Guid.NewGuid(), baseValue: "joe.bloggs")
            with { RejectedValues = ["Joe.Bloggs"] };

        var outcome = await server.RegenerateAsync(request, UniqueValueTestHelpers.Options());

        Assert.That(outcome.Value, Is.EqualTo("joe.bloggs1"));
    }

    [Test]
    public async Task RegenerateAsync_NextCandidateHeldByAnotherObject_SkipsItAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        repo.SeedMetaverseObject(MetaverseObjectHolding(attributeId, "joe.bloggs1"));
        var server = new UniqueValueGenerationServer(repo);
        var request = UniqueValueTestHelpers.ImportRequest(UniqueValueTestHelpers.Generation(), attributeId, Guid.NewGuid(), baseValue: "joe.bloggs")
            with { RejectedValues = ["joe.bloggs"] };

        var outcome = await server.RegenerateAsync(request, UniqueValueTestHelpers.Options());

        Assert.That(outcome.Value, Is.EqualTo("joe.bloggs2"), "the local gates still apply to a remediation's candidates");
    }

    [Test]
    public async Task RegenerateAsync_ExistingAssignment_IsNotReturnedAsStickyAsync()
    {
        var repo = new InMemorySyncRepository();
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var objectId = Guid.NewGuid();
        var generation = UniqueValueTestHelpers.Generation();
        repo.SeedGeneratedValueAssignment(new GeneratedValueAssignment
        {
            Id = Guid.NewGuid(), MetaverseObjectId = objectId, MetaverseAttributeId = attributeId, Value = "joe.bloggs",
            NormalisedValue = "joe.bloggs", SyncRuleMappingGenerationId = generation.Id, State = GeneratedValueAssignmentState.Committed
        });
        var server = new UniqueValueGenerationServer(repo);
        var request = UniqueValueTestHelpers.ImportRequest(generation, attributeId, objectId, baseValue: "joe.bloggs") with { RejectedValues = ["joe.bloggs"] };

        var outcome = await server.RegenerateAsync(request, UniqueValueTestHelpers.Options());

        Assert.That(outcome.Value, Is.EqualTo("joe.bloggs1"), "a remediation draws a new value; the object's own assignment is what is being replaced");
    }

    // ---- Anchoring (plan decision 10) ----

    [Test]
    public async Task IsAnchoredAsync_AnotherParticipatingSystemsJoinedObjectHoldsTheValue_IsAnchoredAsync()
    {
        var (repo, mvoId, systems) = AnchoringTopology();
        SeedCso(repo, ContractorSystemId, ContractorAttributeId, mvoId, "R.Okafor");

        var verdict = await new UniqueValueGenerationServer(repo).IsAnchoredAsync(mvoId, "r.okafor", null, Targets, CorporateSystemId, systems);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdict.Anchoring, Is.EqualTo(GeneratedValueAnchoring.Anchored), "compared case-insensitively");
            Assert.That(verdict.ConnectedSystemId, Is.EqualTo(ContractorSystemId));
        }
    }

    [Test]
    public async Task IsAnchoredAsync_ValueHeldOnlyByAnotherPersonsAccount_IsUnanchoredAsync()
    {
        var (repo, mvoId, systems) = AnchoringTopology();
        SeedCso(repo, ContractorSystemId, ContractorAttributeId, Guid.NewGuid(), "r.okafor");

        var verdict = await new UniqueValueGenerationServer(repo).IsAnchoredAsync(mvoId, "r.okafor", null, Targets, CorporateSystemId, systems);

        Assert.That(verdict.Anchoring, Is.EqualTo(GeneratedValueAnchoring.Unanchored),
            "another person holding it is the collision itself, not a target that accepted it for this object");
    }

    [Test]
    public async Task IsAnchoredAsync_OnlyTheRejectingSystemHoldsIt_IsUnanchoredAsync()
    {
        var (repo, mvoId, systems) = AnchoringTopology();
        SeedCso(repo, CorporateSystemId, CorporateAttributeId, mvoId, "r.okafor");

        var verdict = await new UniqueValueGenerationServer(repo).IsAnchoredAsync(mvoId, "r.okafor", null, Targets, CorporateSystemId, systems);

        Assert.That(verdict.Anchoring, Is.EqualTo(GeneratedValueAnchoring.Unanchored));
    }

    [Test]
    public async Task IsAnchoredAsync_ParticipatingSystemClearedAndNotFullyImportedSince_CannotTellAsync()
    {
        var (repo, mvoId, systems) = AnchoringTopology();
        systems[ContractorSystemId].StrandedValueSweepArmedAt = DateTime.UtcNow.AddHours(-1);
        systems[ContractorSystemId].LastSuccessfulFullImportCompletedAt = DateTime.UtcNow.AddHours(-2);

        var verdict = await new UniqueValueGenerationServer(repo).IsAnchoredAsync(mvoId, "r.okafor", null, Targets, CorporateSystemId, systems);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdict.Anchoring, Is.EqualTo(GeneratedValueAnchoring.CannotTell), "missing knowledge never permits a rename");
            Assert.That(verdict.ConnectedSystemId, Is.EqualTo(ContractorSystemId));
        }
    }

    [Test]
    public async Task IsAnchoredAsync_ParticipatingSystemFullyImportedSinceItsClear_CanTellAsync()
    {
        var (repo, mvoId, systems) = AnchoringTopology();
        systems[ContractorSystemId].StrandedValueSweepArmedAt = DateTime.UtcNow.AddHours(-2);
        systems[ContractorSystemId].LastSuccessfulFullImportCompletedAt = DateTime.UtcNow.AddHours(-1);

        var verdict = await new UniqueValueGenerationServer(repo).IsAnchoredAsync(mvoId, "r.okafor", null, Targets, CorporateSystemId, systems);

        Assert.That(verdict.Anchoring, Is.EqualTo(GeneratedValueAnchoring.Unanchored));
    }

    [Test]
    public async Task IsAnchoredAsync_RejectingSystemClearedButNoOtherParticipant_IsUnanchoredAsync()
    {
        var (repo, mvoId, systems) = AnchoringTopology();
        systems[CorporateSystemId].StrandedValueSweepArmedAt = DateTime.UtcNow;

        var verdict = await new UniqueValueGenerationServer(repo).IsAnchoredAsync(mvoId, "r.okafor", null, Targets, CorporateSystemId, systems);

        Assert.That(verdict.Anchoring, Is.EqualTo(GeneratedValueAnchoring.Unanchored), "only the other participating systems are asked");
    }

    // ---- Needs Decision and its exits ----

    [Test]
    public async Task EnterNeedsDecisionAsync_RecordsTheStateAndWhoRejectedAndAnchoredItAsync()
    {
        var repo = new InMemorySyncRepository();
        var assignment = SeedAssignment(repo, GeneratedValueAssignmentState.Committed);
        var itemId = Guid.NewGuid();

        await new UniqueValueGenerationServer(repo).EnterNeedsDecisionAsync(assignment, rejectedByConnectedSystemId: 3, anchoredByConnectedSystemId: 4, itemId);

        var stored = repo.GeneratedValueAssignments[assignment.Id];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.State, Is.EqualTo(GeneratedValueAssignmentState.NeedsDecision));
            Assert.That(stored.RejectedByConnectedSystemId, Is.EqualTo(3));
            Assert.That(stored.AnchoredByConnectedSystemId, Is.EqualTo(4));
            Assert.That(stored.NeedsDecisionActivityRunProfileExecutionItemId, Is.EqualTo(itemId));
            Assert.That(stored.NeedsDecisionEnteredAt, Is.Not.Null);
        }
    }

    [Test]
    public async Task RetryAsync_NeedsDecision_ReleasesItAndReturnsTheParkedExportToPendingAsync()
    {
        var repo = new InMemorySyncRepository();
        var (assignment, parkedExport) = SeedNeedsDecisionWithParkedExport(repo);

        var released = await new UniqueValueGenerationServer(repo).RetryAsync(assignment.Id);

        var stored = repo.GeneratedValueAssignments[assignment.Id];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(released, Is.True);
            Assert.That(stored.State, Is.EqualTo(GeneratedValueAssignmentState.Committed), "the next export run tries the same value again");
            Assert.That(stored.Value, Is.EqualTo("r.okafor"));
            Assert.That(stored.NeedsDecisionEnteredAt, Is.Null);
            Assert.That(repo.PendingExports[parkedExport.Id].Status, Is.EqualTo(PendingExportStatus.Pending));
            Assert.That(repo.PendingExports[parkedExport.Id].ErrorCount, Is.Zero, "parking never consumed the error count");
        }
    }

    [Test]
    public async Task RetryAsync_AssignmentNotNeedingADecision_DoesNothingAsync()
    {
        var repo = new InMemorySyncRepository();
        var assignment = SeedAssignment(repo, GeneratedValueAssignmentState.Committed);

        var released = await new UniqueValueGenerationServer(repo).RetryAsync(assignment.Id);

        Assert.That(released, Is.False);
    }

    [Test]
    public async Task AuthoriseRenameAsync_RecordsTheAuthorisationAndReleasesTheParkedExportAsync()
    {
        var repo = new InMemorySyncRepository();
        var (assignment, parkedExport) = SeedNeedsDecisionWithParkedExport(repo);

        var authorised = await new UniqueValueGenerationServer(repo).AuthoriseRenameAsync(assignment.Id, "Ada Admin");

        var stored = repo.GeneratedValueAssignments[assignment.Id];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(authorised, Is.True);
            Assert.That(stored.RenameAuthorised, Is.True);
            Assert.That(stored.RenameAuthorisedByName, Is.EqualTo("Ada Admin"));
            Assert.That(stored.RenameAuthorisedAt, Is.Not.Null);
            Assert.That(repo.PendingExports[parkedExport.Id].Status, Is.EqualTo(PendingExportStatus.Pending),
                "the next export run must reach the rejection again for the authorised rename to happen");
        }
    }

    [Test]
    public async Task ReleaseNeedsDecisionForMappingAsync_ReleasesOnlyThatGenerationsAssignmentsAsync()
    {
        var repo = new InMemorySyncRepository();
        var (assignment, parkedExport) = SeedNeedsDecisionWithParkedExport(repo);
        var (otherAssignment, otherParked) = SeedNeedsDecisionWithParkedExport(repo, generationId: assignment.SyncRuleMappingGenerationId + 1000);

        var released = await new UniqueValueGenerationServer(repo).ReleaseNeedsDecisionForMappingAsync(assignment.SyncRuleMappingGenerationId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(released, Is.EqualTo(1));
            Assert.That(repo.GeneratedValueAssignments[assignment.Id].State, Is.EqualTo(GeneratedValueAssignmentState.Committed));
            Assert.That(repo.PendingExports[parkedExport.Id].Status, Is.EqualTo(PendingExportStatus.Pending));
            Assert.That(repo.GeneratedValueAssignments[otherAssignment.Id].State, Is.EqualTo(GeneratedValueAssignmentState.NeedsDecision));
            Assert.That(repo.PendingExports[otherParked.Id].Status, Is.EqualTo(PendingExportStatus.Parked));
        }
    }

    // ---- Helpers ----

    private const int CorporateSystemId = 1;
    private const int ContractorSystemId = 2;
    private const int CorporateAttributeId = 501;
    private const int ContractorAttributeId = 502;

    private static readonly IReadOnlyCollection<(int ConnectedSystemId, int AttributeId)> Targets =
        [(CorporateSystemId, CorporateAttributeId), (ContractorSystemId, ContractorAttributeId)];

    private static (InMemorySyncRepository Repo, Guid MetaverseObjectId, Dictionary<int, ConnectedSystem> Systems) AnchoringTopology()
    {
        var repo = new InMemorySyncRepository();
        var systems = new Dictionary<int, ConnectedSystem>
        {
            [CorporateSystemId] = new() { Id = CorporateSystemId, Name = "Corporate AD", LastSuccessfulFullImportCompletedAt = DateTime.UtcNow },
            [ContractorSystemId] = new() { Id = ContractorSystemId, Name = "Contractor LDAP", LastSuccessfulFullImportCompletedAt = DateTime.UtcNow }
        };
        return (repo, Guid.NewGuid(), systems);
    }

    private static void SeedCso(InMemorySyncRepository repo, int connectedSystemId, int attributeId, Guid? metaverseObjectId, string value)
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystemId,
            MetaverseObjectId = metaverseObjectId,
            Status = ConnectedSystemObjectStatus.Normal
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = attributeId, StringValue = value });
        repo.SeedConnectedSystemObject(cso);
    }

    private static MetaverseObject MetaverseObjectHolding(int attributeId, string value)
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = attributeId, StringValue = value, MetaverseObject = mvo });
        return mvo;
    }

    private static GeneratedValueAssignment SeedAssignment(InMemorySyncRepository repo, GeneratedValueAssignmentState state, int? generationId = null)
    {
        var assignment = new GeneratedValueAssignment
        {
            Id = Guid.NewGuid(),
            MetaverseObjectId = Guid.NewGuid(),
            MetaverseAttributeId = UniqueValueTestHelpers.NextAttributeId(),
            Value = "r.okafor",
            NormalisedValue = "r.okafor",
            SyncRuleMappingGenerationId = generationId ?? 77,
            State = state
        };
        repo.SeedGeneratedValueAssignment(assignment);
        return assignment;
    }

    private static (GeneratedValueAssignment Assignment, PendingExport ParkedExport) SeedNeedsDecisionWithParkedExport(InMemorySyncRepository repo, int generationId = 77)
    {
        var assignment = SeedAssignment(repo, GeneratedValueAssignmentState.NeedsDecision, generationId);
        assignment.RejectedByConnectedSystemId = ContractorSystemId;
        assignment.NeedsDecisionEnteredAt = DateTime.UtcNow;
        assignment.NeedsDecisionActivityRunProfileExecutionItemId = Guid.NewGuid();

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = ContractorSystemId,
            MetaverseObjectId = assignment.MetaverseObjectId,
            Status = ConnectedSystemObjectStatus.Normal
        };
        repo.SeedConnectedSystemObject(cso);

        var parked = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = ContractorSystemId,
            ConnectedSystemObjectId = cso.Id,
            ConnectedSystemObject = cso,
            ChangeType = PendingExportChangeType.Update,
            Status = PendingExportStatus.Parked
        };
        repo.SeedPendingExport(parked);
        return (assignment, parked);
    }
}
