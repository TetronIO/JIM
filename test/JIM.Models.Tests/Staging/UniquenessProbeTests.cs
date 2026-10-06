// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Models.Tests.Staging;

/// <summary>
/// The live uniqueness probe's contract (Unique Value Generation, #242, release 3): a batch's rules, and how the
/// values a search returned become one outcome per candidate, with and without a control value.
/// </summary>
[TestFixture]
public class UniquenessProbeTests
{
    /// <summary>
    /// The outcome may be persisted or carried across process boundaries by ordinal, so the numbering is pinned.
    /// </summary>
    [Test]
    public void UniquenessProbeOutcome_Ordinals_ArePinned()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That((int)UniquenessProbeOutcome.Found, Is.EqualTo(0));
            Assert.That((int)UniquenessProbeOutcome.NotFound, Is.EqualTo(1));
            Assert.That((int)UniquenessProbeOutcome.CouldNotDetermine, Is.EqualTo(2));
            Assert.That(Enum.GetValues<UniquenessProbeOutcome>(), Has.Length.EqualTo(3));
        }
    }

    // ---- Request validation ----

    [Test]
    public void Validate_WellFormedBatch_DoesNotThrow()
    {
        var request = Request(["jbloggs", "jbloggs2"], controlValue: "asmith");

        Assert.That(request.Validate, Throws.Nothing);
    }

    [Test]
    public void Validate_NoControlValue_DoesNotThrow()
    {
        var request = Request(["jbloggs"], controlValue: null);

        Assert.That(request.Validate, Throws.Nothing);
    }

    [Test]
    public void Validate_NoAttributeName_Throws()
    {
        var request = new UniquenessProbeRequest { AttributeName = " ", Candidates = ["jbloggs"] };

        Assert.That(request.Validate, Throws.ArgumentException);
    }

    [Test]
    public void Validate_NoCandidates_Throws()
    {
        var request = Request([], controlValue: null);

        Assert.That(request.Validate, Throws.ArgumentException);
    }

    [Test]
    public void Validate_MoreThanTheMaximumCandidates_Throws()
    {
        var candidates = Enumerable.Range(1, UniquenessProbeRequest.MaximumCandidates + 1).Select(i => $"jbloggs{i}").ToList();

        Assert.That(Request(candidates, controlValue: null).Validate, Throws.ArgumentException);
    }

    [Test]
    public void Validate_ExactlyTheMaximumCandidates_DoesNotThrow()
    {
        var candidates = Enumerable.Range(1, UniquenessProbeRequest.MaximumCandidates).Select(i => $"jbloggs{i}").ToList();

        Assert.That(Request(candidates, controlValue: null).Validate, Throws.Nothing);
    }

    [Test]
    public void Validate_ControlValueIsACandidateInAnotherCase_Throws()
    {
        var request = Request(["jbloggs", "JSmith"], controlValue: "jsmith");

        Assert.That(request.Validate, Throws.ArgumentException);
    }

    // ---- Result from the values found ----

    [Test]
    public void FromValuesFound_ControlReturned_ReportsFoundAndNotFoundPerCandidate()
    {
        var request = Request(["jbloggs", "jbloggs2", "jbloggs3"], controlValue: "asmith");

        var result = UniquenessProbeResult.FromValuesFound(request, ["ASMITH", "JBloggs2"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcomes, Is.EqualTo(new[] { UniquenessProbeOutcome.NotFound, UniquenessProbeOutcome.Found, UniquenessProbeOutcome.NotFound }));
            Assert.That(result.Reason, Is.Null);
            Assert.That(result.IsFailure, Is.False);
        }
    }

    [Test]
    public void FromValuesFound_ControlNotReturned_ReportsEveryCandidateUndeterminedWithAReason()
    {
        var request = Request(["jbloggs", "jbloggs2"], controlValue: "asmith");

        var result = UniquenessProbeResult.FromValuesFound(request, ["jbloggs2"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcomes, Is.All.EqualTo(UniquenessProbeOutcome.CouldNotDetermine));
            Assert.That(result.Outcomes, Has.Count.EqualTo(2));
            Assert.That(result.Reason, Does.Contain("uid"));
            Assert.That(result.IsFailure, Is.False, "a search that completed without its control is blind to the attribute, not failed");
        }
    }

    /// <summary>
    /// Plan decision 14, revised 2026-10-06: with no control the probe still answers. A candidate the search returned
    /// is in use whatever the bind can see; one it did not return is accepted unconfirmed, without a reason.
    /// </summary>
    [Test]
    public void FromValuesFound_NoControlValue_HitIsFoundAndMissIsNotFound()
    {
        var request = Request(["jbloggs", "jbloggs2"], controlValue: null);

        var result = UniquenessProbeResult.FromValuesFound(request, ["JBLOGGS"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcomes, Is.EqualTo(new[] { UniquenessProbeOutcome.Found, UniquenessProbeOutcome.NotFound }));
            Assert.That(result.Reason, Is.Null);
            Assert.That(result.IsFailure, Is.False);
        }
    }

    [Test]
    public void FromValuesFound_NoControlValueAndNothingReturned_EveryCandidateNotFound()
    {
        var request = Request(["jbloggs", "jbloggs2"], controlValue: null);

        var result = UniquenessProbeResult.FromValuesFound(request, []);

        Assert.That(result.Outcomes, Is.All.EqualTo(UniquenessProbeOutcome.NotFound));
    }

    [Test]
    public void Undetermined_ReportsEveryCandidateUndeterminedAndIsNotAFailure()
    {
        var result = UniquenessProbeResult.Undetermined(3, "Too many entries hold the values");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcomes, Has.Count.EqualTo(3));
            Assert.That(result.Outcomes, Is.All.EqualTo(UniquenessProbeOutcome.CouldNotDetermine));
            Assert.That(result.Reason, Is.EqualTo("Too many entries hold the values"));
            Assert.That(result.IsFailure, Is.False);
        }
    }

    [Test]
    public void Failed_ReportsEveryCandidateUndeterminedAndIsAFailure()
    {
        var result = UniquenessProbeResult.Failed(2, "The directory refused the probe");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcomes, Is.All.EqualTo(UniquenessProbeOutcome.CouldNotDetermine));
            Assert.That(result.Reason, Is.EqualTo("The directory refused the probe"));
            Assert.That(result.IsFailure, Is.True);
        }
    }

    [Test]
    public void Failed_NoReason_Throws()
    {
        Assert.That(() => UniquenessProbeResult.Failed(1, " "), Throws.ArgumentException);
    }

    private static UniquenessProbeRequest Request(IReadOnlyList<string> candidates, string? controlValue) => new()
    {
        AttributeName = "uid",
        Candidates = candidates,
        ControlValue = controlValue
    };
}
