// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// A confirming import reconciles the objects it still holds from the values it holds (#2908), answering reconciliation's
/// probe questions from them (<see cref="SyncEngine.AnswerReconciliationProbes"/>) where it used to reload each object and
/// ask the database. These tests hold the two to the same result: over a randomised mix of a group's member rows and the
/// changes exported to it, judging the held object confirms and retries exactly the changes judging a reload does, and
/// records exactly the same last imported value for each. The reload is modelled as the database answers it: the probe's
/// three arms (a resolved row by id, an outstanding row by its exact string, an outstanding row by its string ignoring
/// case), the capped count, and a load that leaves the probed attribute out.
/// </summary>
[TestFixture]
public class SyncEngineReconciliationHeldValueTests
{
    private const int MemberAttributeId = 10;
    private const int DisplayNameAttributeId = 11;

    private static readonly ConnectedSystemObjectTypeAttribute DisplayName = new()
    {
        Id = DisplayNameAttributeId, Name = "displayName", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued
    };

    private static ConnectedSystemObjectTypeAttribute Member(bool caseExact) => new()
    {
        Id = MemberAttributeId, Name = "member", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued,
        CaseExact = caseExact
    };

    private static string Dn(int index, int casing) => casing switch
    {
        0 => $"uid=user{index},ou=People,dc=glitterband,dc=local",
        1 => $"UID=USER{index},OU=PEOPLE,DC=GLITTERBAND,DC=LOCAL",
        _ => $"uid=User{index},ou=People,dc=glitterband,dc=local"
    };

    private static Guid NewGuid(Random random)
    {
        var bytes = new byte[16];
        random.NextBytes(bytes);
        return new Guid(bytes);
    }

    /// <summary>
    /// One trial's group: member rows resolved (keeping their own DN, another's, or none), outstanding in any case, and
    /// duplicated now and then; and its display name.
    /// </summary>
    private static List<ConnectedSystemObjectAttributeValue> HeldValues(Random random, Guid[] objectIds, ConnectedSystemObjectTypeAttribute member)
    {
        var values = new List<ConnectedSystemObjectAttributeValue>
        {
            new() { Id = NewGuid(random), AttributeId = DisplayNameAttributeId, Attribute = DisplayName, StringValue = "Team" }
        };
        for (var i = random.Next(0, 16); i > 0; i--)
        {
            var objectIndex = random.Next(objectIds.Length);
            var row = new ConnectedSystemObjectAttributeValue { Id = NewGuid(random), AttributeId = MemberAttributeId, Attribute = member };
            switch (random.Next(4))
            {
                case 0:
                    row.ReferenceValueId = objectIds[objectIndex];
                    row.UnresolvedReferenceValue = Dn(objectIndex, random.Next(3));
                    break;
                case 1:
                    row.ReferenceValueId = objectIds[objectIndex];
                    row.UnresolvedReferenceValue = Dn(random.Next(objectIds.Length), random.Next(3));
                    break;
                case 2:
                    row.ReferenceValueId = objectIds[objectIndex];
                    break;
                default:
                    row.UnresolvedReferenceValue = Dn(objectIndex, random.Next(3));
                    break;
            }

            values.Add(row);
            if (random.Next(6) == 0)
                values.Add(new ConnectedSystemObjectAttributeValue
                {
                    Id = NewGuid(random), AttributeId = MemberAttributeId, Attribute = member,
                    ReferenceValueId = row.ReferenceValueId, UnresolvedReferenceValue = row.UnresolvedReferenceValue
                });
        }

        return values;
    }

    /// <summary>
    /// One trial's Pending Export, built from <paramref name="seed"/> so the held and reloaded judgements each get their
    /// own identical copy (judging changes them): member changes of every kind, naming an object or not, and a display
    /// name change, which is never probed.
    /// </summary>
    private static PendingExport Export(int seed, Guid csoId, Guid[] objectIds, ConnectedSystemObjectTypeAttribute member)
    {
        var random = new Random(seed);
        var export = new PendingExport
        {
            Id = NewGuid(random), ConnectedSystemObjectId = csoId, ChangeType = PendingExportChangeType.Update, Status = PendingExportStatus.Exported
        };
        export.AttributeValueChanges.Add(new PendingExportAttributeValueChange
        {
            Id = NewGuid(random), AttributeId = DisplayNameAttributeId, Attribute = DisplayName, ChangeType = PendingExportAttributeChangeType.Update,
            Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation, StringValue = random.Next(2) == 0 ? "Team" : "Other"
        });

        for (var i = random.Next(1, 12); i > 0; i--)
        {
            var objectIndex = random.Next(objectIds.Length);
            var change = new PendingExportAttributeValueChange
            {
                Id = NewGuid(random), AttributeId = MemberAttributeId, Attribute = member,
                Status = random.Next(2) == 0 ? PendingExportAttributeChangeStatus.ExportedPendingConfirmation : PendingExportAttributeChangeStatus.ExportedNotConfirmed,
                ChangeType = random.Next(6) switch
                {
                    0 or 1 => PendingExportAttributeChangeType.Add,
                    2 or 3 => PendingExportAttributeChangeType.Remove,
                    4 => PendingExportAttributeChangeType.Update,
                    _ => PendingExportAttributeChangeType.RemoveAll
                }
            };
            export.AttributeValueChanges.Add(change);
            if (change.ChangeType == PendingExportAttributeChangeType.RemoveAll || random.Next(10) == 0)
                continue;

            // Usually naming an object; a change naming none takes its pair to the full load.
            if (random.Next(6) != 0)
                change.ResolvedReferenceCsoId = objectIds[random.Next(4) == 0 ? random.Next(objectIds.Length) : objectIndex];
            if (random.Next(2) == 0)
                change.StringValue = Dn(objectIndex, random.Next(3));
            else
                change.UnresolvedReferenceValue = Dn(objectIndex, random.Next(3));
        }

        return export;
    }

    /// <summary>
    /// The database's answer to <paramref name="questions"/> over the held rows, arm by arm as
    /// <c>SyncRepository.ReconciliationProbeValuesSql</c> and <c>ReconciliationProbeCountSql</c> run them.
    /// </summary>
    private static List<ProbedAttributeValues> AnswerAsTheDatabase(IReadOnlyList<ReconciliationProbeQuestion> questions,
        IReadOnlyList<ConnectedSystemObjectAttributeValue> held) =>
        questions.Select(question =>
        {
            var rows = held.Where(row => row.AttributeId == question.AttributeId).ToList();
            var lowered = question.CaseInsensitiveValues.Select(v => v.ToLowerInvariant()).ToHashSet();
            var candidates = rows.Where(row =>
                    (row.ReferenceValueId is { } id && question.ReferenceIds.Contains(id)) ||
                    (row.ReferenceValueId == null && row.UnresolvedReferenceValue != null &&
                     (question.ExactValues.Contains(row.UnresolvedReferenceValue) || lowered.Contains(row.UnresolvedReferenceValue.ToLowerInvariant()))))
                .Select(row => new ConnectedSystemObjectAttributeValue
                {
                    Id = row.Id, AttributeId = row.AttributeId, ReferenceValueId = row.ReferenceValueId, UnresolvedReferenceValue = row.UnresolvedReferenceValue
                })
                .ToList();
            return new ProbedAttributeValues
            {
                ConnectedSystemObjectId = question.ConnectedSystemObjectId, AttributeId = question.AttributeId, ProbedChangeIds = question.ChangeIds,
                CandidateValues = candidates, CappedValueCount = question.NeedsCount ? Math.Min(rows.Count, 2) : null
            };
        }).ToList();

    private static Dictionary<int, ProbedAttributeValues>? ByAttribute(IEnumerable<ProbedAttributeValues> answers)
    {
        var byAttribute = answers.ToDictionary(a => a.AttributeId);
        return byAttribute.Count == 0 ? null : byAttribute;
    }

    [Test]
    public void ReconcileCsoAgainstPendingExport_HeldValuesAnsweringTheProbe_SameVerdictsAndImportedValuesAsAReload([Values(1, 2, 3, 4, 5)] int seed)
    {
        var random = new Random(seed);
        var engine = new SyncEngine();
        var objectIds = Enumerable.Range(0, 6).Select(_ => NewGuid(random)).ToArray();
        var probedPairs = 0;
        var fullLoadPairs = 0;

        for (var trial = 0; trial < 300; trial++)
        {
            var csoId = NewGuid(random);
            var member = Member(caseExact: random.Next(3) == 0);
            var held = HeldValues(random, objectIds, member);
            var exportSeed = random.Next();

            // As the database would: probe, then load leaving the probed attributes out.
            var reloadedExport = Export(exportSeed, csoId, objectIds, member);
            var reloadPlan = SyncEngine.PlanReconciliationProbes([reloadedExport], databaseFoldsCaseLikeIdentifierKey: true);
            var reloadAnswers = AnswerAsTheDatabase(reloadPlan.Questions, held);
            var probedIds = reloadAnswers.Select(a => a.AttributeId).ToHashSet();
            var reloaded = new ConnectedSystemObject { Id = csoId, AttributeValues = [.. held.Where(v => !probedIds.Contains(v.AttributeId))] };
            var reloadResult = new PendingExportReconciliationResult();
            engine.ReconcileCsoAgainstPendingExport(reloaded, reloadedExport, reloadResult, probedValuesByAttributeId: ByAttribute(reloadAnswers));

            // As the import now does: answer the same plan from every value held.
            var heldExport = Export(exportSeed, csoId, objectIds, member);
            var heldPlan = SyncEngine.PlanReconciliationProbes([heldExport], databaseFoldsCaseLikeIdentifierKey: true);
            var heldAnswers = SyncEngine.AnswerReconciliationProbes(heldPlan.Questions,
                new Dictionary<Guid, IReadOnlyList<ConnectedSystemObjectAttributeValue>> { [csoId] = held });
            var heldResult = new PendingExportReconciliationResult();
            engine.ReconcileCsoAgainstPendingExport(new ConnectedSystemObject { Id = csoId, AttributeValues = [.. held] }, heldExport, heldResult,
                probedValuesByAttributeId: ByAttribute(heldAnswers));

            var label = $"seed {seed}, trial {trial}";
            using (Assert.EnterMultipleScope())
            {
                Assert.That(heldResult.ConfirmedChanges.Select(c => c.Id), Is.EqualTo(reloadResult.ConfirmedChanges.Select(c => c.Id)), "Confirmed: " + label);
                Assert.That(heldResult.RetryChanges.Select(c => (c.Id, c.LastImportedValue)),
                    Is.EqualTo(reloadResult.RetryChanges.Select(c => (c.Id, c.LastImportedValue))), "Retried, with what was imported: " + label);
                Assert.That(heldResult.PendingExportDeleted, Is.EqualTo(reloadResult.PendingExportDeleted), "Deleted: " + label);
                Assert.That(heldExport.Status, Is.EqualTo(reloadedExport.Status), "Status: " + label);
            }

            probedPairs += reloadPlan.Questions.Count;
            fullLoadPairs += reloadPlan.FullLoadPairCount;
        }

        // The mix means something: both paths a reload takes are taken.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(probedPairs, Is.GreaterThan(50));
            Assert.That(fullLoadPairs, Is.GreaterThan(20));
        }
    }

    [Test]
    public void AnswerReconciliationProbes_ObjectWithNoHeldValues_Throws()
    {
        var question = new ReconciliationProbeQuestion
        {
            ConnectedSystemObjectId = Guid.NewGuid(), AttributeId = MemberAttributeId, ChangeIds = new HashSet<Guid>(), ReferenceIds = [],
            ExactValues = [], CaseInsensitiveValues = [], NeedsCount = true
        };

        Assert.That(() => SyncEngine.AnswerReconciliationProbes([question], new Dictionary<Guid, IReadOnlyList<ConnectedSystemObjectAttributeValue>>()),
            Throws.TypeOf<JIM.Models.Exceptions.ReconciliationProbeIncompleteException>());
    }
}
