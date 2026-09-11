// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Reflection;
using JIM.Application.Servers;
using JIM.Connectors.Mock;
using JIM.InMemoryData;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Worker.Models;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Processors;

/// <summary>
/// Run Profile Safeguards (#1618, Layer 2): a Full Import Run Profile can carry
/// <see cref="ConnectedSystemRunProfile.MaxDetectedDeletions"/> and/or
/// <see cref="ConnectedSystemRunProfile.MaxDetectedDeletionsPercent"/>. If the number of Connected System
/// Objects deletion detection would newly mark as deleted exceeds either limit, the whole detection is
/// refused: nothing is marked, no execution item is written, and no stale Pending Export is cleaned up,
/// for any candidate. Invokes the production orchestrator (<c>ProcessConnectedSystemObjectDeletionsAsync</c>)
/// via reflection, exactly as <see cref="DeselectedObjectTypeDeletionDetectionTests"/> does, so the
/// resolve-then-apply split and the per-type set difference stay the real, unreimplemented behaviour.
/// </summary>
[TestFixture]
public class DeletionDetectionLimitTests
{
    private const int ConnectedSystemId = 1;
    private const int ObjectTypeId = 100;
    private const int ExternalIdAttributeId = 10;

    [Test]
    public async Task ProcessConnectedSystemObjectDeletions_CountExactlyAtLimit_AppliesAsync()
    {
        var result = await RunAsync(candidateCount: 5, maxDetectedDeletions: 5, maxDetectedDeletionsPercent: null, existingCsoCount: 100);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Updated, Has.Count.EqualTo(5), "At the limit, every candidate must be marked.");
            Assert.That(result.Activity.DetectedDeletionsWithheld, Is.Zero);
            Assert.That(result.Activity.WarningMessage, Is.Null.Or.Empty);
        }
    }

    [Test]
    public async Task ProcessConnectedSystemObjectDeletions_CountOneAboveLimit_RefusesAsync()
    {
        var result = await RunAsync(candidateCount: 5, maxDetectedDeletions: 4, maxDetectedDeletionsPercent: null, existingCsoCount: 100);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Updated, Is.Empty, "One above the limit must withhold the whole detection, not the first four.");
            Assert.That(result.ExecutionItems, Is.Empty);
            Assert.That(result.Activity.DetectedDeletionsWithheld, Is.EqualTo(5));
            Assert.That(result.Activity.WarningMessage, Does.Contain("limit of 4"));
        }
    }

    [Test]
    public async Task ProcessConnectedSystemObjectDeletions_CountOneBelowLimit_AppliesAsync()
    {
        var result = await RunAsync(candidateCount: 5, maxDetectedDeletions: 6, maxDetectedDeletionsPercent: null, existingCsoCount: 100);

        Assert.That(result.Updated, Has.Count.EqualTo(5));
        Assert.That(result.Activity.DetectedDeletionsWithheld, Is.Zero);
    }

    [Test]
    public async Task ProcessConnectedSystemObjectDeletions_ShareExactlyAtLimit_AppliesAsync()
    {
        // 10 of 100 is exactly the 10% threshold; the check is strictly greater than, so this must apply.
        var result = await RunAsync(candidateCount: 10, maxDetectedDeletions: null, maxDetectedDeletionsPercent: 10, existingCsoCount: 100);

        Assert.That(result.Updated, Has.Count.EqualTo(10));
        Assert.That(result.Activity.DetectedDeletionsWithheld, Is.Zero);
    }

    [Test]
    public async Task ProcessConnectedSystemObjectDeletions_ShareOneObjectAboveLimit_RefusesAsync()
    {
        var result = await RunAsync(candidateCount: 11, maxDetectedDeletions: null, maxDetectedDeletionsPercent: 10, existingCsoCount: 100);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Updated, Is.Empty);
            Assert.That(result.Activity.DetectedDeletionsWithheld, Is.EqualTo(11));
            Assert.That(result.Activity.WarningMessage, Does.Contain("limit of 10%"));
        }
    }

    [Test]
    public async Task ProcessConnectedSystemObjectDeletions_ShareOneObjectBelowLimit_AppliesAsync()
    {
        var result = await RunAsync(candidateCount: 9, maxDetectedDeletions: null, maxDetectedDeletionsPercent: 10, existingCsoCount: 100);

        Assert.That(result.Updated, Has.Count.EqualTo(9));
        Assert.That(result.Activity.DetectedDeletionsWithheld, Is.Zero);
    }

    [Test]
    public async Task ProcessConnectedSystemObjectDeletions_Refused_CleansNoStalePendingExportAsync()
    {
        var result = await RunAsync(candidateCount: 3, maxDetectedDeletions: 1, maxDetectedDeletionsPercent: null,
            existingCsoCount: 100, seedPendingExportsOnCandidates: true);

        var remainingPendingExports = await result.Repository.GetPendingExportsCountAsync(ConnectedSystemId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(remainingPendingExports, Is.EqualTo(3),
                "A refused detection must leave every stale Pending Export in place too: the import that fed " +
                "it may itself be wrong, so nothing about the Connector Space's existing state may change.");
            Assert.That(result.Updated, Is.Empty);
        }
    }

    [Test]
    public async Task ProcessConnectedSystemObjectDeletions_Applied_CleansStalePendingExportsAsync()
    {
        var result = await RunAsync(candidateCount: 2, maxDetectedDeletions: null, maxDetectedDeletionsPercent: null,
            existingCsoCount: 100, seedPendingExportsOnCandidates: true);

        var remainingPendingExports = await result.Repository.GetPendingExportsCountAsync(ConnectedSystemId);

        Assert.That(remainingPendingExports, Is.Zero, "Today's behaviour: an applied detection clears stale Pending Exports for every candidate it marks.");
    }

    [Test]
    public async Task ProcessConnectedSystemObjectDeletions_NoLimitsSet_AppliesExactlyAsBeforeAsync()
    {
        var result = await RunAsync(candidateCount: 4, maxDetectedDeletions: null, maxDetectedDeletionsPercent: null, existingCsoCount: 4);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Updated, Has.Count.EqualTo(4));
            Assert.That(result.Updated.Select(cso => cso.Status), Is.All.EqualTo(ConnectedSystemObjectStatus.Obsolete));
            Assert.That(result.ExecutionItems, Has.Count.EqualTo(4));
            Assert.That(result.Activity.DetectedDeletionsWithheld, Is.Zero);
        }
    }

    [Test]
    public async Task ProcessConnectedSystemObjectDeletions_AlreadyObsoleteCandidates_ExcludedFromTheLimitCountAsync()
    {
        // 3 already-Obsolete candidates plus 2 genuinely new ones: a limit of 2 would refuse if the
        // already-Obsolete candidates counted, but must apply because they are excluded.
        var result = await RunAsync(candidateCount: 2, maxDetectedDeletions: 2, maxDetectedDeletionsPercent: null,
            existingCsoCount: 100, alreadyObsoleteCandidateCount: 3);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Activity.DetectedDeletionsWithheld, Is.Zero,
                "Already-Obsolete candidates must not count towards the limit at all.");
            Assert.That(result.Updated, Has.Count.EqualTo(2), "Only the genuinely new candidates are marked and queued for update.");
            Assert.That(result.ExecutionItems, Has.Count.EqualTo(2));
        }
    }

    [Test]
    public void DetectedDeletionsWithheld_LeftNullUnlessDeletionDetectionRuns()
    {
        // PerformImportAsync only calls ProcessConnectedSystemObjectDeletionsAsync at all when the Run
        // Profile is a Full Import (the guard around line 548 is unchanged by Layer 2), so a Delta
        // Import's Activity never has this method invoked against it and the counter stays at its
        // default: null, not zero. Asserted here at the boundary this fixture actually controls, rather
        // than by driving a full Delta Import.
        var activity = new Activity();

        Assert.That(activity.DetectedDeletionsWithheld, Is.Null);
    }

    /// <summary>
    /// What one invocation of the production orchestrator did: the objects it queued for update, the
    /// execution items it recorded, the Activity it acted on (for the counter and warning), and the
    /// repository (so a test can ask what became of any Pending Exports it seeded).
    /// </summary>
    private sealed record DeletionResult(
        List<ConnectedSystemObject> Updated,
        List<ActivityRunProfileExecutionItem> ExecutionItems,
        Activity Activity,
        SyncRepository Repository);

    /// <summary>
    /// Seeds <paramref name="candidateCount"/> Connected System Objects (Normal status) and, optionally,
    /// <paramref name="alreadyObsoleteCandidateCount"/> more (Obsolete status), none of which appear in
    /// an empty imported external id set, so every one of them is a deletion candidate. Invokes the
    /// production two-phase orchestrator against a Full Import Run Profile carrying the given limits.
    /// </summary>
    private static async Task<DeletionResult> RunAsync(
        int candidateCount,
        int? maxDetectedDeletions,
        int? maxDetectedDeletionsPercent,
        int existingCsoCount,
        int alreadyObsoleteCandidateCount = 0,
        bool seedPendingExportsOnCandidates = false)
    {
        var repository = new SyncRepository();

        for (var i = 0; i < candidateCount; i++)
            SeedCandidate(repository, $"normal-{i}", ConnectedSystemObjectStatus.Normal, seedPendingExportsOnCandidates);

        for (var i = 0; i < alreadyObsoleteCandidateCount; i++)
            SeedCandidate(repository, $"obsolete-{i}", ConnectedSystemObjectStatus.Obsolete, seedPendingExportsOnCandidates);

        var connectedSystem = new ConnectedSystem
        {
            Id = ConnectedSystemId,
            Name = "Glitterband",
            ObjectTypes =
            [
                new ConnectedSystemObjectType
                {
                    Id = ObjectTypeId,
                    Name = "User",
                    ConnectedSystemId = ConnectedSystemId,
                    Selected = true,
                    Attributes =
                    [
                        new ConnectedSystemObjectTypeAttribute
                        {
                            Id = ExternalIdAttributeId,
                            Name = "id",
                            Type = AttributeDataType.Text,
                            IsExternalId = true,
                            Selected = true
                        }
                    ]
                }
            ]
        };

        var runProfile = new ConnectedSystemRunProfile
        {
            Name = "Full Import",
            RunType = ConnectedSystemRunType.FullImport,
            ConnectedSystemId = ConnectedSystemId,
            MaxDetectedDeletions = maxDetectedDeletions,
            MaxDetectedDeletionsPercent = maxDetectedDeletionsPercent
        };

        var activity = new Activity();
        var workerTask = TestUtilities.CreateTestWorkerTask(activity, initiatedBy: null);
        using var cancellationTokenSource = new CancellationTokenSource();

        var processor = new SyncImportTaskProcessor(
            null!,
            repository,
            null!,
            new SyncEngine(),
            new MockFileConnector(),
            connectedSystem,
            runProfile,
            workerTask,
            cancellationTokenSource);

        const string methodName = "ProcessConnectedSystemObjectDeletionsAsync";
        var method = typeof(SyncImportTaskProcessor).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"SyncImportTaskProcessor.{methodName} was not found. If it has been renamed or its signature has " +
                "changed, update this fixture to invoke the current production method; do not reimplement its " +
                "resolve-then-apply behaviour here, because that behaviour is what these tests pin.");

        var toBeUpdated = new List<ConnectedSystemObject>();
        var task = (Task)method.Invoke(processor, [new List<ExternalIdPair>(), toBeUpdated, null, existingCsoCount])!;
        await task;

        const string fieldName = "_activityRunProfileExecutionItems";
        var executionItems = (List<ActivityRunProfileExecutionItem>)(typeof(SyncImportTaskProcessor)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(processor)
            ?? throw new InvalidOperationException(
                $"SyncImportTaskProcessor.{fieldName} was not found. It is where the import accumulates the " +
                "execution items it will persist, and whether an item lands there is the behaviour under test."));

        return new DeletionResult(toBeUpdated, executionItems, activity, repository);
    }

    private static void SeedCandidate(SyncRepository repository, string externalId, ConnectedSystemObjectStatus status, bool seedPendingExport)
    {
        var csoId = Guid.NewGuid();
        repository.SeedConnectedSystemObject(new ConnectedSystemObject
        {
            Id = csoId,
            ConnectedSystemId = ConnectedSystemId,
            TypeId = ObjectTypeId,
            ExternalIdAttributeId = ExternalIdAttributeId,
            Status = status,
            Created = DateTime.UtcNow,
            AttributeValues =
            [
                new ConnectedSystemObjectAttributeValue
                {
                    Id = Guid.NewGuid(),
                    AttributeId = ExternalIdAttributeId,
                    StringValue = externalId
                }
            ]
        });

        if (seedPendingExport)
            repository.SeedPendingExport(new PendingExport
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = ConnectedSystemId,
                ConnectedSystemObjectId = csoId,
                ChangeType = PendingExportChangeType.Update
            });
    }
}
