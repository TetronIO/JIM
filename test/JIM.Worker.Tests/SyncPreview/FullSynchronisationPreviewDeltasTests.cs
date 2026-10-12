// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers.Preview;
using JIM.Models.Activities;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncPreview;

/// <summary>
/// How a Full Synchronisation preview turns the errors a run would record into its rows.
/// </summary>
[TestFixture]
public class FullSynchronisationPreviewDeltasTests
{
    [TestCase(SyncPreviewMessageCode.AmbiguousMatch)]
    [TestCase(SyncPreviewMessageCode.AmbiguousProvisioningMatch)]
    public void ForObject_AmbiguousMatchError_IsAWouldMatchAmbiguouslyRow(SyncPreviewMessageCode code)
    {
        // #2052: an identity matching several existing accounts before provisioning is the same decision for the
        // administrator as an object matching several identities, so both read as an ambiguous match.
        var item = new FullSyncPreviewItem
        {
            ConnectedSystemObjectId = Guid.NewGuid(),
            DisplayName = "Jane Smith",
            Preview = new SyncPreviewResult { Errors = [new SyncPreviewMessage { Code = code, Detail = "matches 2" }] }
        };

        var deltas = FullSynchronisationPreviewDeltas.ForObject(item, connectedSystemId: 1, gracePeriodOf: _ => null);

        var failure = deltas.Single(d => d.NewValue == "matches 2");
        Assert.That(failure.TransitionType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.WouldMatchAmbiguously));
    }

    [Test]
    public void ForObject_AmbiguousMatchError_CarriesItsCandidates()
    {
        var first = new ActivityRunProfileExecutionItemMatchCandidate
        {
            CandidateKind = MatchCandidateKind.ConnectedSystemObject, CandidateId = Guid.NewGuid(), DisplayNameSnapshot = "jsmith",
            ConnectedSystemId = 4, ConnectedSystemNameSnapshot = "Directory", MatchedValue = "E1", Ordinal = 0, CandidateTotal = 2
        };
        var second = new ActivityRunProfileExecutionItemMatchCandidate
        {
            CandidateKind = MatchCandidateKind.ConnectedSystemObject, CandidateId = Guid.NewGuid(), DisplayNameSnapshot = "jsmith2",
            ConnectedSystemId = 4, ConnectedSystemNameSnapshot = "Directory", MatchedValue = "E1", Ordinal = 1, CandidateTotal = 2
        };
        var item = new FullSyncPreviewItem
        {
            ConnectedSystemObjectId = Guid.NewGuid(),
            DisplayName = "Jane Smith",
            Preview = new SyncPreviewResult
            {
                Errors = [new SyncPreviewMessage { Code = SyncPreviewMessageCode.AmbiguousProvisioningMatch, Detail = "matches 2", MatchCandidates = [first, second] }]
            }
        };

        var deltas = FullSynchronisationPreviewDeltas.ForObject(item, connectedSystemId: 1, gracePeriodOf: _ => null);

        Assert.That(deltas.Single(d => d.NewValue == "matches 2").MatchCandidates, Is.EqualTo(new[] { first, second }));
    }

    [Test]
    public void ForObject_ErrorThatIsNotAnAmbiguity_CarriesNoCandidates()
    {
        var item = new FullSyncPreviewItem
        {
            ConnectedSystemObjectId = Guid.NewGuid(),
            DisplayName = "Jane Smith",
            Preview = new SyncPreviewResult { Errors = [new SyncPreviewMessage { Code = SyncPreviewMessageCode.ObjectMatchingRuleFailed, Detail = "failed" }] }
        };

        var deltas = FullSynchronisationPreviewDeltas.ForObject(item, connectedSystemId: 1, gracePeriodOf: _ => null);

        Assert.That(deltas.Single(d => d.NewValue == "failed").MatchCandidates, Is.Null.Or.Empty);
    }
}
