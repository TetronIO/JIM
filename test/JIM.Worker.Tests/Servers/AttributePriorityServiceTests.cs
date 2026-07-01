// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Models.Logic;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Unit tests for the deterministic resolution core of <see cref="AttributePriorityService"/> (#91): given the
/// tri-state contributions to a single Metaverse Object attribute, it selects the winner by priority and honours
/// "Null is a value". These exercise the algorithm in isolation; gathering and persistence are engine concerns.
/// </summary>
[TestFixture]
public class AttributePriorityServiceTests
{
    private AttributePriorityService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _service = new AttributePriorityService();
    }

    private static AttributeContribution Contribution(int mappingId, int priority, ContributionState state, bool nullIsValue = false)
    {
        return new AttributeContribution
        {
            Mapping = new SyncRuleMapping { Id = mappingId, Priority = priority, NullIsValue = nullIsValue },
            State = state
        };
    }

    [Test]
    public void Resolve_NoContributions_ReturnsNoContributor()
    {
        var result = _service.Resolve(Array.Empty<AttributeContribution>());

        Assert.That(result.Outcome, Is.EqualTo(AttributeResolutionOutcome.NoContributor));
        Assert.That(result.WinningContribution, Is.Null);
    }

    [Test]
    public void Resolve_AllNotApplicable_ReturnsNoContributor()
    {
        var contributions = new[]
        {
            Contribution(10, 1, ContributionState.RuleNotApplicable, nullIsValue: true),
            Contribution(20, 2, ContributionState.RuleNotApplicable)
        };

        var result = _service.Resolve(contributions);

        Assert.That(result.Outcome, Is.EqualTo(AttributeResolutionOutcome.NoContributor));
    }

    [Test]
    public void Resolve_HighestPriorityHasValue_ThatContributionWins()
    {
        var contributions = new[]
        {
            Contribution(20, 2, ContributionState.ConnectedWithValue),
            Contribution(10, 1, ContributionState.ConnectedWithValue)
        };

        var result = _service.Resolve(contributions);

        Assert.That(result.Outcome, Is.EqualTo(AttributeResolutionOutcome.Value));
        Assert.That(result.WinningMapping!.Id, Is.EqualTo(10)); // priority 1 beats priority 2
    }

    [Test]
    public void Resolve_TopPriorityNotApplicable_FallsThroughToNextWithValue()
    {
        // HR migration scenario: priority-1 rule has no opinion (not joined), so the lower-priority rule contributes.
        var contributions = new[]
        {
            Contribution(10, 1, ContributionState.RuleNotApplicable, nullIsValue: true),
            Contribution(20, 2, ContributionState.ConnectedWithValue)
        };

        var result = _service.Resolve(contributions);

        Assert.That(result.Outcome, Is.EqualTo(AttributeResolutionOutcome.Value));
        Assert.That(result.WinningMapping!.Id, Is.EqualTo(20));
    }

    [Test]
    public void Resolve_TopPriorityConnectedNoValueWithNullIsValue_AssertsNullNoFallback()
    {
        var contributions = new[]
        {
            Contribution(10, 1, ContributionState.ConnectedNoValue, nullIsValue: true),
            Contribution(20, 2, ContributionState.ConnectedWithValue)
        };

        var result = _service.Resolve(contributions);

        Assert.That(result.Outcome, Is.EqualTo(AttributeResolutionOutcome.AssertedNull));
        Assert.That(result.WinningMapping!.Id, Is.EqualTo(10));
    }

    [Test]
    public void Resolve_TopPriorityConnectedNoValueWithoutNullIsValue_FallsThrough()
    {
        var contributions = new[]
        {
            Contribution(10, 1, ContributionState.ConnectedNoValue, nullIsValue: false),
            Contribution(20, 2, ContributionState.ConnectedWithValue)
        };

        var result = _service.Resolve(contributions);

        Assert.That(result.Outcome, Is.EqualTo(AttributeResolutionOutcome.Value));
        Assert.That(result.WinningMapping!.Id, Is.EqualTo(20));
    }

    [Test]
    public void Resolve_ConnectedNoValueNoNullIsValue_AndNoFurtherContributor_ReturnsNoContributor()
    {
        var contributions = new[]
        {
            Contribution(10, 1, ContributionState.ConnectedNoValue, nullIsValue: false)
        };

        var result = _service.Resolve(contributions);

        Assert.That(result.Outcome, Is.EqualTo(AttributeResolutionOutcome.NoContributor));
    }

    [Test]
    public void Resolve_EqualPriority_TieBreaksDeterministicallyByMappingId()
    {
        // Duplicate priorities should never happen (validation prevents it), but resolution must still be
        // deterministic if they do: lowest mapping id wins.
        var contributions = new[]
        {
            Contribution(30, 1, ContributionState.ConnectedWithValue),
            Contribution(10, 1, ContributionState.ConnectedWithValue),
            Contribution(20, 1, ContributionState.ConnectedWithValue)
        };

        var result = _service.Resolve(contributions);

        Assert.That(result.WinningMapping!.Id, Is.EqualTo(10));
    }

    [Test]
    public void Resolve_NotApplicableIgnoredEvenAtHighPriority_LowerPriorityNullAssertionWins()
    {
        // Priority 1 not applicable (skipped); priority 2 connected-no-value with NullIsValue asserts null;
        // priority 3 has a value but must not be reached.
        var contributions = new[]
        {
            Contribution(10, 1, ContributionState.RuleNotApplicable),
            Contribution(20, 2, ContributionState.ConnectedNoValue, nullIsValue: true),
            Contribution(30, 3, ContributionState.ConnectedWithValue)
        };

        var result = _service.Resolve(contributions);

        Assert.That(result.Outcome, Is.EqualTo(AttributeResolutionOutcome.AssertedNull));
        Assert.That(result.WinningMapping!.Id, Is.EqualTo(20));
    }
}
