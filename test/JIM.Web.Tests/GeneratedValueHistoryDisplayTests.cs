// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Which events in a generated attribute's history read as the value being generated or corrected (Unique Value
/// Generation, #242, release 4, Phase 9): the attribute inspector's rail names those two events for what they were rather
/// than as a bare Added or Set.
/// </summary>
[TestFixture]
public class GeneratedValueHistoryDisplayTests
{
    private static AttributeHistoryEntry Entry(AttributeHistoryChangeKind kind, bool generated, MetaverseObjectChangeInitiatorType initiator, string? initiatedBy = null) => new()
    {
        Kind = kind,
        Value = "joe.bloggs1",
        PreviousValue = kind == AttributeHistoryChangeKind.Set ? "joe.bloggs" : null,
        IsGeneratedValue = generated,
        Change = new ProvenanceChange { ChangeInitiatorType = initiator, InitiatedByName = initiatedBy }
    };

    [Test]
    public void EventFor_ARevisionByCollisionRemediation_IsACorrection()
    {
        var entry = Entry(AttributeHistoryChangeKind.Set, true, MetaverseObjectChangeInitiatorType.System, MetaverseServer.CollisionRemediationInitiatorName);

        Assert.That(GeneratedValueHistoryDisplay.EventFor(entry), Is.EqualTo(GeneratedValueHistoryEvent.Corrected));
    }

    [Test]
    public void EventFor_AGeneratedValueFlowingIn_IsGeneration()
    {
        var entry = Entry(AttributeHistoryChangeKind.Added, true, MetaverseObjectChangeInitiatorType.SynchronisationRule);

        Assert.That(GeneratedValueHistoryDisplay.EventFor(entry), Is.EqualTo(GeneratedValueHistoryEvent.Generated));
    }

    [Test]
    public void EventFor_AnOrdinaryValueOrARemoval_IsNeither()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(GeneratedValueHistoryDisplay.EventFor(Entry(AttributeHistoryChangeKind.Set, false, MetaverseObjectChangeInitiatorType.SynchronisationRule)), Is.Null);
            Assert.That(GeneratedValueHistoryDisplay.EventFor(Entry(AttributeHistoryChangeKind.Removed, true, MetaverseObjectChangeInitiatorType.SynchronisationRule)), Is.Null);
            Assert.That(GeneratedValueHistoryDisplay.EventFor(Entry(AttributeHistoryChangeKind.Set, false, MetaverseObjectChangeInitiatorType.System, MetaverseServer.CollisionRemediationInitiatorName)), Is.Null,
                "only a generated value is corrected");
        }
    }
}
