// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// What the Metaverse Object page records for its export review banner (#2615). The banner is for administrators, and what
/// it shows is read only for them: for anyone else nothing is recorded and nothing further is read.
/// </summary>
[TestFixture]
public class MetaverseObjectViewStateExportReviewTests
{
    private int _readCount;

    private IReadOnlyList<string> Read()
    {
        _readCount++;
        return ["Mobile", "Job Title"];
    }

    [SetUp]
    public void SetUp() => _readCount = 0;

    [Test]
    public void ApplyExportReview_FlaggedObjectForAnAdministrator_RecordsTheFlagAndWhatTheObjectRecords()
    {
        var state = new MetaverseObjectViewState(Guid.NewGuid());

        state.ApplyExportReview(pending: true, isAdministrator: true, Read);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.ExportReviewPending, Is.True);
            Assert.That(state.ExportReviewChangedAttributeNames, Is.EqualTo(new[] { "Mobile", "Job Title" }));
        }
    }

    [Test]
    public void ApplyExportReview_ReaderWhoIsNotAnAdministrator_RecordsNothingAndReadsNothing()
    {
        var state = new MetaverseObjectViewState(Guid.NewGuid());

        state.ApplyExportReview(pending: true, isAdministrator: false, Read);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.ExportReviewPending, Is.False);
            Assert.That(state.ExportReviewChangedAttributeNames, Is.Empty);
            Assert.That(_readCount, Is.Zero, "what the object records is not read for a reader who cannot see it");
        }
    }

    [Test]
    public void ApplyExportReview_ObjectNotFlagged_RecordsNothingAndReadsNothing()
    {
        var state = new MetaverseObjectViewState(Guid.NewGuid());

        state.ApplyExportReview(pending: false, isAdministrator: true, Read);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.ExportReviewPending, Is.False);
            Assert.That(state.ExportReviewChangedAttributeNames, Is.Empty);
            Assert.That(_readCount, Is.Zero);
        }
    }

    [Test]
    public void ApplyExportReview_NothingSuppliedToRead_RecordsTheFlagAlone()
    {
        var state = new MetaverseObjectViewState(Guid.NewGuid());

        state.ApplyExportReview(pending: true, isAdministrator: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.ExportReviewPending, Is.True);
            Assert.That(state.ExportReviewChangedAttributeNames, Is.Empty);
        }
    }

    [Test]
    public void ApplyExportReview_AppliedAgain_ReplacesWhatWasRecorded()
    {
        var state = new MetaverseObjectViewState(Guid.NewGuid());
        state.ApplyExportReview(pending: true, isAdministrator: true, Read);

        state.ApplyExportReview(pending: false, isAdministrator: true, Read);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.ExportReviewPending, Is.False);
            Assert.That(state.ExportReviewChangedAttributeNames, Is.Empty);
        }
    }
}
