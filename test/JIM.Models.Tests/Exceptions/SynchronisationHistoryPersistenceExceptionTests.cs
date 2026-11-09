// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Models.Exceptions;
using NUnit.Framework;

namespace JIM.Models.Tests.Exceptions;

/// <summary>
/// The message a failed synchronisation history write fails the Activity with (#2874): which page's history, of which
/// Connected System, and what that leaves behind.
/// </summary>
[TestFixture]
public class SynchronisationHistoryPersistenceExceptionTests
{
    [Test]
    public void BuildMessage_Page_NamesThePageTheSystemAndWhatWasSaved()
    {
        var message = SynchronisationHistoryPersistenceException.BuildMessage(4, 12, "HR");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(message, Does.StartWith("Failed to record the execution items and change history of page 4 of 12 for Connected System 'HR'."));
            Assert.That(message, Does.Contain("synchronisation changes were saved; their history was not"));
            Assert.That(message, Does.Contain("the run stopped before saving any further changes"));
        }
    }

    [Test]
    public void BuildMessage_NoPage_NamesTheStepsAfterThePages()
    {
        var message = SynchronisationHistoryPersistenceException.BuildMessage(0, 0, "Directory");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(message, Does.StartWith("Failed to record the execution items and change history of the steps after the pages for Connected System 'Directory'."));
            Assert.That(message, Does.Not.Contain("page 0"));
        }
    }

    [Test]
    public void Constructor_IsASyncPersistenceExceptionCarryingItsContext()
    {
        var inner = new TimeoutException("the write timed out");

        var exception = new SynchronisationHistoryPersistenceException("wrapped", inner, page: 2, totalPages: 9, connectedSystemName: "HR");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception, Is.InstanceOf<SyncPersistenceException>(),
                "the page loop leaves a SyncPersistenceException alone, so the page it names is the one reported");
            Assert.That(exception.InnerException, Is.SameAs(inner));
            Assert.That(exception.Page, Is.EqualTo(2));
            Assert.That(exception.TotalPages, Is.EqualTo(9));
            Assert.That(exception.ConnectedSystemName, Is.EqualTo("HR"));
        }
    }
}
