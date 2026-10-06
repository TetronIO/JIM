// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.UniqueValues;
using JIM.Models.Logic;
using NUnit.Framework;
using InMemorySyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests.UniqueValues;

/// <summary>
/// The ProbeGate (Unique Value Generation, #242, release 3, plan Phase 7 item 4): last in the gate chain, so a local
/// hit never costs a probe; Found moves to the next candidate with rejection text naming the Connected System;
/// NotFound and CouldNotDetermine accept; only-if-taken tokens probe a window of up to ten candidates at once while
/// sequence and random tokens probe only what they drew; the object's own values are exempt; and a dry run never
/// probes.
/// </summary>
[TestFixture]
public class UniqueValueGenerationServerProbeGateTests
{
    private const int CorporateAdId = 7;
    private const int PartnerLdapId = 8;
    private const int SamAccountNameId = 700;
    private const int UidId = 800;

    private static readonly UniquenessProbeTarget CorporateAd = new(CorporateAdId, SamAccountNameId);
    private static readonly UniquenessProbeTarget PartnerLdap = new(PartnerLdapId, UidId);

    // ---- Ordering ----

    [Test]
    public async Task ResolveAsync_CandidateTakenLocally_IsNeverProbedAsync()
    {
        var attributeId = UniqueValueTestHelpers.NextAttributeId();
        var reservations = new UniqueValueReservationSet();
        reservations.TryReserve(Guid.NewGuid(), UniqueValueScope.MetaverseAttribute, attributeId, "joe.bloggs");
        var session = new FakeUniquenessProbeSession().WithSystem(CorporateAdId, "Corporate AD");

        var request = Request(UniqueValueTestHelpers.Generation(attemptLimit: 3), attributeId, CorporateAd);
        var outcomes = await Server().ResolveAsync([request], Options(session, reservations));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes[0].Value, Is.EqualTo("joe.bloggs1"));
            Assert.That(session.Calls.SelectMany(c => c.Candidates), Does.Not.Contain("joe.bloggs"),
                "a candidate a local gate rejected must never reach the probe");
        }
    }

    [Test]
    public async Task ResolveAsync_NoProbeTargets_NeverProbesAsync()
    {
        var session = new FakeUniquenessProbeSession().WithSystem(CorporateAdId, "Corporate AD", "joe.bloggs");
        var request = UniqueValueTestHelpers.ImportRequest(UniqueValueTestHelpers.Generation(), UniqueValueTestHelpers.NextAttributeId());

        var outcomes = await Server().ResolveAsync([request], Options(session));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes[0].Value, Is.EqualTo("joe.bloggs"));
            Assert.That(session.Calls, Is.Empty);
        }
    }

    // ---- Outcomes ----

    [Test]
    public async Task ResolveAsync_ProbeFindsTheCandidate_MovesToTheNextOneAsync()
    {
        var session = new FakeUniquenessProbeSession().WithSystem(CorporateAdId, "Corporate AD", "joe.bloggs");

        var outcomes = await Server().ResolveAsync([Request(UniqueValueTestHelpers.Generation(), UniqueValueTestHelpers.NextAttributeId(), CorporateAd)], Options(session));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes[0].Kind, Is.EqualTo(GenerationOutcomeKind.Generated));
            Assert.That(outcomes[0].Value, Is.EqualTo("joe.bloggs1"));
            Assert.That(session.ValuesChosenWithoutProbe, Is.Empty);
        }
    }

    [Test]
    public async Task ResolveAsync_ProbeFindsEveryCandidate_ExhaustsNamingTheConnectedSystemAsync()
    {
        var session = new FakeUniquenessProbeSession().WithSystem(CorporateAdId, "Corporate AD", "joe.bloggs", "joe.bloggs1");

        var outcomes = await Server().ResolveAsync([Request(UniqueValueTestHelpers.Generation(attemptLimit: 2), UniqueValueTestHelpers.NextAttributeId(), CorporateAd)], Options(session));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes[0].Kind, Is.EqualTo(GenerationOutcomeKind.Exhausted));
            Assert.That(outcomes[0].FailureMessage, Does.Contain("an account in Corporate AD"));
        }
    }

    [Test]
    public async Task ResolveAsync_SecondTargetFindsTheCandidate_RejectsItAsync()
    {
        var session = new FakeUniquenessProbeSession()
            .WithSystem(CorporateAdId, "Corporate AD")
            .WithSystem(PartnerLdapId, "Partner LDAP", "joe.bloggs");

        var outcomes = await Server().ResolveAsync([Request(UniqueValueTestHelpers.Generation(), UniqueValueTestHelpers.NextAttributeId(), CorporateAd, PartnerLdap)], Options(session));

        Assert.That(outcomes[0].Value, Is.EqualTo("joe.bloggs1"));
    }

    [Test]
    public async Task ResolveAsync_ProbeCouldNotDetermine_AcceptsAndRecordsTheValueAsChosenWithoutAProbeAsync()
    {
        var session = new FakeUniquenessProbeSession().WithUndeterminedSystem(CorporateAdId, "Corporate AD");

        var outcomes = await Server().ResolveAsync([Request(UniqueValueTestHelpers.Generation(), UniqueValueTestHelpers.NextAttributeId(), CorporateAd)], Options(session));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes[0].Kind, Is.EqualTo(GenerationOutcomeKind.Generated));
            Assert.That(outcomes[0].Value, Is.EqualTo("joe.bloggs"));
            Assert.That(session.ValuesChosenWithoutProbe, Is.EqualTo(new[] { CorporateAdId }));
        }
    }

    [Test]
    public async Task ResolveAsync_SystemNotProbed_AcceptsWithoutRecordingAnythingAsync()
    {
        var session = new FakeUniquenessProbeSession().WithNotProbedSystem(CorporateAdId, "Corporate AD");

        var outcomes = await Server().ResolveAsync([Request(UniqueValueTestHelpers.Generation(), UniqueValueTestHelpers.NextAttributeId(), CorporateAd)], Options(session));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes[0].Value, Is.EqualTo("joe.bloggs"));
            Assert.That(session.ValuesChosenWithoutProbe, Is.Empty,
                "a system that is not probed at all is checked against JIM's own records, as before release 3, and is not reported");
        }
    }

    // ---- Window batching ----

    [Test]
    public async Task ResolveAsync_OnlyIfTaken_ProbesAWindowOfTenAndAnswersLaterRoundsFromItAsync()
    {
        var session = new FakeUniquenessProbeSession().WithSystem(CorporateAdId, "Corporate AD", "joe.bloggs", "joe.bloggs1", "joe.bloggs2");

        var outcomes = await Server().ResolveAsync([Request(UniqueValueTestHelpers.Generation(), UniqueValueTestHelpers.NextAttributeId(), CorporateAd)], Options(session));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes[0].Value, Is.EqualTo("joe.bloggs3"));
            Assert.That(session.Calls, Has.Count.EqualTo(1), "one batch answers every round within the window");
            Assert.That(session.Calls[0].Candidates, Is.EqualTo(new[]
            {
                "joe.bloggs", "joe.bloggs1", "joe.bloggs2", "joe.bloggs3", "joe.bloggs4",
                "joe.bloggs5", "joe.bloggs6", "joe.bloggs7", "joe.bloggs8", "joe.bloggs9"
            }));
        }
    }

    [Test]
    public async Task ResolveAsync_OnlyIfTakenWindowExhausted_ProbesTheNextWindowAsync()
    {
        var taken = Enumerable.Range(0, 10).Select(i => i == 0 ? "joe.bloggs" : $"joe.bloggs{i}").ToArray();
        var session = new FakeUniquenessProbeSession().WithSystem(CorporateAdId, "Corporate AD", taken);

        var outcomes = await Server().ResolveAsync([Request(UniqueValueTestHelpers.Generation(), UniqueValueTestHelpers.NextAttributeId(), CorporateAd)], Options(session));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes[0].Value, Is.EqualTo("joe.bloggs10"));
            Assert.That(session.Calls, Has.Count.EqualTo(2));
            Assert.That(session.Calls[1].Candidates[0], Is.EqualTo("joe.bloggs10"));
        }
    }

    [Test]
    public async Task ResolveAsync_OnlyIfTakenWindow_IsCappedByTheAttemptLimitAsync()
    {
        var session = new FakeUniquenessProbeSession().WithSystem(CorporateAdId, "Corporate AD");

        await Server().ResolveAsync([Request(UniqueValueTestHelpers.Generation(attemptLimit: 4), UniqueValueTestHelpers.NextAttributeId(), CorporateAd)], Options(session));

        Assert.That(session.Calls[0].Candidates, Has.Count.EqualTo(4));
    }

    /// <summary>
    /// Drawing ahead would consume sequence numbers the run then never issues, so a sequence token probes only the
    /// number it drew.
    /// </summary>
    [Test]
    public async Task ResolveAsync_SequenceToken_ProbesOnlyTheDrawnCandidateAsync()
    {
        var session = new FakeUniquenessProbeSession().WithSystem(CorporateAdId, "Corporate AD", "u1");
        var generation = UniqueValueTestHelpers.Generation(tokenKind: GeneratedValueTokenKind.Sequence);
        var request = Request(generation, UniqueValueTestHelpers.NextAttributeId(), CorporateAd) with { BaseValue = "u" };

        var outcomes = await new UniqueValueGenerationServer(new InMemorySyncRepository()).ResolveAsync([request], Options(session));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes[0].Value, Is.EqualTo("u2"));
            Assert.That(session.Calls, Has.Count.EqualTo(2));
            Assert.That(session.Calls.Select(c => c.Candidates.Count), Is.All.EqualTo(1));
        }
    }

    [Test]
    public async Task ResolveAsync_RandomToken_ProbesOnlyTheDrawnCandidateAsync()
    {
        var session = new FakeUniquenessProbeSession().WithSystem(CorporateAdId, "Corporate AD");
        var generation = UniqueValueTestHelpers.Generation(tokenKind: GeneratedValueTokenKind.Random);

        var outcomes = await Server().ResolveAsync([Request(generation, UniqueValueTestHelpers.NextAttributeId(), CorporateAd)], Options(session));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(session.Calls, Has.Count.EqualTo(1));
            Assert.That(session.Calls[0].Candidates, Is.EqualTo(new[] { outcomes[0].Value }));
        }
    }

    // ---- Exemptions ----

    /// <summary>
    /// The object's own joined account holding the value is the same person, not a collision: the value is not
    /// probed at all, so the directory's answer (which would find that very account) cannot reject it.
    /// </summary>
    [Test]
    public async Task ResolveAsync_CandidateHeldByTheObjectsOwnAccount_IsNotProbedAndIsAcceptedAsync()
    {
        var session = new FakeUniquenessProbeSession().WithSystem(CorporateAdId, "Corporate AD", "joe.bloggs");
        var request = Request(UniqueValueTestHelpers.Generation(), UniqueValueTestHelpers.NextAttributeId(), CorporateAd)
            with { ProbeExemptValues = ["JOE.BLOGGS"] };

        var outcomes = await Server().ResolveAsync([request], Options(session));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes[0].Value, Is.EqualTo("joe.bloggs"));
            Assert.That(session.Calls.SelectMany(c => c.Candidates), Does.Not.Contain("joe.bloggs").IgnoreCase);
        }
    }

    [Test]
    public async Task ResolveAsync_ExemptValueInsideTheWindow_IsLeftOutOfTheBatchAsync()
    {
        var session = new FakeUniquenessProbeSession().WithSystem(CorporateAdId, "Corporate AD", "joe.bloggs");
        var request = Request(UniqueValueTestHelpers.Generation(), UniqueValueTestHelpers.NextAttributeId(), CorporateAd)
            with { ProbeExemptValues = ["joe.bloggs2"] };

        await Server().ResolveAsync([request], Options(session));

        Assert.That(session.Calls[0].Candidates, Does.Not.Contain("joe.bloggs2"));
    }

    [Test]
    public async Task ResolveAsync_ExportModeCurrentTargetValue_IsExemptAsync()
    {
        var session = new FakeUniquenessProbeSession().WithSystem(CorporateAdId, "Corporate AD", "joe.bloggs");
        var request = UniqueValueTestHelpers.ExportRequest(UniqueValueTestHelpers.Generation(), SamAccountNameId, Guid.NewGuid())
            with { ProbeTargets = [CorporateAd], ProbeExemptValues = ["joe.bloggs"] };

        var outcomes = await Server().ResolveAsync([request], Options(session));

        Assert.That(outcomes[0].Value, Is.EqualTo("joe.bloggs"));
    }

    // ---- Dry run ----

    [Test]
    public async Task ResolveAsync_DryRun_NeverProbesAsync()
    {
        var session = new FakeUniquenessProbeSession().WithSystem(CorporateAdId, "Corporate AD", "joe.bloggs");
        var options = UniqueValueTestHelpers.Options(dryRun: true);
        options.ProbeSession = session;

        var outcomes = await Server().ResolveAsync([Request(UniqueValueTestHelpers.Generation(), UniqueValueTestHelpers.NextAttributeId(), CorporateAd)], options);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes[0].Value, Is.EqualTo("joe.bloggs"), "Sync Preview checks the local gates only");
            Assert.That(session.Calls, Is.Empty);
        }
    }

    private static UniqueValueGenerationServer Server() => new(new InMemorySyncRepository());

    private static GenerationRequest Request(SyncRuleMappingGeneration generation, int attributeId, params UniquenessProbeTarget[] targets) =>
        UniqueValueTestHelpers.ImportRequest(generation, attributeId) with { ProbeTargets = targets };

    private static UniqueValueResolveOptions Options(FakeUniquenessProbeSession session, UniqueValueReservationSet? reservations = null)
    {
        var options = UniqueValueTestHelpers.Options(reservations);
        options.ProbeSession = session;
        return options;
    }
}
