// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// Merging newly evaluated changes into a staged Pending Export keys every change once and removes the superseded ones in
/// one pass (#2658), where it once re-keyed every staged change for each incoming change that collided and removed them
/// one at a time. The outcome must be exactly the old one, so these tests hold the merge to that algorithm, kept here
/// verbatim as <see cref="MergeAsBefore"/>, across a broad generated set of staged and incoming changes: single-valued and
/// multi-valued attributes and changes with no attribute navigation, every change type, values colliding within and
/// across the two lists, an incoming change for the same key more than once, single-valued attributes the merge folds,
/// whole-attribute replacements, the same change instance staged twice or both staged and incoming, and unresolved
/// references. Each case is generated twice from one seed, so the two merges run on identical but separate changes.
/// </summary>
[TestFixture]
public class SyncEngineExportMergeDifferentialTests
{
    private const int Seed = 2658;
    private const int CaseCount = 20_000;
    private const int MinimumCasesPerOutcome = 50;

    private static readonly PendingExportAttributeChangeType[] ChangeTypes =
    [
        PendingExportAttributeChangeType.Add,
        PendingExportAttributeChangeType.Add,
        PendingExportAttributeChangeType.Remove,
        PendingExportAttributeChangeType.Update,
        PendingExportAttributeChangeType.RemoveAll
    ];

    private static readonly string?[] Texts = ["cn=alice", "cn=bob", "CN=ALICE", "cn=carol", null];

    private static readonly string[] References =
    [
        "2658bbbb-0000-4000-8000-000000000001",
        "2658bbbb-0000-4000-8000-000000000002"
    ];

    [Test]
    public void MergeAttributeChangesIntoPendingExport_EveryGeneratedCase_GivesTheOutcomeTheRescanningMergeGave()
    {
        var engine = new SyncEngine();
        var disagreements = new List<string>();
        var casesWith = new Dictionary<string, int>();

        for (var caseNumber = 0; caseNumber < CaseCount; caseNumber++)
        {
            var before = GenerateCase(Seed + caseNumber);
            var after = GenerateCase(Seed + caseNumber);

            var expected = MergeAsBefore(before.Staged, before.Incoming);
            var actual = engine.MergeAttributeChangesIntoPendingExport(after.Staged, after.Incoming);

            var expectedOutcome = Describe(before.Staged, expected, before.GeneratedIds);
            var actualOutcome = Describe(after.Staged, actual, after.GeneratedIds);
            if (expectedOutcome != actualOutcome && disagreements.Count < 10)
                disagreements.Add($"case {caseNumber}:\n  before: {expectedOutcome}\n  after:  {actualOutcome}");

            Count(casesWith, "a staged change replaced", expected.ReplacedCount > 0 && before.Staged.AttributeValueChanges.Count > 0);
            Count(casesWith, "an incoming change replacing an earlier incoming one", before.IncomingRepeatsAKey);
            Count(casesWith, "a whole-attribute replacement", before.Incoming.Any(change => change.ChangeType is PendingExportAttributeChangeType.Update or PendingExportAttributeChangeType.RemoveAll));
            Count(casesWith, "a single-valued attribute folded", before.FoldsASingleValuedAttribute);
            Count(casesWith, "a change staged twice", before.StagesAChangeTwice);
            Count(casesWith, "a change both staged and incoming", before.StagedAlsoIncoming);
            Count(casesWith, "nothing incoming", before.Incoming.Count == 0);
            Count(casesWith, "an unresolved reference flagged", before.Staged.HasUnresolvedReferences && !before.InitiallyFlagged);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(disagreements, Is.Empty, "the merge must give the outcome the rescanning merge gave in every case");
            foreach (var outcome in new[]
                     {
                         "a staged change replaced", "an incoming change replacing an earlier incoming one", "a whole-attribute replacement",
                         "a single-valued attribute folded", "a change staged twice", "a change both staged and incoming", "nothing incoming",
                         "an unresolved reference flagged"
                     })
            {
                Assert.That(casesWith.GetValueOrDefault(outcome), Is.GreaterThanOrEqualTo(MinimumCasesPerOutcome), $"cases with {outcome} are covered");
            }
        }
    }

    /// <summary>
    /// The merge as it stood before #2658, verbatim but for its signature: each incoming change that collided re-keyed
    /// every staged change to find the ones it replaced, and every superseded change was removed one at a time.
    /// </summary>
    private static PendingExportMergeResult MergeAsBefore(PendingExport stagedPendingExport, List<PendingExportAttributeValueChange> newChanges)
    {
        var replacedCount = 0;
        var addedCount = 0;

        newChanges = SyncEngine.WithOneChangePerSingleValuedAttribute(newChanges);

        var wholeAttributeReplacementIds = SyncEngine.GetWholeAttributeReplacementAttributeIds(newChanges);
        var supersededChanges = stagedPendingExport.AttributeValueChanges
            .Where(avc => wholeAttributeReplacementIds.Contains(avc.AttributeId))
            .ToList();
        foreach (var superseded in supersededChanges)
            stagedPendingExport.AttributeValueChanges.Remove(superseded);

        var existingChangeKeys = new HashSet<string>();
        foreach (var existing in stagedPendingExport.AttributeValueChanges)
            existingChangeKeys.Add(SyncEngine.GetAttributeChangeMergeKey(existing));

        foreach (var newChange in newChanges)
        {
            var key = SyncEngine.GetAttributeChangeMergeKey(newChange);
            if (existingChangeKeys.Contains(key))
            {
                var toRemove = stagedPendingExport.AttributeValueChanges
                    .Where(avc => SyncEngine.GetAttributeChangeMergeKey(avc) == key)
                    .ToList();
                foreach (var r in toRemove)
                    stagedPendingExport.AttributeValueChanges.Remove(r);
                stagedPendingExport.AttributeValueChanges.Add(newChange);
                existingChangeKeys.Remove(key);
                existingChangeKeys.Add(SyncEngine.GetAttributeChangeMergeKey(newChange));
                replacedCount++;
            }
            else
            {
                stagedPendingExport.AttributeValueChanges.Add(newChange);
                existingChangeKeys.Add(key);
                addedCount++;
            }
        }

        if (newChanges.Any(ac => !string.IsNullOrEmpty(ac.UnresolvedReferenceValue)))
            stagedPendingExport.HasUnresolvedReferences = true;

        return new PendingExportMergeResult { ReplacedCount = replacedCount, AddedCount = addedCount };
    }

    private sealed record GeneratedCase(
        PendingExport Staged,
        List<PendingExportAttributeValueChange> Incoming,
        HashSet<Guid> GeneratedIds,
        bool InitiallyFlagged,
        bool IncomingRepeatsAKey,
        bool FoldsASingleValuedAttribute,
        bool StagesAChangeTwice,
        bool StagedAlsoIncoming);

    /// <summary>
    /// One case, entirely determined by <paramref name="seed"/>: called twice with one seed, it gives two identical but
    /// separate sets of changes, so that the merge folding a single-valued attribute's changes in place on one cannot touch
    /// the other.
    /// </summary>
    private static GeneratedCase GenerateCase(int seed)
    {
        var random = new Random(seed);
        var attributes = new ConnectedSystemObjectTypeAttribute?[]
        {
            Attribute(1, AttributePlurality.SingleValued),
            Attribute(2, AttributePlurality.SingleValued),
            Attribute(3, AttributePlurality.MultiValued),
            Attribute(4, AttributePlurality.MultiValued),
            null
        };
        var generatedIds = new HashSet<Guid>();

        PendingExportAttributeValueChange NewChange()
        {
            var attributeIndex = random.Next(attributes.Length);
            var attribute = attributes[attributeIndex];
            var change = new PendingExportAttributeValueChange
            {
                Id = NextGuid(random),
                Attribute = attribute!,
                // A change with no attribute navigation keys by id alone, like a single-valued one.
                AttributeId = attribute?.Id ?? 5,
                ChangeType = ChangeTypes[random.Next(ChangeTypes.Length)],
                SyncRuleId = random.Next(3),
                SyncRuleName = $"rule{random.Next(3)}"
            };
            if (random.Next(3) == 0)
                change.UnresolvedReferenceValue = References[random.Next(References.Length)];
            else
                change.StringValue = Texts[random.Next(Texts.Length)];
            generatedIds.Add(change.Id);
            return change;
        }

        var staged = Enumerable.Range(0, random.Next(0, 9)).Select(_ => NewChange()).ToList();
        var stagesAChangeTwice = staged.Count > 0 && random.Next(8) == 0;
        if (stagesAChangeTwice)
            staged.Insert(random.Next(staged.Count + 1), staged[random.Next(staged.Count)]);

        var incoming = Enumerable.Range(0, random.Next(0, 9)).Select(_ => NewChange()).ToList();
        var stagedAlsoIncoming = staged.Count > 0 && random.Next(8) == 0;
        if (stagedAlsoIncoming)
            incoming.Insert(random.Next(incoming.Count + 1), staged[random.Next(staged.Count)]);

        var initiallyFlagged = random.Next(4) == 0;
        var pendingExport = new PendingExport
        {
            Id = NextGuid(random),
            ConnectedSystemId = 1,
            ConnectedSystemObjectId = NextGuid(random),
            ChangeType = PendingExportChangeType.Update,
            Status = PendingExportStatus.Pending,
            HasUnresolvedReferences = initiallyFlagged,
            AttributeValueChanges = new(staged)
        };

        var incomingKeys = incoming.Select(SyncEngine.GetAttributeChangeMergeKey).ToList();
        var foldsASingleValuedAttribute = incoming
            .Where(change => change.Attribute?.AttributePlurality == AttributePlurality.SingleValued)
            .GroupBy(change => change.AttributeId)
            .Any(group => group.Skip(1).Any());

        return new GeneratedCase(pendingExport, incoming, generatedIds, initiallyFlagged,
            IncomingRepeatsAKey: incomingKeys.Distinct().Count() < incomingKeys.Count,
            foldsASingleValuedAttribute, stagesAChangeTwice, stagedAlsoIncoming);
    }

    /// <summary>
    /// What a merge left: its counts, whether the export is flagged as carrying unresolved references, and its changes in
    /// order. A generated change is named by its id; one the merge made by folding a single-valued attribute's removals is
    /// new in each run, so it is described by its content instead.
    /// </summary>
    private static string Describe(PendingExport merged, PendingExportMergeResult result, HashSet<Guid> generatedIds)
    {
        var changes = merged.AttributeValueChanges.Select(change =>
            (generatedIds.Contains(change.Id) ? change.Id.ToString("N")[..8] : "folded") +
            $"/{change.AttributeId}/{change.ChangeType}/{change.StringValue ?? change.UnresolvedReferenceValue ?? "-"}/{change.SyncRuleName}");

        return $"replaced {result.ReplacedCount}, added {result.AddedCount}, flagged {merged.HasUnresolvedReferences}: [{string.Join(", ", changes)}]";
    }

    private static ConnectedSystemObjectTypeAttribute Attribute(int id, AttributePlurality plurality) => new()
    {
        Id = id, Name = $"attr{id}", Type = AttributeDataType.Text, AttributePlurality = plurality
    };

    private static Guid NextGuid(Random random)
    {
        var bytes = new byte[16];
        random.NextBytes(bytes);
        return new Guid(bytes);
    }

    private static void Count(Dictionary<string, int> counts, string outcome, bool happened)
    {
        if (happened)
            counts[outcome] = counts.GetValueOrDefault(outcome) + 1;
    }
}
