// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Models.Exceptions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests;

/// <summary>
/// Which failures <c>Worker.SafeFailActivityAsync</c> records through a fresh DbContext first. A failure raised
/// while a page was being persisted leaves the run's own context holding that page's unsaved entities, so saving
/// the Activity on it re-attempts the same doomed write: the Activity is then only failed at the third attempt,
/// after two misleading Error lines (found by Scenario 23, where JIM's own integrity guard threw mid-flush and
/// the second attempt failed on a foreign key for execution items that were never written).
/// </summary>
[TestFixture]
public class WorkerFailActivityContextTests
{
    [Test]
    public void ShouldFailOnFreshContextFirst_DbUpdateException_ReturnsTrue()
    {
        Assert.That(Worker.ShouldFailOnFreshContextFirst(new DbUpdateException("save failed")), Is.True);
    }

    [Test]
    public void ShouldFailOnFreshContextFirst_SyncPersistenceExceptionWrappingANonDatabaseException_ReturnsTrue()
    {
        // The worker's own wrapper for "this page failed while persisting", whatever the inner cause: here the
        // unresolved-generated-value integrity guard, which is not a database exception at all.
        var exception = new SyncPersistenceException(
            "Failed to persist synchronisation changes on page 1 of 1 for Connected System 'Directory'.",
            new InvalidOperationException("Pending Export attribute change still has an unresolved generated value marker."),
            page: 1, totalPages: 1, connectedSystemName: "Directory");

        Assert.That(Worker.ShouldFailOnFreshContextFirst(exception), Is.True);
    }

    [Test]
    public void ShouldFailOnFreshContextFirst_ExceptionOutsidePersistence_ReturnsFalse()
    {
        // A failure outside persistence (a connector error, say) leaves the context usable, and failing the Activity
        // on it keeps whatever the run had already recorded alongside the failure.
        Assert.That(Worker.ShouldFailOnFreshContextFirst(new InvalidOperationException("connector failed")), Is.False);
    }
}
