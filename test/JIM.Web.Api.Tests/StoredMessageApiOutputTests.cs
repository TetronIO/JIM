// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Text.Json;
using JIM.Application.Localisation;
using JIM.Models.Activities;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using JIM.Web.Models.Api;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// A synchronisation outcome's detail and a Pending Export's last error are stored as a message beside their English
/// (#3094) so the portal can show them in the viewer's language. The REST API is unchanged: it returns the English as
/// before, whatever the caller's culture, and the stored message is not serialised.
/// </summary>
[TestFixture]
public class StoredMessageApiOutputTests
{
    private const string RemediatedEnglish =
        "Corporate AD already holds the mail \"ada@example.com\" for another object, so JIM corrected it to \"ada2@example.com\".";

    [Test]
    [SetUICulture("fr")]
    public void SyncOutcomeNodeDto_OutcomeWithAStoredMessage_ReturnsTheEnglishAndNotTheMessage()
    {
        var outcome = new ActivityRunProfileExecutionItemSyncOutcome
        {
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.GeneratedValueRemediated,
            DetailMessageDetail = ActivityMessage.CollisionRemediated("Corporate AD", "mail", "ada@example.com", "ada2@example.com")
        };

        var dto = SyncOutcomeNodeDto.FromModel(SyncOutcomeNode.FromSyncOutcome(outcome));
        var json = JsonSerializer.Serialize(dto);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.DetailMessage, Is.EqualTo(RemediatedEnglish));
            Assert.That(json, Does.Not.Contain("DetailMessageDetail"));
        }
    }

    [Test]
    [SetUICulture("fr")]
    public void PendingExportDtos_LastErrorStoredAsAMessage_ReturnTheEnglishAndNotTheMessage()
    {
        var pendingExport = new PendingExport { Id = Guid.NewGuid(), ErrorCount = 1, LastErrorMessageDetail = ActivityMessage.ExportFailedNoReason() };

        var detail = PendingExportDetailDto.FromDetailResult(new PendingExportDetailResult { PendingExport = pendingExport });
        var header = PendingExportHeader.FromEntity(pendingExport);
        var json = JsonSerializer.Serialize(detail) + JsonSerializer.Serialize(header);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(detail.LastErrorMessage, Is.EqualTo("Export failed"));
            Assert.That(header.LastErrorMessage, Is.EqualTo("Export failed"));
            Assert.That(json, Does.Not.Contain("LastErrorMessageDetail"));
        }
    }
}
