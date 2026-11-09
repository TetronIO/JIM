// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// Reconciliation over probed values judges every change from a lookup built once over the candidate rows (#2850),
/// where it used to scan them once per change. These tests hold the lookup to the scan: over a broad, randomised mix of
/// rows and changes, every verdict and every recorded imported value is what the scan gave. The scan is kept here, as it
/// stood before #2850, as the oracle.
/// </summary>
[TestFixture]
public class SyncEngineReconciliationProbeEquivalenceTests
{
    private const int AttributeId = 10;

    private static ConnectedSystemObjectTypeAttribute Reference(bool caseExact) => new()
    {
        Id = AttributeId, Name = "member", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued,
        CaseExact = caseExact
    };

    /// <summary>
    /// The same attribute id described as text: no probed attribute is, but a change carrying one must still be judged
    /// as the scan judged it, which takes the lookup's fallback.
    /// </summary>
    private static readonly ConnectedSystemObjectTypeAttribute TextUnderTheSameId = new()
    {
        Id = AttributeId, Name = "member", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued
    };

    /// <summary>
    /// A connector-defined attribute under the same id, whose clearing changes are confirmed whatever the import shows.
    /// </summary>
    private static readonly ConnectedSystemObjectTypeAttribute ConnectorDefined = new()
    {
        Id = AttributeId, Name = "member", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued,
        IsConnectorDefined = true
    };

    private static readonly ConnectedSystemObjectTypeAttribute CaseInsensitive = Reference(caseExact: false);
    private static readonly ConnectedSystemObjectTypeAttribute CaseExact = Reference(caseExact: true);

    /// <summary>
    /// A second, separately loaded instance of each, so attribute identity is never what decides a comparison.
    /// </summary>
    private static readonly ConnectedSystemObjectTypeAttribute CaseInsensitiveAgain = Reference(caseExact: false);
    private static readonly ConnectedSystemObjectTypeAttribute CaseExactAgain = Reference(caseExact: true);

    private static readonly Guid[] ObjectIds = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray();

    private static string Dn(int index) => $"uid=user{index},ou=People,dc=glitterband,dc=local";

    /// <summary>
    /// One of the object's DNs in a case: as written, in capitals, or with its first component capitalised.
    /// </summary>
    private static string DnIn(int index, int casing) => casing switch
    {
        0 => Dn(index),
        1 => Dn(index).ToUpperInvariant(),
        _ => "UID=User" + Dn(index)[8..]
    };

    #region Oracle: the scan as it stood before #2850

    private static bool OracleConfirmed(ProbedAttributeValues probed, PendingExportAttributeValueChange attrChange)
    {
        if (attrChange.Attribute == null)
            return false;

        if (SyncEngine.IsConnectorDefinedClear(attrChange))
            return true;

        switch (attrChange.ChangeType)
        {
            case PendingExportAttributeChangeType.Add:
            case PendingExportAttributeChangeType.Update:
                if (SyncEngine.IsPendingChangeEmpty(attrChange))
                    return probed.CappedValueCount!.Value == 0;

                if (SyncEngine.IsWholeSetUpdate(attrChange) && probed.CappedValueCount!.Value != 1)
                    return false;

                return SyncEngine.ValueExistsOnCso(probed.CandidateValues, attrChange);

            case PendingExportAttributeChangeType.Remove:
                return !SyncEngine.ValueExistsOnCso(probed.CandidateValues, attrChange);

            case PendingExportAttributeChangeType.RemoveAll:
                return probed.CappedValueCount!.Value == 0;

            default:
                return false;
        }
    }

    private static string OracleImportedValue(ProbedAttributeValues probed, PendingExportAttributeValueChange attrChange)
    {
        var countOnly = attrChange.ChangeType == PendingExportAttributeChangeType.RemoveAll ||
                        (attrChange.ChangeType is PendingExportAttributeChangeType.Add or PendingExportAttributeChangeType.Update &&
                         SyncEngine.IsPendingChangeEmpty(attrChange));
        if (countOnly)
        {
            return probed.CappedValueCount!.Value switch
            {
                0 => "(no values)",
                1 => "(1 value)",
                _ => "(2 or more values)"
            };
        }

        var matched = probed.CandidateValues
            .Where(row => SyncEngine.ValueExistsOnCso([row], attrChange))
            .Select(row => row.UnresolvedReferenceValue ?? row.ReferenceValueId?.ToString())
            .Where(value => value != null)
            .ToList();
        if (matched.Count == 0)
            return "(value not present)";

        var shown = string.Join(", ", matched);
        return SyncEngine.IsWholeSetUpdate(attrChange) && probed.CappedValueCount > matched.Count ? shown + " (and other values)" : shown;
    }

    #endregion

    #region Cases

    /// <summary>
    /// Candidate rows as a probe may return them: resolved rows keeping their own DN in any case, resolved rows keeping
    /// another object's DN (stale), resolved rows keeping none, unresolved rows in any case, and duplicates of each. Most
    /// carry no attribute definition, as the repositories return them; some carry one that compares differently from the
    /// changes, which the comparison must ignore as the scan did.
    /// </summary>
    private static List<ConnectedSystemObjectAttributeValue> NewRows(Random random)
    {
        var rows = new List<ConnectedSystemObjectAttributeValue>();
        var rowCount = random.Next(0, 24);
        for (var i = 0; i < rowCount; i++)
        {
            var objectIndex = random.Next(ObjectIds.Length);
            var row = new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = AttributeId };
            switch (random.Next(5))
            {
                case 0:
                    row.ReferenceValueId = ObjectIds[objectIndex];
                    row.UnresolvedReferenceValue = DnIn(objectIndex, random.Next(3));
                    break;
                case 1:
                    row.ReferenceValueId = ObjectIds[objectIndex];
                    row.UnresolvedReferenceValue = DnIn(random.Next(ObjectIds.Length), random.Next(3));
                    break;
                case 2:
                    row.ReferenceValueId = ObjectIds[objectIndex];
                    break;
                default:
                    row.UnresolvedReferenceValue = DnIn(objectIndex, random.Next(3));
                    break;
            }

            row.Attribute = random.Next(4) == 0 ? (random.Next(2) == 0 ? CaseExact : CaseInsensitive) : null!;
            rows.Add(row);

            if (random.Next(5) == 0)
            {
                rows.Add(new ConnectedSystemObjectAttributeValue
                {
                    Id = Guid.NewGuid(), AttributeId = AttributeId, Attribute = row.Attribute,
                    ReferenceValueId = row.ReferenceValueId, UnresolvedReferenceValue = row.UnresolvedReferenceValue
                });
            }
        }

        return rows;
    }

    /// <summary>
    /// Every kind of change: Add, Remove, whole-set Update, RemoveAll and empty Add or Update; naming an object or not;
    /// carrying its DN as the resolved string, the unresolved string, both, or neither, in any case; on a case-insensitive
    /// or a case-exact attribute, and occasionally under an attribute that compares differently from the first change
    /// judged (or describes another data type), so the lookup's fallback runs mid-sequence.
    /// </summary>
    private static PendingExportAttributeValueChange NewChange(Random random, ConnectedSystemObjectTypeAttribute usualAttribute)
    {
        var objectIndex = random.Next(ObjectIds.Length);
        var change = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(), AttributeId = AttributeId,
            Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation,
            Attribute = random.Next(10) switch
            {
                0 => usualAttribute.CaseExact ? CaseInsensitiveAgain : CaseExactAgain,
                1 => TextUnderTheSameId,
                2 => usualAttribute.CaseExact ? CaseExactAgain : CaseInsensitiveAgain,
                3 when random.Next(3) == 0 => ConnectorDefined,
                _ => usualAttribute
            },
            ChangeType = random.Next(7) switch
            {
                0 or 1 => PendingExportAttributeChangeType.Add,
                2 or 3 => PendingExportAttributeChangeType.Remove,
                4 => PendingExportAttributeChangeType.Update,
                5 => PendingExportAttributeChangeType.RemoveAll,
                _ => random.Next(2) == 0 ? PendingExportAttributeChangeType.Add : PendingExportAttributeChangeType.Update
            }
        };

        if (change.ChangeType == PendingExportAttributeChangeType.RemoveAll || random.Next(12) == 0)
            return change;

        // Another object's id now and then: a change whose DN and id disagree.
        if (random.Next(3) != 0)
            change.ResolvedReferenceCsoId = ObjectIds[random.Next(4) == 0 ? random.Next(ObjectIds.Length) : objectIndex];

        switch (random.Next(4))
        {
            case 0:
                change.StringValue = DnIn(objectIndex, random.Next(3));
                break;
            case 1:
                change.UnresolvedReferenceValue = DnIn(objectIndex, random.Next(3));
                break;
            case 2:
                change.StringValue = DnIn(objectIndex, random.Next(3));
                change.UnresolvedReferenceValue = DnIn(random.Next(ObjectIds.Length), random.Next(3));
                break;
            default:
                // An empty string is not a value: judged as carrying none, while not counting as an empty change.
                change.StringValue = "";
                break;
        }

        return change;
    }

    #endregion

    /// <summary>
    /// For many randomised groups and change sets, reconciliation over the probed values confirms and retries exactly the
    /// changes the scan did, and records exactly the imported value the scan did, for every change and in the same order.
    /// </summary>
    [Test]
    public void ReconcileCsoAgainstPendingExport_OverProbedValues_SameVerdictsAndImportedValuesAsTheScan([Values(1, 2, 3, 4, 5)] int seed)
    {
        var random = new Random(seed);
        var engine = new SyncEngine();
        var judged = 0;

        for (var trial = 0; trial < 200; trial++)
        {
            var groupId = Guid.NewGuid();
            var usualAttribute = random.Next(2) == 0 ? CaseExact : CaseInsensitive;
            var rows = NewRows(random);
            var changes = Enumerable.Range(0, random.Next(1, 30)).Select(_ => NewChange(random, usualAttribute)).ToList();
            var probed = new ProbedAttributeValues
            {
                ConnectedSystemObjectId = groupId, AttributeId = AttributeId,
                ProbedChangeIds = changes.Select(c => c.Id).ToHashSet(), CandidateValues = rows,
                CappedValueCount = Math.Min(random.Next(0, 3), rows.Count == 0 ? 0 : 2)
            };

            var expectedConfirmed = changes.Where(c => OracleConfirmed(probed, c)).Select(c => c.Id).ToList();
            var expectedImported = changes.ToDictionary(c => c.Id, c => OracleImportedValue(probed, c));

            // What the import showed, for every change, from one lookup over the rows: as Verbose logging reads it.
            var lookup = new SyncEngine.ProbedValueLookup(probed);
            var imported = changes.ToDictionary(c => c.Id, c => SyncEngine.GetProbedImportedValueAsString(lookup, c));

            var pendingExport = new PendingExport
            {
                Id = Guid.NewGuid(), ConnectedSystemObjectId = groupId, ChangeType = PendingExportChangeType.Update,
                Status = PendingExportStatus.Exported, AttributeValueChanges = [.. changes]
            };
            var result = new PendingExportReconciliationResult();
            engine.ReconcileCsoAgainstPendingExport(new ConnectedSystemObject { Id = groupId }, pendingExport, result,
                probedValuesByAttributeId: new Dictionary<int, ProbedAttributeValues> { [AttributeId] = probed });

            var label = $"seed {seed}, trial {trial}";
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.ConfirmedChanges.Select(c => c.Id), Is.EqualTo(expectedConfirmed), "Confirmed: " + label);
                Assert.That(result.RetryChanges.Select(c => c.Id), Is.EqualTo(changes.Select(c => c.Id).Except(expectedConfirmed)), "Retried: " + label);
                Assert.That(result.RetryChanges.Select(c => (c.Id, c.LastImportedValue)),
                    Is.EqualTo(result.RetryChanges.Select(c => (c.Id, (string?)expectedImported[c.Id]))), "Recorded imported value: " + label);
                Assert.That(imported, Is.EqualTo(expectedImported), "Imported value: " + label);
            }

            judged += changes.Count;
        }

        TestContext.Out.WriteLine($"Seed {seed}: {judged:N0} changes judged");
    }

    /// <summary>
    /// The random mix is broad enough to mean something: across the seeds it confirms and retries changes of every type,
    /// finds a match by object and by string alike, and runs the fallback.
    /// </summary>
    [Test]
    public void ChangeMix_CoversEveryKindOfChangeAndOutcome()
    {
        var outcomes = new HashSet<(PendingExportAttributeChangeType, bool)>();
        var sawMatchByObjectOnly = false;
        var sawMatchByStringOnly = false;
        var sawFallback = false;
        var sawMultipleMatches = false;

        foreach (var seed in new[] { 1, 2, 3, 4, 5 })
        {
            var random = new Random(seed);
            for (var trial = 0; trial < 200; trial++)
            {
                var usualAttribute = random.Next(2) == 0 ? CaseExact : CaseInsensitive;
                var rows = NewRows(random);
                var changes = Enumerable.Range(0, random.Next(1, 30)).Select(_ => NewChange(random, usualAttribute)).ToList();
                var probed = new ProbedAttributeValues
                {
                    ConnectedSystemObjectId = Guid.NewGuid(), AttributeId = AttributeId,
                    ProbedChangeIds = changes.Select(c => c.Id).ToHashSet(), CandidateValues = rows,
                    CappedValueCount = Math.Min(random.Next(0, 3), rows.Count == 0 ? 0 : 2)
                };

                foreach (var change in changes)
                {
                    outcomes.Add((change.ChangeType, OracleConfirmed(probed, change)));
                    sawFallback |= !ReferenceEquals(change.Attribute, usualAttribute);
                    var matching = rows.Where(r => SyncEngine.ValueExistsOnCso([r], change)).ToList();
                    sawMultipleMatches |= matching.Count > 1;
                    sawMatchByObjectOnly |= matching.Any(r => r.ReferenceValueId.HasValue && r.ReferenceValueId == change.ResolvedReferenceCsoId &&
                        !string.Equals(r.UnresolvedReferenceValue, change.StringValue, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(r.UnresolvedReferenceValue, change.UnresolvedReferenceValue, StringComparison.OrdinalIgnoreCase));
                    sawMatchByStringOnly |= matching.Any(r => !r.ReferenceValueId.HasValue);
                }
            }
        }

        using (Assert.EnterMultipleScope())
        {
            foreach (var changeType in new[]
                     {
                         PendingExportAttributeChangeType.Add, PendingExportAttributeChangeType.Remove,
                         PendingExportAttributeChangeType.Update, PendingExportAttributeChangeType.RemoveAll
                     })
            {
                Assert.That(outcomes, Does.Contain((changeType, true)), $"{changeType} confirmed");
                Assert.That(outcomes, Does.Contain((changeType, false)), $"{changeType} retried");
            }

            Assert.That(sawMatchByObjectOnly, Is.True, "a match by the object named alone");
            Assert.That(sawMatchByStringOnly, Is.True, "a match by string on an unresolved row");
            Assert.That(sawMultipleMatches, Is.True, "a change matching several rows");
            Assert.That(sawFallback, Is.True, "a change under an attribute other than the usual one");
        }
    }
}
