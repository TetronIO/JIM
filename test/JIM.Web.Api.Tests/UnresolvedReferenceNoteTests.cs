// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// When a Run Profile Execution Item's page mentions the references its import left unresolved (#959). The item's
/// error already says so when the unresolved reference error holds its slot; otherwise the count would go unseen,
/// which is the loss the count exists to prevent.
/// </summary>
[TestFixture]
public class UnresolvedReferenceNoteTests
{
    [TestCase(null)]
    [TestCase(0)]
    public void GetUnresolvedReferenceNote_NothingUnresolved_ShowsNoNote(int? count)
    {
        Assert.That(JIM.Web.Helpers.GetUnresolvedReferenceNote(ActivityRunProfileExecutionItemErrorType.NotSet, count), Is.Null);
    }

    [Test]
    public void GetUnresolvedReferenceNote_UnresolvedReferenceErrorHoldsTheSlot_ShowsNoNote()
    {
        // The error's own message carries the count and the breakdown; a note would say it twice.
        Assert.That(JIM.Web.Helpers.GetUnresolvedReferenceNote(ActivityRunProfileExecutionItemErrorType.UnresolvedReference, 3), Is.Null);
    }

    [Test]
    public void GetUnresolvedReferenceNote_AnotherErrorHoldsTheSlot_SaysTheReferencesAreAdditional()
    {
        var note = JIM.Web.Helpers.GetUnresolvedReferenceNote(ActivityRunProfileExecutionItemErrorType.ImportAttributeValueError, 2);

        Assert.That(note, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(note, Does.StartWith("Also"));
            Assert.That(note, Does.Contain("2 references"));
        }
    }

    [TestCase(null)]
    [TestCase(ActivityRunProfileExecutionItemErrorType.NotSet)]
    public void GetUnresolvedReferenceNote_NoError_StatesTheCount(ActivityRunProfileExecutionItemErrorType? errorType)
    {
        // Warn and Ignore leave the item un-errored; the count is still worth showing to someone who opens it.
        var note = JIM.Web.Helpers.GetUnresolvedReferenceNote(errorType, 1_284);

        Assert.That(note, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(note, Does.Not.StartWith("Also"));
            Assert.That(note, Does.Contain("1,284 references"));
        }
    }

    [Test]
    public void GetUnresolvedReferenceNote_OneReference_UsesTheSingular()
    {
        var note = JIM.Web.Helpers.GetUnresolvedReferenceNote(ActivityRunProfileExecutionItemErrorType.NotSet, 1);

        Assert.That(note, Does.Contain("1 reference ").And.Not.Contain("1 references"));
    }
}
