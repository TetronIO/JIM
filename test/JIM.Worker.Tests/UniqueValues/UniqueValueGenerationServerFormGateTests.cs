// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.UniqueValues;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.Models.Transactional;
using NUnit.Framework;
using InMemorySyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests.UniqueValues;

/// <summary>
/// Every gate checks a candidate's forms with it (Generated Value Forms, #2023, plan Phase 3): a candidate is issued only
/// if every value built from it is free too, in JIM's records, in each Connected System it is written to, and where the
/// probe can see; it is then claimed with its forms, all or none. A form that cannot be worked out yet holds the
/// generated value back, and one whose expression fails fails the object without leaving anything claimed.
/// </summary>
[TestFixture]
public class UniqueValueGenerationServerFormGateTests
{
    private const int CorporateAdId = 7;
    private const int SamAccountNameId = 700;
    private const int MailId = 701;

    private int _accountNameId;
    private int _emailId;
    private InMemorySyncRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        _accountNameId = UniqueValueTestHelpers.NextAttributeId();
        _emailId = UniqueValueTestHelpers.NextAttributeId();
        _repository = new InMemorySyncRepository();
    }

    // ---- JIM's own records ----

    [Test]
    public async Task ResolveAsync_FormHeldByAnotherMetaverseObject_PassesTheCandidateOverAndSaysWhyAsync()
    {
        SeedMetaverseObjectHolding(_emailId, "joe.bloggs@corp.local");

        var outcome = (await Server().ResolveAsync([Request(new FakeFormSource(_emailId))], UniqueValueTestHelpers.Options()))[0];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.Kind, Is.EqualTo(GenerationOutcomeKind.Generated));
            Assert.That(outcome.Value, Is.EqualTo("joe.bloggs1"));
            Assert.That(outcome.SkippedCandidates, Is.EqualTo(new[] { "joe.bloggs: Email (built from Account Name) is already held by another Metaverse Object" }));
        }
    }

    [Test]
    public async Task ResolveAsync_FormHeldInAConnectedSystem_PassesTheCandidateOverAsync()
    {
        SeedAccountHolding(MailId, "joe.bloggs@corp.local", metaverseObjectId: Guid.NewGuid());

        var outcome = (await Server().ResolveAsync([Request(new FakeFormSource(_emailId) { ConnectorSpaceAttributeIds = [MailId] })], UniqueValueTestHelpers.Options()))[0];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.Value, Is.EqualTo("joe.bloggs1"));
            Assert.That(outcome.SkippedCandidates.Single(), Does.EndWith("is already held by a Connected System Object"));
        }
    }

    [Test]
    public async Task ResolveAsync_FormHeldByTheObjectsOwnAccount_IsNoCollisionAsync()
    {
        var mvoId = Guid.NewGuid();
        SeedAccountHolding(MailId, "joe.bloggs@corp.local", metaverseObjectId: mvoId);

        var outcome = (await Server().ResolveAsync([Request(new FakeFormSource(_emailId) { ConnectorSpaceAttributeIds = [MailId] }, mvoId)], UniqueValueTestHelpers.Options()))[0];

        Assert.That(outcome.Value, Is.EqualTo("joe.bloggs"));
    }

    [Test]
    public async Task ResolveAsync_FormAssignedAsAGeneratedValueElsewhere_PassesTheCandidateOverAsync()
    {
        _repository.SeedGeneratedValueAssignment(new GeneratedValueAssignment
        {
            Id = Guid.NewGuid(), MetaverseObjectId = Guid.NewGuid(), MetaverseAttributeId = _emailId,
            Value = "joe.bloggs@corp.local", NormalisedValue = "joe.bloggs@corp.local", State = GeneratedValueAssignmentState.Committed,
            SyncRuleMappingGenerationId = UniqueValueTestHelpers.Generation().Id
        });

        var outcome = (await Server().ResolveAsync([Request(new FakeFormSource(_emailId))], UniqueValueTestHelpers.Options()))[0];

        Assert.That(outcome.Value, Is.EqualTo("joe.bloggs1"));
    }

    [Test]
    public async Task ResolveAsync_FormClaimedByAnotherObjectInTheRun_PassesTheCandidateOverAsync()
    {
        var options = UniqueValueTestHelpers.Options();
        options.Reservations.TryReserve(Guid.NewGuid(), UniqueValueScope.MetaverseAttribute, _emailId, "joe.bloggs@corp.local");

        var outcome = (await Server().ResolveAsync([Request(new FakeFormSource(_emailId))], options))[0];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.Value, Is.EqualTo("joe.bloggs1"));
            Assert.That(outcome.SkippedCandidates.Single(), Does.EndWith("is already held by another object in this run"));
        }
    }

    [Test]
    public async Task ResolveAsync_FormATargetRefused_PassesTheCandidateOverAsync()
    {
        var outcome = (await Server().ResolveAsync([Request(new FakeFormSource(_emailId) { RejectedValues = ["joe.bloggs@corp.local"] })], UniqueValueTestHelpers.Options()))[0];

        Assert.That(outcome.Value, Is.EqualTo("joe.bloggs1"));
    }

    [Test]
    public async Task ResolveAsync_EveryCandidatesFormTaken_ExhaustsNamingTheFormAsync()
    {
        SeedMetaverseObjectHolding(_emailId, "joe.bloggs@corp.local");
        SeedMetaverseObjectHolding(_emailId, "joe.bloggs1@corp.local");

        var outcome = (await Server().ResolveAsync([Request(new FakeFormSource(_emailId), attemptLimit: 2)], UniqueValueTestHelpers.Options()))[0];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.Kind, Is.EqualTo(GenerationOutcomeKind.Exhausted));
            Assert.That(outcome.FailureMessage, Does.Contain("the last candidate, \"joe.bloggs1\", was passed over because its Email (built from Account Name), \"joe.bloggs1@corp.local\", is already held by another Metaverse Object"));
        }
    }

    // ---- Claimed together ----

    [Test]
    public async Task ResolveAsync_Issued_ClaimsTheCandidateAndItsFormsTogetherAsync()
    {
        var options = UniqueValueTestHelpers.Options();

        await Server().ResolveAsync([Request(new FakeFormSource(_emailId))], options);

        var anotherRun = Guid.NewGuid();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(options.Reservations.IsReservedByAnotherOwner(anotherRun, UniqueValueScope.MetaverseAttribute, _accountNameId, "joe.bloggs"), Is.True);
            Assert.That(options.Reservations.IsReservedByAnotherOwner(anotherRun, UniqueValueScope.MetaverseAttribute, _emailId, "joe.bloggs@corp.local"), Is.True);
        }
    }

    [Test]
    public async Task ResolveAsync_TwoObjectsInOneCallWhoseFormsWouldClash_GetDifferentFormsAsync()
    {
        // Two account names that differ only in case build the same Email: the second must not be issued it.
        var first = Request(new FakeFormSource(_emailId), baseValue: "joe.bloggs");
        var second = Request(new FakeFormSource(_emailId) { LowerCase = true }, baseValue: "Joe.Bloggs", accountNameId: UniqueValueTestHelpers.NextAttributeId());

        var outcomes = await Server().ResolveAsync([first, second], UniqueValueTestHelpers.Options());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes[0].Value, Is.EqualTo("joe.bloggs"));
            Assert.That(outcomes[1].Value, Is.EqualTo("Joe.Bloggs1"));
        }
    }

    // ---- Waiting, and failing closed ----

    [Test]
    public async Task ResolveAsync_FormWaitsForAnInput_WaitsSayingForWhatAndClaimsNothingAsync()
    {
        var options = UniqueValueTestHelpers.Options();
        var source = new FakeFormSource(_emailId) { Waits = [new GeneratedValueFormWait("UPN Suffix", "User Principal Name")] };

        var outcome = (await Server().ResolveAsync([Request(source)], options))[0];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.Kind, Is.EqualTo(GenerationOutcomeKind.Waiting));
            Assert.That(outcome.FormWaits, Is.EqualTo(source.Waits));
            Assert.That(options.Reservations.IsReservedByAnotherOwner(Guid.NewGuid(), UniqueValueScope.MetaverseAttribute, _accountNameId, "joe.bloggs"), Is.False);
        }
    }

    [Test]
    public async Task ResolveAsync_FormExpressionFails_ThrowsAndReleasesWhatTheCallClaimedAsync()
    {
        var options = UniqueValueTestHelpers.Options();
        var withoutForms = Request(formSource: null);

        // The second object's first candidate is taken in JIM's records, so its second is worked out in a later round,
        // after the first object's value has been claimed.
        var secondAccountNameId = UniqueValueTestHelpers.NextAttributeId();
        SeedMetaverseObjectHolding(secondAccountNameId, "ada.lovelace");
        var failing = Request(new FakeFormSource(_emailId) { FailsFor = "ada.lovelace1" }, baseValue: "ada.lovelace", accountNameId: secondAccountNameId);

        Assert.ThrowsAsync<SyncExpressionEvaluationException>(() => Server().ResolveAsync([withoutForms, failing], options));

        Assert.That(options.Reservations.IsReservedByAnotherOwner(Guid.NewGuid(), UniqueValueScope.MetaverseAttribute, _accountNameId, "joe.bloggs"), Is.False,
            "nothing the failed call claimed stays claimed for the rest of the run");
    }

    // ---- The probe ----

    /// <summary>
    /// The candidate and its Email go to one Connected System, so they are asked about in one call: one search per
    /// system and route, however many values are checked (PRD requirement 7). The call carries the window for each.
    /// </summary>
    [Test]
    public async Task ResolveAsync_FormProbedInTheCandidatesOwnSystem_AsksAboutBothInOneCallAsync()
    {
        var session = new FakeUniquenessProbeSession().WithSystem(CorporateAdId, "Corporate AD", "joe.bloggs@corp.local");
        var options = UniqueValueTestHelpers.Options();
        options.ProbeSession = session;
        var request = Request(new FakeFormSource(_emailId) { ProbeTargets = [new UniquenessProbeTarget(CorporateAdId, MailId)] }, attemptLimit: 3)
            with { ProbeTargets = [new UniquenessProbeTarget(CorporateAdId, SamAccountNameId)] };

        var outcome = (await Server().ResolveAsync([request], options))[0];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.Value, Is.EqualTo("joe.bloggs1"));
            Assert.That(outcome.SkippedCandidates, Is.EqualTo(new[] { "joe.bloggs: Email (built from Account Name) is already held by an account in Corporate AD" }));
            Assert.That(session.SystemCalls, Has.Count.EqualTo(1), "the second candidate is answered from the first call's window");
            Assert.That(session.SystemCalls[0].AttributeIds, Is.EqualTo(new[] { SamAccountNameId, MailId }));
            Assert.That(session.Calls.Single(c => c.Target.ConnectedSystemObjectTypeAttributeId == MailId).Candidates,
                Is.EqualTo(new[] { "joe.bloggs@corp.local", "joe.bloggs1@corp.local", "joe.bloggs2@corp.local" }));
        }
    }

    [Test]
    public async Task ResolveAsync_FormHeldByTheObjectsOwnAccount_IsNotProbedAsync()
    {
        var session = new FakeUniquenessProbeSession().WithSystem(CorporateAdId, "Corporate AD", "joe.bloggs@corp.local");
        var options = UniqueValueTestHelpers.Options();
        options.ProbeSession = session;
        var source = new FakeFormSource(_emailId) { ProbeTargets = [new UniquenessProbeTarget(CorporateAdId, MailId)], ProbeExemptValues = ["joe.bloggs@corp.local"] };

        var outcome = (await Server().ResolveAsync([Request(source)], options))[0];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.Value, Is.EqualTo("joe.bloggs"));
            Assert.That(session.Calls, Is.Empty);
        }
    }

    // ---- fixture helpers ----

    private UniqueValueGenerationServer Server() => new(_repository);

    private GenerationRequest Request(IGeneratedValueFormSource? formSource, Guid? metaverseObjectId = null, string baseValue = "joe.bloggs", int attemptLimit = 1000, int? accountNameId = null) =>
        UniqueValueTestHelpers.ImportRequest(UniqueValueTestHelpers.Generation(attemptLimit: attemptLimit), accountNameId ?? _accountNameId, metaverseObjectId, baseValue: baseValue)
            with { FormSource = formSource };

    private void SeedMetaverseObjectHolding(int attributeId, string value)
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Created = DateTime.UtcNow };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = attributeId, StringValue = value });
        _repository.SeedMetaverseObject(mvo);
    }

    private void SeedAccountHolding(int attributeId, string value, Guid metaverseObjectId)
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), MetaverseObjectId = metaverseObjectId };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = attributeId, StringValue = value, ConnectedSystemObject = cso });
        _repository.SeedConnectedSystemObject(cso);
    }

    /// <summary>
    /// An Email built from the candidate (candidate@corp.local), held in a Metaverse attribute and checked where the
    /// test says, or a scripted wait or failure.
    /// </summary>
    private sealed class FakeFormSource(int emailAttributeId) : IGeneratedValueFormSource
    {
        public IReadOnlyCollection<int> ConnectorSpaceAttributeIds { get; init; } = [];

        public IReadOnlyList<UniquenessProbeTarget> ProbeTargets { get; init; } = [];

        public IReadOnlyCollection<string> ProbeExemptValues { get; init; } = [];

        public IReadOnlyCollection<string> RejectedValues { get; init; } = [];

        public IReadOnlyList<GeneratedValueFormWait> Waits { get; init; } = [];

        public string? FailsFor { get; init; }

        public bool LowerCase { get; init; }

        public GeneratedValueFormEvaluation Evaluate(string candidate, long? numericCandidate)
        {
            if (candidate == FailsFor)
                throw new SyncExpressionEvaluationException("mv[\"Account Name\"].Broken()", "Email", new InvalidOperationException("broken"));

            if (Waits.Count > 0)
                return GeneratedValueFormEvaluation.Waiting(Waits);

            return GeneratedValueFormEvaluation.Checking(
            [
                new GeneratedValueFormCheck
                {
                    Name = "Email",
                    Value = (LowerCase ? candidate.ToLowerInvariant() : candidate) + "@corp.local",
                    Mode = GeneratedValueMode.Import,
                    AttributeId = emailAttributeId,
                    ConnectorSpaceAttributeIds = ConnectorSpaceAttributeIds,
                    ProbeTargets = ProbeTargets,
                    ProbeExemptValues = ProbeExemptValues,
                    RejectedValues = RejectedValues
                }
            ]);
        }
    }
}
