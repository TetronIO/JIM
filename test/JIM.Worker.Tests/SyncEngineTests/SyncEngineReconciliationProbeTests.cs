// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Exceptions;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Utilities;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// Pending Export reconciliation over probed values (#2669): instead of loading every member of a large group, the
/// engine is given, per multi-valued reference attribute, the rows that could match its changes and, when a verdict
/// depends on it, the value count capped at two. These tests hold the probed path to the full load's verdicts, pin the
/// planner's rules for what may be probed, and pin the fail-fast path for a question the probe cannot answer.
/// </summary>
public class SyncEngineReconciliationProbeTests
{
    private const int CaseInsensitiveMemberId = 10;
    private const int CaseExactMemberId = 11;

    private static readonly ConnectedSystemObjectTypeAttribute CaseInsensitiveMember = new()
    {
        Id = CaseInsensitiveMemberId, Name = "member", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued
    };

    private static readonly ConnectedSystemObjectTypeAttribute CaseExactMember = new()
    {
        Id = CaseExactMemberId, Name = "uniqueMember", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued,
        CaseExact = true
    };

    private static string Dn(string uid) => $"uid={uid},ou=People,dc=glitterband,dc=local";

    private static readonly Guid AliceId = Guid.NewGuid();
    private static readonly Guid BobId = Guid.NewGuid();
    private static readonly Guid CarolId = Guid.NewGuid();
    private static readonly Guid DaveId = Guid.NewGuid();
    private static readonly Guid ErinId = Guid.NewGuid();
    private static readonly Guid FrankId = Guid.NewGuid();
    private static readonly Guid NickId = Guid.NewGuid();
    private static readonly Guid KateId = Guid.NewGuid();
    private static readonly Guid GroupId = Guid.NewGuid();

    private SyncEngine _engine = null!;

    [SetUp]
    public void SetUp()
    {
        _engine = new SyncEngine();
    }

    #region Fixtures

    /// <summary>
    /// A group holding, on its case-insensitive member attribute: Alice resolved; a row resolved to Bob whose retained DN
    /// is Carol's in capitals; Dave unresolved in capitals; Erin unresolved; and two unresolved DNs whose non-ASCII
    /// characters JIM folds into ASCII (the dotted capital I in Nick, the Kelvin sign in Kate). On its case-exact member
    /// attribute: Alice resolved and Frank unresolved, both lower case, a non-ASCII DN, and a row resolved to Bob whose
    /// retained DN is Carol's, exactly.
    /// </summary>
    private static ConnectedSystemObject NewGroup()
    {
        var cso = new ConnectedSystemObject { Id = GroupId };
        void Add(ConnectedSystemObjectTypeAttribute attribute, Guid? referenceValueId, string dn) =>
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(), AttributeId = attribute.Id, Attribute = attribute,
                ReferenceValueId = referenceValueId, UnresolvedReferenceValue = dn
            });

        Add(CaseInsensitiveMember, AliceId, Dn("alice"));
        Add(CaseInsensitiveMember, BobId, Dn("carol").ToUpperInvariant());
        Add(CaseInsensitiveMember, null, Dn("dave").ToUpperInvariant());
        Add(CaseInsensitiveMember, null, Dn("erin"));
        Add(CaseInsensitiveMember, null, Dn("nİck"));
        Add(CaseInsensitiveMember, null, Dn("Kate"));
        Add(CaseExactMember, AliceId, Dn("alice"));
        Add(CaseExactMember, null, Dn("frank"));
        Add(CaseExactMember, null, Dn("josé"));
        Add(CaseExactMember, BobId, Dn("carol"));
        return cso;
    }

    private static PendingExportAttributeValueChange Change(ConnectedSystemObjectTypeAttribute attribute, PendingExportAttributeChangeType changeType,
        Guid? resolvedTo = null, string? stringValue = null, string? unresolvedValue = null) => new()
    {
        Id = Guid.NewGuid(), AttributeId = attribute.Id, Attribute = attribute, ChangeType = changeType,
        Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation,
        ResolvedReferenceCsoId = resolvedTo, StringValue = stringValue, UnresolvedReferenceValue = unresolvedValue
    };

    /// <summary>
    /// Every kind of change reconciliation judges on a multi-valued reference attribute, resolved and unresolved, in
    /// several cases, each with the verdict the full load gives (confirmed or not).
    /// </summary>
    private static List<(string Name, PendingExportAttributeValueChange Change, bool Confirmed)> ChangeCatalogue() =>
    [
        ("add alice, resolved", Change(CaseInsensitiveMember, PendingExportAttributeChangeType.Add, AliceId, Dn("alice")), true),
        ("add alice, resolved, DN in capitals", Change(CaseInsensitiveMember, PendingExportAttributeChangeType.Add, AliceId, Dn("alice").ToUpperInvariant()), true),
        ("add frank, resolved, absent", Change(CaseInsensitiveMember, PendingExportAttributeChangeType.Add, FrankId, Dn("frank")), false),
        ("remove carol, resolved; only a row resolved to Bob retains her DN", Change(CaseInsensitiveMember, PendingExportAttributeChangeType.Remove, CarolId, Dn("carol")), true),
        ("remove dave, resolved; unresolved in capitals", Change(CaseInsensitiveMember, PendingExportAttributeChangeType.Remove, DaveId, Dn("dave")), false),
        ("add erin, resolved, DN in capitals; unresolved row", Change(CaseInsensitiveMember, PendingExportAttributeChangeType.Add, ErinId, Dn("erin").ToUpperInvariant()), true),
        ("remove nick; stored with the dotted capital I", Change(CaseInsensitiveMember, PendingExportAttributeChangeType.Remove, NickId, Dn("nick")), false),
        ("remove kate; stored with the Kelvin sign", Change(CaseInsensitiveMember, PendingExportAttributeChangeType.Remove, KateId, Dn("kate")), false),
        ("remove all, values held", Change(CaseInsensitiveMember, PendingExportAttributeChangeType.RemoveAll), false),
        ("empty add, values held", Change(CaseInsensitiveMember, PendingExportAttributeChangeType.Add), false),
        ("whole-set update to alice, others held", Change(CaseInsensitiveMember, PendingExportAttributeChangeType.Update, AliceId, Dn("alice")), false),
        ("case-exact add alice, unresolved change", Change(CaseExactMember, PendingExportAttributeChangeType.Add, unresolvedValue: Dn("alice")), true),
        ("case-exact remove alice in capitals, unresolved change", Change(CaseExactMember, PendingExportAttributeChangeType.Remove, unresolvedValue: Dn("alice").ToUpperInvariant()), true),
        ("case-exact add frank, resolved", Change(CaseExactMember, PendingExportAttributeChangeType.Add, FrankId, Dn("frank")), true),
        ("case-exact remove jose, non-ASCII", Change(CaseExactMember, PendingExportAttributeChangeType.Remove, unresolvedValue: Dn("josé")), false),
        ("case-exact remove alice, resolved, DN in capitals", Change(CaseExactMember, PendingExportAttributeChangeType.Remove, AliceId, Dn("alice").ToUpperInvariant()), false),
        ("case-exact add frank, resolved, DN in capitals", Change(CaseExactMember, PendingExportAttributeChangeType.Add, FrankId, Dn("frank").ToUpperInvariant()), false),
        ("case-exact add carol, resolved; only a row resolved to Bob retains her DN", Change(CaseExactMember, PendingExportAttributeChangeType.Add, CarolId, Dn("carol")), false),
        ("case-exact remove jose, resolved, non-ASCII", Change(CaseExactMember, PendingExportAttributeChangeType.Remove, ErinId, Dn("josé")), false)
    ];

    /// <summary>
    /// Whether a change names no object but carries a string on a case-exact attribute: it matches a resolved row by the DN
    /// the row keeps (<see cref="SyncEngine.ReferenceRowMatches"/>), which the probe does not seek (#2871), so its pair
    /// loads in full.
    /// </summary>
    private static bool NamesNoObjectOnCaseExact(PendingExportAttributeValueChange change) =>
        change.Attribute.CaseExact && !change.ResolvedReferenceCsoId.HasValue &&
        (!string.IsNullOrEmpty(change.UnresolvedReferenceValue) || !string.IsNullOrEmpty(change.StringValue));

    private static PendingExport PendingExportFor(Guid csoId, IEnumerable<PendingExportAttributeValueChange> changes) => new()
    {
        Id = Guid.NewGuid(), ConnectedSystemObjectId = csoId, ChangeType = PendingExportChangeType.Update,
        Status = PendingExportStatus.Exported, AttributeValueChanges = new(changes.ToList())
    };

    /// <summary>
    /// Stands in for the database: answers each question as the probe's SQL does, by the referenced object's id among
    /// resolved rows, and by exact string and by lowered string among unresolved rows (folding as a database
    /// whose <c>lower()</c> passed the once-per-run check does), with the count capped at two when asked. With
    /// <paramref name="everyRow"/> it returns every row of the attribute instead: the widest superset.
    /// </summary>
    private static Dictionary<int, ProbedAttributeValues> Answer(ConnectedSystemObject fullCso, IEnumerable<ReconciliationProbeQuestion> questions,
        bool everyRow = false)
    {
        static string Lower(string value) => IdentifierKey.For(value, caseExact: false);

        var answers = new Dictionary<int, ProbedAttributeValues>();
        foreach (var question in questions.Where(q => q.ConnectedSystemObjectId == fullCso.Id))
        {
            var rows = fullCso.AttributeValues.Where(av => av.AttributeId == question.AttributeId).ToList();
            var candidates = everyRow
                ? rows
                : rows.Where(r =>
                    (r.ReferenceValueId.HasValue && question.ReferenceIds.Contains(r.ReferenceValueId.Value)) ||
                    (r.ReferenceValueId == null && r.UnresolvedReferenceValue != null && question.ExactValues.Contains(r.UnresolvedReferenceValue)) ||
                    (r.ReferenceValueId == null && r.UnresolvedReferenceValue != null &&
                     question.CaseInsensitiveValues.Any(v => Lower(v) == Lower(r.UnresolvedReferenceValue)))).ToList();

            answers[question.AttributeId] = new ProbedAttributeValues
            {
                ConnectedSystemObjectId = fullCso.Id,
                AttributeId = question.AttributeId,
                ProbedChangeIds = question.ChangeIds,
                CandidateValues = candidates.Select(r => new ConnectedSystemObjectAttributeValue
                {
                    Id = r.Id, AttributeId = r.AttributeId,
                    ReferenceValueId = r.ReferenceValueId, UnresolvedReferenceValue = r.UnresolvedReferenceValue
                }).ToList(),
                CappedValueCount = question.NeedsCount ? Math.Min(rows.Count, 2) : null
            };
        }

        return answers;
    }

    /// <summary>
    /// The object as the reconciliation load returns it once its probed attributes are left out.
    /// </summary>
    private static ConnectedSystemObject WithoutValuesOf(ConnectedSystemObject fullCso, IEnumerable<int> attributeIds)
    {
        var excluded = attributeIds.ToHashSet();
        var cso = new ConnectedSystemObject { Id = fullCso.Id };
        cso.AttributeValues.AddRange(fullCso.AttributeValues.Where(av => !excluded.Contains(av.AttributeId)));
        return cso;
    }

    #endregion

    #region Same verdicts as the full load

    /// <summary>
    /// For every change in the catalogue, alone and all together, reconciliation over the probed values reaches the
    /// verdict the full load reaches, whether the probe returns exactly what its SQL would or every row of the attribute.
    /// The planner probes every one of them but a case-exact change naming no object, whose pair, and only that pair,
    /// loads in full (#2871).
    /// </summary>
    [Test]
    public void ReconcileCsoAgainstPendingExport_OverProbedValues_SameVerdictsAsFullLoad([Values] bool everyRow)
    {
        var catalogue = ChangeCatalogue();
        var batches = catalogue.Select(entry => new[] { entry.Name }).Append(catalogue.Select(entry => entry.Name).ToArray());

        foreach (var batch in batches)
        {
            List<PendingExportAttributeValueChange> NewChanges()
            {
                var fresh = ChangeCatalogue().ToDictionary(e => e.Name, e => e.Change);
                var template = catalogue.ToDictionary(e => e.Name, e => e.Change);
                return batch.Select(name =>
                {
                    var change = fresh[name];
                    change.Id = template[name].Id;
                    return change;
                }).ToList();
            }

            var fullCso = NewGroup();
            var overFull = new PendingExportReconciliationResult();
            _engine.ReconcileCsoAgainstPendingExport(fullCso, PendingExportFor(fullCso.Id, NewChanges()), overFull);

            var probedExport = PendingExportFor(fullCso.Id, NewChanges());
            var plan = SyncEngine.PlanReconciliationProbes([probedExport], databaseFoldsCaseLikeIdentifierKey: true);
            var answers = Answer(fullCso, plan.Questions, everyRow);
            var overProbe = new PendingExportReconciliationResult();
            _engine.ReconcileCsoAgainstPendingExport(WithoutValuesOf(fullCso, answers.Keys), probedExport, overProbe,
                probedValuesByAttributeId: answers);

            var expectedConfirmed = catalogue.Where(e => batch.Contains(e.Name) && e.Confirmed).Select(e => e.Change.Id);
            var inBatch = catalogue.Where(e => batch.Contains(e.Name)).Select(e => e.Change).ToList();
            var fullLoadAttributeIds = inBatch.Where(NamesNoObjectOnCaseExact).Select(c => c.AttributeId).ToHashSet();
            var label = string.Join("; ", batch);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(plan.FullLoadPairCount, Is.EqualTo(fullLoadAttributeIds.Count), label);
                Assert.That(plan.Questions.SelectMany(q => q.ChangeIds),
                    Is.EquivalentTo(inBatch.Where(c => !fullLoadAttributeIds.Contains(c.AttributeId)).Select(c => c.Id)), label);
                Assert.That(overFull.ConfirmedChanges.Select(c => c.Id), Is.EquivalentTo(expectedConfirmed), "Full load: " + label);
                Assert.That(overProbe.ConfirmedChanges.Select(c => c.Id), Is.EquivalentTo(overFull.ConfirmedChanges.Select(c => c.Id)), "Confirmed: " + label);
                Assert.That(overProbe.RetryChanges.Select(c => c.Id), Is.EquivalentTo(overFull.RetryChanges.Select(c => c.Id)), "Retried: " + label);
                Assert.That(overProbe.PendingExportToDelete != null, Is.EqualTo(overFull.PendingExportToDelete != null), label);
                Assert.That(overProbe.PendingExportToUpdate?.Status, Is.EqualTo(overFull.PendingExportToUpdate?.Status), label);
            }
        }
    }

    /// <summary>
    /// The count decides a whole-set Update, a RemoveAll and an empty change: one value held, none held.
    /// </summary>
    [Test]
    public void ReconcileCsoAgainstPendingExport_OverProbedValuesWithCount_ConfirmsByCount()
    {
        var oneMember = new ConnectedSystemObject { Id = Guid.NewGuid() };
        oneMember.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), AttributeId = CaseInsensitiveMemberId, Attribute = CaseInsensitiveMember,
            ReferenceValueId = AliceId, UnresolvedReferenceValue = Dn("alice")
        });
        var noMembers = new ConnectedSystemObject { Id = Guid.NewGuid() };

        var update = Change(CaseInsensitiveMember, PendingExportAttributeChangeType.Update, AliceId, Dn("alice"));
        var removeAll = Change(CaseInsensitiveMember, PendingExportAttributeChangeType.RemoveAll);
        var emptyUpdate = Change(CaseInsensitiveMember, PendingExportAttributeChangeType.Update);

        var updateExport = PendingExportFor(oneMember.Id, [update]);
        var clearingExport = PendingExportFor(noMembers.Id, [removeAll, emptyUpdate]);
        var plan = SyncEngine.PlanReconciliationProbes([updateExport, clearingExport], databaseFoldsCaseLikeIdentifierKey: true);

        var overOne = new PendingExportReconciliationResult();
        _engine.ReconcileCsoAgainstPendingExport(WithoutValuesOf(oneMember, [CaseInsensitiveMemberId]), updateExport, overOne,
            probedValuesByAttributeId: Answer(oneMember, plan.Questions));
        var overNone = new PendingExportReconciliationResult();
        _engine.ReconcileCsoAgainstPendingExport(noMembers, clearingExport, overNone,
            probedValuesByAttributeId: Answer(noMembers, plan.Questions));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan.Questions.Select(q => q.NeedsCount), Is.All.True);
            Assert.That(overOne.ConfirmedChanges, Is.EquivalentTo(new[] { update }));
            Assert.That(overNone.ConfirmedChanges, Is.EquivalentTo(new[] { removeAll, emptyUpdate }));
        }
    }

    #endregion

    #region Planner

    private static ReconciliationProbePlan Plan(bool databaseFolds, params PendingExportAttributeValueChange[] changes) =>
        SyncEngine.PlanReconciliationProbes([PendingExportFor(GroupId, changes)], databaseFolds);

    [Test]
    public void PlanReconciliationProbes_ResolvedChangesOnCaseInsensitiveReference_AsksByIdAndLoweredDn()
    {
        var add = Change(CaseInsensitiveMember, PendingExportAttributeChangeType.Add, AliceId, Dn("alice"));
        var remove = Change(CaseInsensitiveMember, PendingExportAttributeChangeType.Remove, BobId, Dn("bob"));

        var plan = Plan(true, add, remove);

        Assert.That(plan.Questions, Has.Count.EqualTo(1));
        var question = plan.Questions[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(question.ConnectedSystemObjectId, Is.EqualTo(GroupId));
            Assert.That(question.AttributeId, Is.EqualTo(CaseInsensitiveMemberId));
            Assert.That(question.ChangeIds, Is.EquivalentTo(new[] { add.Id, remove.Id }));
            Assert.That(question.ReferenceIds, Is.EquivalentTo(new[] { AliceId, BobId }));
            Assert.That(question.CaseInsensitiveValues, Is.EquivalentTo(new[] { Dn("alice"), Dn("bob") }));
            Assert.That(question.ExactValues, Is.Empty);
            Assert.That(question.NeedsCount, Is.False);
            Assert.That(plan.FullLoadPairCount, Is.Zero);
        }
    }

    [Test]
    public void PlanReconciliationProbes_CountDependentChanges_AskForTheCount()
    {
        foreach (var change in new[]
                 {
                     Change(CaseInsensitiveMember, PendingExportAttributeChangeType.RemoveAll),
                     Change(CaseInsensitiveMember, PendingExportAttributeChangeType.Add),
                     Change(CaseInsensitiveMember, PendingExportAttributeChangeType.Update, AliceId, Dn("alice"))
                 })
        {
            var plan = Plan(true, change);
            Assert.That(plan.Questions.Single().NeedsCount, Is.True, change.ChangeType.ToString());
        }
    }

    /// <summary>
    /// Only multi-valued reference attributes are probed (#2669: the low-selectivity Text arm is out of scope); everything
    /// else loads as before, and is not counted as a reference falling back to the full load.
    /// </summary>
    [Test]
    public void PlanReconciliationProbes_NotMultiValuedReference_LoadsInFull()
    {
        var text = new ConnectedSystemObjectTypeAttribute { Id = 20, Name = "proxyAddresses", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued };
        var binary = new ConnectedSystemObjectTypeAttribute { Id = 21, Name = "userCertificate", Type = AttributeDataType.Binary, AttributePlurality = AttributePlurality.MultiValued };
        var dateTime = new ConnectedSystemObjectTypeAttribute { Id = 22, Name = "dates", Type = AttributeDataType.DateTime, AttributePlurality = AttributePlurality.MultiValued };
        var manager = new ConnectedSystemObjectTypeAttribute { Id = 23, Name = "manager", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.SingleValued };

        var plan = Plan(true,
            Change(text, PendingExportAttributeChangeType.Add, stringValue: "smtp:a@b.c"),
            new PendingExportAttributeValueChange { Id = Guid.NewGuid(), AttributeId = binary.Id, Attribute = binary, ChangeType = PendingExportAttributeChangeType.Add, ByteValue = [1], Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation },
            new PendingExportAttributeValueChange { Id = Guid.NewGuid(), AttributeId = dateTime.Id, Attribute = dateTime, ChangeType = PendingExportAttributeChangeType.Add, DateTimeValue = DateTime.UtcNow, Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation },
            Change(manager, PendingExportAttributeChangeType.Update, AliceId, Dn("alice")));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan.Questions, Is.Empty);
            Assert.That(plan.FullLoadPairCount, Is.Zero);
        }
    }

    /// <summary>
    /// A naming attribute, an External ID or a Secondary External ID is read beyond the verdict (the object's name, the
    /// execution item's snapshots), so it is never probed, whatever its type.
    /// </summary>
    [Test]
    public void PlanReconciliationProbes_NamingOrIdentifierAttribute_LoadsInFull()
    {
        var named = new ConnectedSystemObjectTypeAttribute { Id = 30, Name = "cn", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued };
        var externalId = new ConnectedSystemObjectTypeAttribute { Id = 31, Name = "refs", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued, IsExternalId = true };
        var secondaryExternalId = new ConnectedSystemObjectTypeAttribute { Id = 32, Name = "refs2", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued, IsSecondaryExternalId = true };

        var plan = Plan(true,
            Change(named, PendingExportAttributeChangeType.Add, AliceId, Dn("alice")),
            Change(externalId, PendingExportAttributeChangeType.Add, AliceId, Dn("alice")),
            Change(secondaryExternalId, PendingExportAttributeChangeType.Add, AliceId, Dn("alice")));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan.Questions, Is.Empty);
            Assert.That(plan.FullLoadPairCount, Is.EqualTo(3));
        }
    }

    /// <summary>
    /// A change naming no object matches resolved rows by the DN they retain, ignoring case, and no index finds a
    /// resolved row by its lowered DN; so its attribute loads in full for that object, its other changes with it.
    /// </summary>
    [Test]
    public void PlanReconciliationProbes_UnresolvedChangeOnCaseInsensitiveAttribute_LoadsThePairInFull()
    {
        var plan = Plan(true,
            Change(CaseInsensitiveMember, PendingExportAttributeChangeType.Add, AliceId, Dn("alice")),
            Change(CaseInsensitiveMember, PendingExportAttributeChangeType.Remove, unresolvedValue: Dn("bob")));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan.Questions, Is.Empty);
            Assert.That(plan.FullLoadPairCount, Is.EqualTo(1));
        }
    }

    /// <summary>
    /// Ignoring case, only ASCII is probed: a non-ASCII DN loads its pair in full, as does every case-insensitive DN when
    /// the database's folding disagrees with JIM's. A case-exact attribute compares exactly, so neither applies to it.
    /// </summary>
    [Test]
    public void PlanReconciliationProbes_NonAsciiOrDatabaseFoldingDisagrees_LoadsCaseInsensitivePairsInFull()
    {
        var nonAscii = Plan(true, Change(CaseInsensitiveMember, PendingExportAttributeChangeType.Remove, AliceId, Dn("josé")));
        var disagrees = Plan(false,
            Change(CaseInsensitiveMember, PendingExportAttributeChangeType.Remove, AliceId, Dn("alice")),
            Change(CaseExactMember, PendingExportAttributeChangeType.Remove, FrankId, Dn("josé")));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(nonAscii.Questions, Is.Empty);
            Assert.That(nonAscii.FullLoadPairCount, Is.EqualTo(1));
            Assert.That(disagrees.Questions.Select(q => q.AttributeId), Is.EquivalentTo(new[] { CaseExactMemberId }));
            Assert.That(disagrees.Questions.Single().ExactValues, Is.EquivalentTo(new[] { Dn("josé") }));
            Assert.That(disagrees.FullLoadPairCount, Is.EqualTo(1));
        }
    }

    /// <summary>
    /// A change naming no object matches resolved rows by the DN they keep (<see cref="SyncEngine.ReferenceRowMatches"/>), on
    /// a case-exact attribute as on any other, and the probe seeks strings among unresolved rows only (#2871): so its
    /// attribute loads in full for that object, its other changes with it, whichever of its strings it carries.
    /// </summary>
    [Test]
    public void PlanReconciliationProbes_CaseExactChangeNamingNoObject_LoadsThePairInFull()
    {
        var unresolved = Plan(true,
            Change(CaseExactMember, PendingExportAttributeChangeType.Add, FrankId, Dn("frank")),
            Change(CaseExactMember, PendingExportAttributeChangeType.Remove, unresolvedValue: Dn("alice")));
        var stringOnly = Plan(true, Change(CaseExactMember, PendingExportAttributeChangeType.Remove, stringValue: Dn("alice")));

        using (Assert.EnterMultipleScope())
        {
            foreach (var plan in new[] { unresolved, stringOnly })
            {
                Assert.That(plan.Questions, Is.Empty);
                Assert.That(plan.FullLoadPairCount, Is.EqualTo(1));
                Assert.That(plan.FullLoadPairCountsByReason, Is.EquivalentTo(new Dictionary<ReconciliationProbeFallbackReason, int>
                {
                    [ReconciliationProbeFallbackReason.NamesNoObject] = 1
                }));
            }
        }
    }

    /// <summary>
    /// A case-exact change naming an object is asked about by that object's id and by its strings exactly, whatever their
    /// characters and however the database folds case, since nothing is compared ignoring case.
    /// </summary>
    [Test]
    public void PlanReconciliationProbes_CaseExactChangesNamingObjects_AskByIdAndExactDn()
    {
        var add = Change(CaseExactMember, PendingExportAttributeChangeType.Add, AliceId, Dn("alice"));
        var remove = Change(CaseExactMember, PendingExportAttributeChangeType.Remove, FrankId, Dn("josé"));

        var plan = Plan(false, add, remove);

        Assert.That(plan.Questions, Has.Count.EqualTo(1));
        var question = plan.Questions[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(question.ChangeIds, Is.EquivalentTo(new[] { add.Id, remove.Id }));
            Assert.That(question.ReferenceIds, Is.EquivalentTo(new[] { AliceId, FrankId }));
            Assert.That(question.ExactValues, Is.EquivalentTo(new[] { Dn("alice"), Dn("josé") }));
            Assert.That(question.CaseInsensitiveValues, Is.Empty);
            Assert.That(plan.FullLoadPairCount, Is.Zero);
        }
    }

    /// <summary>
    /// Each pair loaded in full is counted by why (#2809), so a run's log can say why a page fell back rather than only how
    /// many pairs did: the first change that cannot be put as a question decides its pair's reason.
    /// </summary>
    [Test]
    public void PlanReconciliationProbes_PairsLoadedInFull_CountedByReason()
    {
        var named = new ConnectedSystemObjectTypeAttribute { Id = 30, Name = "cn", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued };
        var exports = new[]
        {
            PendingExportFor(Guid.NewGuid(), [Change(named, PendingExportAttributeChangeType.Add, AliceId, Dn("alice"))]),
            PendingExportFor(Guid.NewGuid(), [Change(CaseInsensitiveMember, PendingExportAttributeChangeType.Remove, stringValue: Dn("bob"))]),
            PendingExportFor(Guid.NewGuid(), [Change(CaseInsensitiveMember, PendingExportAttributeChangeType.Remove, unresolvedValue: Dn("carol"))]),
            PendingExportFor(Guid.NewGuid(), [Change(CaseInsensitiveMember, PendingExportAttributeChangeType.Remove, DaveId, Dn("josé"))]),
            PendingExportFor(Guid.NewGuid(), [Change(CaseInsensitiveMember, PendingExportAttributeChangeType.Remove, ErinId, Dn("erin"))])
        };

        var folds = SyncEngine.PlanReconciliationProbes(exports, databaseFoldsCaseLikeIdentifierKey: true);
        var disagrees = SyncEngine.PlanReconciliationProbes(exports.Skip(4), databaseFoldsCaseLikeIdentifierKey: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(folds.FullLoadPairCount, Is.EqualTo(4));
            Assert.That(folds.FullLoadPairCountsByReason, Is.EquivalentTo(new Dictionary<ReconciliationProbeFallbackReason, int>
            {
                [ReconciliationProbeFallbackReason.IdentifierOrNamingAttribute] = 1,
                [ReconciliationProbeFallbackReason.NamesNoObject] = 2,
                [ReconciliationProbeFallbackReason.NonAsciiValue] = 1
            }));
            Assert.That(disagrees.FullLoadPairCountsByReason, Is.EquivalentTo(new Dictionary<ReconciliationProbeFallbackReason, int>
            {
                [ReconciliationProbeFallbackReason.DatabaseFoldsCaseDifferently] = 1
            }));
        }
    }

    /// <summary>
    /// A change whose attribute definition is not loaded cannot be planned for, so its pair loads in full; and only the
    /// changes awaiting confirmation are asked about.
    /// </summary>
    [Test]
    public void PlanReconciliationProbes_UnknownAttributeOrNotAwaitingConfirmation_HandledSafely()
    {
        var unknown = Change(CaseInsensitiveMember, PendingExportAttributeChangeType.Add, AliceId, Dn("alice"));
        unknown.Attribute = null!;
        var known = Change(CaseInsensitiveMember, PendingExportAttributeChangeType.Add, BobId, Dn("bob"));
        var notSent = Change(CaseExactMember, PendingExportAttributeChangeType.Add, BobId, Dn("bob"));
        notSent.Status = PendingExportAttributeChangeStatus.Pending;

        var plan = Plan(true, unknown, known, notSent);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan.Questions, Is.Empty);
            Assert.That(plan.FullLoadPairCount, Is.EqualTo(1));
        }
    }

    #endregion

    #region A reference recall removal (#2809)

    /// <summary>
    /// What a group's member attribute holds when a leaver's recall removal is confirmed.
    /// </summary>
    public enum LeaverRow
    {
        /// <summary>The leaver is still a member: a row resolved to the leaver's object.</summary>
        StillResolvedToTheLeaver,

        /// <summary>The leaver has gone; only another member is held.</summary>
        Absent,

        /// <summary>The leaver is still listed, by a DN in another case that resolved to nothing (as deleting the leaver's
        /// object leaves the row).</summary>
        UnresolvedInAnotherCase,

        /// <summary>A stale row resolved to another object, keeping the leaver's DN.</summary>
        ResolvedToAnotherObjectWithTheLeaversDn
    }

    private static readonly Guid LeaverCsoId = Guid.NewGuid();

    /// <summary>
    /// The removal reference recall stages for a leaver, as the recall decision makes it, sent and awaiting the confirming
    /// import.
    /// </summary>
    private PendingExportAttributeValueChange RecallRemovalOfTheLeaver()
    {
        var flow = new ReferenceRecallDirectFlow
        {
            ExportRule = new JIM.Models.Logic.SyncRule { Id = 1, Name = "Group Export", ConnectedSystemId = 1 },
            TargetAttribute = CaseInsensitiveMember,
            SourcePlurality = AttributePlurality.MultiValued
        };
        var change = _engine.DecideRecallRemovalChange(flow, new ResolvedRecallReference(LeaverCsoId, Dn("lena")))!;
        change.Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation;
        return change;
    }

    private static ConnectedSystemObject GroupHolding(LeaverRow leaverRow)
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid() };
        void Add(Guid? referenceValueId, string dn) => cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), AttributeId = CaseInsensitiveMemberId, Attribute = CaseInsensitiveMember,
            ReferenceValueId = referenceValueId, UnresolvedReferenceValue = dn
        });

        Add(AliceId, Dn("alice"));
        switch (leaverRow)
        {
            case LeaverRow.StillResolvedToTheLeaver:
                Add(LeaverCsoId, Dn("lena"));
                break;
            case LeaverRow.UnresolvedInAnotherCase:
                Add(null, Dn("lena").ToUpperInvariant());
                break;
            case LeaverRow.ResolvedToAnotherObjectWithTheLeaversDn:
                Add(BobId, Dn("lena"));
                break;
        }

        return cso;
    }

    /// <summary>
    /// A leaver's recall removal on a case-insensitive member attribute is put to the database as a question, by the
    /// leaver's object among resolved rows and by the lowered DN among unresolved ones, so the group's members are not
    /// loaded to confirm it (#2809: on the 500,000-user run no leaver group probed, and loading them cost 14.9 s).
    /// </summary>
    [Test]
    public void PlanReconciliationProbes_RecallRemovalOnCaseInsensitiveReference_AsksByTheLeaversObjectAndLoweredDn()
    {
        var removal = RecallRemovalOfTheLeaver();

        var plan = Plan(true, removal);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan.FullLoadPairCount, Is.Zero, "The leaver group's member attribute is not loaded in full");
            Assert.That(plan.Questions, Has.Count.EqualTo(1));
            Assert.That(plan.Questions.Single().ReferenceIds, Is.EquivalentTo(new[] { LeaverCsoId }));
            Assert.That(plan.Questions.Single().CaseInsensitiveValues, Is.EquivalentTo(new[] { Dn("lena") }));
        }
    }

    /// <summary>
    /// Over the probe and over the full load alike, the leaver's removal is confirmed only once the group holds no row
    /// naming the leaver: not while a row resolved to the leaver's object remains, nor while an unresolved row carries the
    /// leaver's DN in another case; and a stale row resolved to another object names that object, whatever DN it keeps.
    /// </summary>
    [Test]
    public void ReconcileCsoAgainstPendingExport_RecallRemoval_SameVerdictOverProbeAndFullLoad([Values] LeaverRow leaverRow, [Values] bool everyRow)
    {
        var expectedConfirmed = leaverRow is LeaverRow.Absent or LeaverRow.ResolvedToAnotherObjectWithTheLeaversDn;

        var fullCso = GroupHolding(leaverRow);
        var fullRemoval = RecallRemovalOfTheLeaver();
        var overFull = new PendingExportReconciliationResult();
        _engine.ReconcileCsoAgainstPendingExport(fullCso, PendingExportFor(fullCso.Id, [fullRemoval]), overFull);

        var probedRemoval = RecallRemovalOfTheLeaver();
        var probedExport = PendingExportFor(fullCso.Id, [probedRemoval]);
        var plan = SyncEngine.PlanReconciliationProbes([probedExport], databaseFoldsCaseLikeIdentifierKey: true);
        var answers = Answer(fullCso, plan.Questions, everyRow);
        var overProbe = new PendingExportReconciliationResult();
        _engine.ReconcileCsoAgainstPendingExport(WithoutValuesOf(fullCso, answers.Keys), probedExport, overProbe,
            probedValuesByAttributeId: answers);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan.FullLoadPairCount, Is.Zero, "Probed, not loaded");
            Assert.That(answers.Keys, Is.EquivalentTo(new[] { CaseInsensitiveMemberId }));
            Assert.That(overFull.ConfirmedChanges.Count == 1, Is.EqualTo(expectedConfirmed), "Full load");
            Assert.That(overProbe.ConfirmedChanges.Count == 1, Is.EqualTo(expectedConfirmed), "Probe");
            Assert.That(overProbe.RetryChanges.Count, Is.EqualTo(overFull.RetryChanges.Count));
            Assert.That(overProbe.PendingExportToDelete != null, Is.EqualTo(overFull.PendingExportToDelete != null));
        }
    }

    #endregion

    #region Fails fast

    [Test]
    public void ReconcileCsoAgainstPendingExport_ChangeNotAmongThoseProbed_Throws()
    {
        var change = Change(CaseInsensitiveMember, PendingExportAttributeChangeType.Remove, AliceId, Dn("alice"));
        var probed = new Dictionary<int, ProbedAttributeValues>
        {
            [CaseInsensitiveMemberId] = new()
            {
                ConnectedSystemObjectId = GroupId, AttributeId = CaseInsensitiveMemberId,
                ProbedChangeIds = new HashSet<Guid> { Guid.NewGuid() }, CandidateValues = []
            }
        };

        Assert.That(() => _engine.ReconcileCsoAgainstPendingExport(new ConnectedSystemObject { Id = GroupId }, PendingExportFor(GroupId, [change]),
            new PendingExportReconciliationResult(), probedValuesByAttributeId: probed), Throws.TypeOf<ReconciliationProbeIncompleteException>());
    }

    [Test]
    public void ReconcileCsoAgainstPendingExport_CountDependentChangeWithoutCount_Throws()
    {
        var change = Change(CaseInsensitiveMember, PendingExportAttributeChangeType.RemoveAll);
        var probed = new Dictionary<int, ProbedAttributeValues>
        {
            [CaseInsensitiveMemberId] = new()
            {
                ConnectedSystemObjectId = GroupId, AttributeId = CaseInsensitiveMemberId,
                ProbedChangeIds = new HashSet<Guid> { change.Id }, CandidateValues = []
            }
        };

        Assert.That(() => _engine.ReconcileCsoAgainstPendingExport(new ConnectedSystemObject { Id = GroupId }, PendingExportFor(GroupId, [change]),
            new PendingExportReconciliationResult(), probedValuesByAttributeId: probed), Throws.TypeOf<ReconciliationProbeIncompleteException>());
    }

    #endregion

    #region What the import showed

    /// <summary>
    /// A change not confirmed over probed values records what the import showed for that change only: the rows matching
    /// it, or that its value is absent, never the attribute's whole value set (#2669).
    /// </summary>
    [Test]
    public void ReconcileCsoAgainstPendingExport_OverProbedValuesNotConfirmed_RecordsBoundedLastImportedValue()
    {
        var fullCso = NewGroup();
        var removeDave = Change(CaseInsensitiveMember, PendingExportAttributeChangeType.Remove, DaveId, Dn("dave"));
        var addFrank = Change(CaseInsensitiveMember, PendingExportAttributeChangeType.Add, FrankId, Dn("frank"));
        var removeAll = Change(CaseInsensitiveMember, PendingExportAttributeChangeType.RemoveAll);
        var export = PendingExportFor(fullCso.Id, [removeDave, addFrank, removeAll]);
        var plan = SyncEngine.PlanReconciliationProbes([export], databaseFoldsCaseLikeIdentifierKey: true);

        var result = new PendingExportReconciliationResult();
        _engine.ReconcileCsoAgainstPendingExport(WithoutValuesOf(fullCso, [CaseInsensitiveMemberId]), export, result,
            probedValuesByAttributeId: Answer(fullCso, plan.Questions));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.RetryChanges, Is.EquivalentTo(new[] { removeDave, addFrank, removeAll }));
            Assert.That(removeDave.LastImportedValue, Is.EqualTo(Dn("dave").ToUpperInvariant()));
            Assert.That(addFrank.LastImportedValue, Is.EqualTo("(value not present)"));
            Assert.That(removeAll.LastImportedValue, Is.EqualTo("(2 or more values)"));
        }
    }

    #endregion
}
