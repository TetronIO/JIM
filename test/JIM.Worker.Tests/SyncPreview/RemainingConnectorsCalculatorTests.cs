// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers.Preview;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncPreview;

/// <summary>
/// Tests for <see cref="RemainingConnectorsCalculator"/> (#288 Phase 1 of the Sync Preview Surface plan):
/// the remaining-connector arithmetic hoisted out of <c>PreviewDeletionEligibilityEvaluator</c> so it and
/// the out-of-scope destructive cascade in <c>SyncPreviewServer</c> compute identical results.
/// </summary>
public class RemainingConnectorsCalculatorTests
{
    [Test]
    public void RemainingConnectorsAfterDisconnection_SingleOccurrenceOfDisconnectingSystem_RemovesItAndKeepsTheRest()
    {
        var result = RemainingConnectorsCalculator.RemainingConnectorsAfterDisconnection(
            [1, 2, 3], disconnectingSystemId: 2);

        Assert.That(result, Is.EqualTo(new List<int> { 1, 3 }));
    }

    [Test]
    public void RemainingConnectorsAfterDisconnection_SystemHoldsTwoJoinedObjectsOneLeaves_TheOtherStaysAConnector()
    {
        // A system holding two joined objects where one leaves is still a connector: only one occurrence
        // of the disconnecting system's id is removed, not every entry.
        var result = RemainingConnectorsCalculator.RemainingConnectorsAfterDisconnection(
            [1, 1, 2], disconnectingSystemId: 1);

        Assert.That(result, Is.EqualTo(new List<int> { 1, 2 }));
    }

    [Test]
    public void RemainingConnectorsAfterDisconnection_DisconnectingSystemNotPresent_ReturnsTheListUnchanged()
    {
        var result = RemainingConnectorsCalculator.RemainingConnectorsAfterDisconnection(
            [1, 2, 3], disconnectingSystemId: 99);

        Assert.That(result, Is.EqualTo(new List<int> { 1, 2, 3 }));
    }

    [Test]
    public void RemainingConnectorsAfterDisconnection_OnlyConnectorIsTheDisconnectingSystem_ReturnsEmpty()
    {
        var result = RemainingConnectorsCalculator.RemainingConnectorsAfterDisconnection(
            [1], disconnectingSystemId: 1);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void RemainingConnectorsAfterDisconnection_EmptyJoinedList_ReturnsEmpty()
    {
        var result = RemainingConnectorsCalculator.RemainingConnectorsAfterDisconnection(
            [], disconnectingSystemId: 1);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void RemainingConnectorsAfterDisconnection_DisconnectingCountGreaterThanOne_RemovesThatManyOccurrences()
    {
        // Mirrors PreviewDeletionEligibilityEvaluator's own multi-occurrence caller: several of the
        // disconnecting system's joined objects leave at once, and only that many entries come out.
        var result = RemainingConnectorsCalculator.RemainingConnectorsAfterDisconnection(
            [1, 1, 1, 2], disconnectingSystemId: 1, disconnectingCount: 2);

        Assert.That(result, Is.EqualTo(new List<int> { 1, 2 }));
    }

    [Test]
    public void RemainingConnectorsAfterDisconnection_PreservesOriginalOrderOfRemainingEntries()
    {
        // Matches List<int>.Remove's own guarantee (the real HandleCsoOutOfScopeAsync code path): removing
        // one element leaves every other element in place, in its original order.
        var result = RemainingConnectorsCalculator.RemainingConnectorsAfterDisconnection(
            [3, 1, 2, 1], disconnectingSystemId: 1);

        Assert.That(result, Is.EqualTo(new List<int> { 3, 2, 1 }));
    }
}
