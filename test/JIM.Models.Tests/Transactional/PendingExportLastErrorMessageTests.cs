// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Models.Activities;
using JIM.Models.Localisation;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Models.Tests.Transactional;

/// <summary>
/// A Pending Export's last error and a synchronisation outcome's detail are stored as a message beside their English where
/// JIM worded them (#3094). The message and the English can never disagree: setting the message sets the English, and
/// every way the English is replaced with plain text or cleared clears the message.
/// </summary>
[TestFixture]
public class PendingExportLastErrorMessageTests
{
    private static readonly JimMessage Message = JimMessage.Create("JIM.Models.Tests:TestMessages", "NotFound", "HR");

    [Test]
    public void LastErrorMessageDetail_Set_SetsTheEnglishBesideIt()
    {
        var export = new PendingExport { LastErrorMessageDetail = Message };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(export.LastErrorMessage, Is.EqualTo(Message.ToEnglish()));
            Assert.That(export.LastErrorMessageDetail?.Key, Is.EqualTo("NotFound"));
            Assert.That(export.LastErrorMessageDetailJson, Is.Not.Null);
        }
    }

    [Test]
    public void ClearFailure_ErrorStoredAsAMessage_ClearsTheMessageWithTheEnglish()
    {
        var export = new PendingExport { ErrorCount = 2, LastErrorMessageDetail = Message };

        export.ClearFailure();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(export.LastErrorMessage, Is.Null);
            Assert.That(export.LastErrorMessageDetailJson, Is.Null);
        }
    }

    [Test]
    public void AfterFailedAttempt_WithAMessage_KeepsTheMessageAndItsEnglish()
    {
        var export = new PendingExport();

        PendingExportRetryState.Of(export).AfterFailedAttempt(Message, "at Somewhere()", DateTime.UtcNow).ApplyTo(export);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(export.ErrorCount, Is.EqualTo(1));
            Assert.That(export.LastErrorMessage, Is.EqualTo(Message.ToEnglish()));
            Assert.That(export.LastErrorMessageDetail?.Key, Is.EqualTo("NotFound"));
            Assert.That(export.LastErrorStackTrace, Is.EqualTo("at Somewhere()"));
        }
    }

    [Test]
    public void AfterFailedAttempt_WithPlainTextAfterAMessage_ClearsTheEarlierMessage()
    {
        var export = new PendingExport { LastErrorMessageDetail = Message };

        PendingExportRetryState.Of(export).AfterFailedAttempt("The directory went away.", null, DateTime.UtcNow).ApplyTo(export);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(export.LastErrorMessage, Is.EqualTo("The directory went away."));
            Assert.That(export.LastErrorMessageDetailJson, Is.Null);
        }
    }

    [Test]
    public void RetryState_Of_CarriesTheMessageSoPuttingABatchBackRestoresIt()
    {
        var export = new PendingExport { LastErrorMessageDetail = Message };
        var before = PendingExportRetryState.Of(export);
        export.ClearFailure();

        before.ApplyTo(export);

        Assert.That(export.LastErrorMessageDetail?.Key, Is.EqualTo("NotFound"));
    }

    [Test]
    public void SyncOutcomeDetailMessageDetail_Set_SetsTheEnglishBesideIt()
    {
        var outcome = new ActivityRunProfileExecutionItemSyncOutcome { DetailMessageDetail = Message };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.DetailMessage, Is.EqualTo(Message.ToEnglish()));
            Assert.That(outcome.DetailMessageDetail?.Key, Is.EqualTo("NotFound"));
        }
    }

    [Test]
    public void SyncOutcomeDetailMessageDetail_SetToNull_ClearsBoth()
    {
        var outcome = new ActivityRunProfileExecutionItemSyncOutcome { DetailMessageDetail = Message };

        outcome.DetailMessageDetail = null;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome.DetailMessage, Is.Null);
            Assert.That(outcome.DetailMessageDetailJson, Is.Null);
        }
    }
}
