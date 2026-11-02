// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Models.Tests.Transactional;

/// <summary>
/// The Exchange queue row (#2021): one Exchange change owed to one directory account. What is pinned here is the
/// declared-state comparison that decides whether re-staging leaves the account's row as it is, whatever its status.
/// </summary>
[TestFixture]
public class PendingExchangeChangeTests
{
    private static PendingExchangeChange Declared(
        string recipientType = "Mailbox",
        string? alias = "ada.lovelace",
        string? database = "DB01",
        string? primarySmtpAddress = "ada.lovelace@example.com",
        string? externalAddress = null) => new()
    {
        ConnectedSystemId = 3,
        ConnectedSystemObjectId = Guid.NewGuid(),
        ActivityId = Guid.NewGuid(),
        RecipientType = recipientType,
        Alias = alias,
        Database = database,
        PrimarySmtpAddress = primarySmtpAddress,
        ExternalAddress = externalAddress
    };

    [Test]
    public void HasSameDeclaredStateAs_AllFiveValuesEqual_ReturnsTrue()
    {
        var row = Declared(externalAddress: "SMTP:ada@partner.example");
        var other = Declared(externalAddress: "SMTP:ada@partner.example");

        Assert.That(row.HasSameDeclaredStateAs(other), Is.True);
    }

    [Test]
    public void HasSameDeclaredStateAs_NullAndEmptyInEachValue_AreTheSame()
    {
        var row = Declared(alias: null, database: "", primarySmtpAddress: null, externalAddress: "");
        var other = Declared(alias: "", database: null, primarySmtpAddress: "", externalAddress: null);

        Assert.That(row.HasSameDeclaredStateAs(other), Is.True);
    }

    [Test]
    public void HasSameDeclaredStateAs_ValuesDifferingOnlyByCase_AreDifferent()
    {
        // Ordinal, not case-insensitive: an alias or address declared with different casing is a different request,
        // and Exchange keeps the casing it is given.
        var row = Declared(alias: "ada.lovelace");
        var other = Declared(alias: "Ada.Lovelace");

        Assert.That(row.HasSameDeclaredStateAs(other), Is.False);
    }

    [TestCase("RecipientType")]
    [TestCase("Alias")]
    [TestCase("Database")]
    [TestCase("PrimarySmtpAddress")]
    [TestCase("ExternalAddress")]
    public void HasSameDeclaredStateAs_OneValueDiffers_ReturnsFalse(string differing)
    {
        var row = Declared(externalAddress: "SMTP:ada@partner.example");
        var other = Declared(externalAddress: "SMTP:ada@partner.example");
        switch (differing)
        {
            case "RecipientType": other.RecipientType = "SharedMailbox"; break;
            case "Alias": other.Alias = "ada"; break;
            case "Database": other.Database = "DB02"; break;
            case "PrimarySmtpAddress": other.PrimarySmtpAddress = "ada@example.com"; break;
            case "ExternalAddress": other.ExternalAddress = null; break;
        }

        Assert.That(row.HasSameDeclaredStateAs(other), Is.False, $"{differing} differs");
    }

    [Test]
    public void HasSameDeclaredStateAs_IgnoresEverythingButTheDeclaredState()
    {
        // The domain controller, the account, the rule and the Activity describe where and why the state was declared,
        // not what was declared, so a re-stage that differs only in those still matches what was cancelled.
        var row = Declared();
        row.DomainController = "dc01.corp.example";
        row.Status = PendingExchangeChangeStatus.Cancelled;
        var other = Declared();
        other.DomainController = "dc02.corp.example";
        other.SyncRuleId = 9;
        other.MetaverseObjectId = Guid.NewGuid();

        Assert.That(row.HasSameDeclaredStateAs(other), Is.True);
    }
}
