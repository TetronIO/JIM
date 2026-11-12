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
            Assert.That(message, Does.Contain("the run stopped without saving the changes of any page after the next one, or of the steps after the pages"),
                "a page's history is written while the next page saves its changes (#2924), so the next page may have saved them");
        }
    }

    /// <summary>
    /// The pages a run expects is an estimate that grows as pages end at the value budget (#2934), so a page numbered as the
    /// last can be followed by another (a closing sweep page, say) that saved its changes. The message must not rest on it.
    /// </summary>
    [Test]
    public void BuildMessage_PageNumberedAsTheLast_StillSaysTheNextPageMayHaveSavedItsChanges()
    {
        var message = SynchronisationHistoryPersistenceException.BuildMessage(12, 12, "HR");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(message, Does.Contain("the run stopped without saving the changes of any page after the next one, or of the steps after the pages"));
            Assert.That(message, Does.Not.Contain("page 13"), "no page after the last is named");
        }
    }

    [Test]
    public void BuildMessage_NoPage_SavedNothingFurther()
    {
        var message = SynchronisationHistoryPersistenceException.BuildMessage(0, 0, "Directory");

        Assert.That(message, Does.Contain("the run stopped before saving any further changes"));
    }

    [Test]
    public void BuildNextPageMessage_Written_SaysTheNextPagesHistoryWasWritten()
    {
        var message = SynchronisationHistoryPersistenceException.BuildNextPageMessage(5, 12, SynchronisationHistoryNextPageOutcome.Written, null);

        Assert.That(message, Is.EqualTo("Page 5 of 12 had saved its changes when the failure was found; its history was written."));
    }

    [Test]
    public void BuildNextPageMessage_WriteFailed_NamesThePageAndWhyItFailed()
    {
        var message = SynchronisationHistoryPersistenceException.BuildNextPageMessage(5, 12, SynchronisationHistoryNextPageOutcome.WriteFailed,
            "the disk is full");

        Assert.That(message, Is.EqualTo(
            "Page 5 of 12 had saved its changes when the failure was found; its history could not be written either: the disk is full"));
    }

    [Test]
    public void BuildNextPageMessage_DependsOnTheFailedPage_SaysWhyItWasNotWritten()
    {
        var message = SynchronisationHistoryPersistenceException.BuildNextPageMessage(5, 12,
            SynchronisationHistoryNextPageOutcome.NotWrittenDependsOnFailedPage, "a causal edge names an execution item of the failed page");

        Assert.That(message, Is.EqualTo(
            "Page 5 of 12 had saved its changes when the failure was found; its history was not written, because it refers to the " +
            "history that failed (a causal edge names an execution item of the failed page)."));
    }

    [Test]
    public void Constructor_WithTheNextPage_KeepsThePageThatFailedFirstAsThePrimaryCause()
    {
        var first = new TimeoutException("page 4's write timed out");
        var second = new InvalidOperationException("page 5's write failed too");

        var exception = new SynchronisationHistoryPersistenceException("both", first, page: 4, totalPages: 12, connectedSystemName: "HR",
            nextPage: 5, nextPageOutcome: SynchronisationHistoryNextPageOutcome.WriteFailed, nextPageFailure: second);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.Page, Is.EqualTo(4), "the page whose history failed first is the one the run fails on");
            Assert.That(exception.InnerException, Is.SameAs(first));
            Assert.That(exception.NextPage, Is.EqualTo(5));
            Assert.That(exception.NextPageOutcome, Is.EqualTo(SynchronisationHistoryNextPageOutcome.WriteFailed));
            Assert.That(exception.NextPageFailure, Is.SameAs(second));
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
