// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using JIM.Models.Exceptions;
using NUnit.Framework;

namespace JIM.Models.Tests.Exceptions;

/// <summary>
/// Verifies the structured message and context that <see cref="SyncPersistenceException"/> attaches to a
/// synchronisation-page persistence failure, so the diagnosis it provides (over the previous anonymous
/// "unhandled exception") is covered without provoking a real database failure through the sync engine.
/// </summary>
[TestFixture]
public class SyncPersistenceExceptionTests
{
    [Test]
    public void BuildMessage_WithAffectedIds_IncludesPageConnectedSystemAndSample()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        var message = SyncPersistenceException.BuildMessage(3, 101, "Scenario 14 Secondary", new List<Guid> { id1, id2 });

        Assert.Multiple(() =>
        {
            Assert.That(message, Does.Contain("page 3 of 101"));
            Assert.That(message, Does.Contain("Scenario 14 Secondary"));
            Assert.That(message, Does.Contain(id1.ToString()));
            Assert.That(message, Does.Contain(id2.ToString()));
        });
    }

    [Test]
    public void BuildMessage_WithNoAffectedIds_OmitsTheSampleClause()
    {
        var message = SyncPersistenceException.BuildMessage(1, 1, "Primary", Array.Empty<Guid>());

        Assert.Multiple(() =>
        {
            Assert.That(message, Does.Contain("page 1 of 1"));
            Assert.That(message, Does.Contain("Primary"));
            Assert.That(message, Does.Not.Contain("Affected Metaverse Object id"));
        });
    }

    [Test]
    public void Constructor_PreservesInnerExceptionAndContext()
    {
        var inner = new InvalidOperationException("underlying database failure");

        var exception = new SyncPersistenceException("wrapped", inner, page: 7, totalPages: 12, connectedSystemName: "HR");

        Assert.Multiple(() =>
        {
            Assert.That(exception.InnerException, Is.SameAs(inner));
            Assert.That(exception.Page, Is.EqualTo(7));
            Assert.That(exception.TotalPages, Is.EqualTo(12));
            Assert.That(exception.ConnectedSystemName, Is.EqualTo("HR"));
        });
    }
}
