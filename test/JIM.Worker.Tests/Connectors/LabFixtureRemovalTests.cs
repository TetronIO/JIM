// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using NUnit.Framework;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// How the lab write suite removes each fixture it made (#2021, Phase 2): with the cmdlet for whatever the fixture is
/// when the run ends. Exchange's Remove-* cmdlets remove recipients only, so a fixture left a plain directory object
/// (a run that failed before the Connector enabled it) is first made the simplest recipient of its kind. The lab run
/// on 2026-11-05 left six fixtures behind for want of this.
/// </summary>
[TestFixture]
public class LabFixtureRemovalTests
{
    [TestCase("user", "UserMailbox", null, "Remove-Mailbox")]
    [TestCase("user", "SharedMailbox", null, "Remove-Mailbox")]
    [TestCase("user", "MailUser", null, "Remove-MailUser")]
    [TestCase("user", null, "Enable-MailUser", "Remove-MailUser")]
    [TestCase("contact", "MailContact", null, "Remove-MailContact")]
    [TestCase("contact", null, "Enable-MailContact", "Remove-MailContact")]
    [TestCase("group", "MailUniversalDistributionGroup", null, "Remove-DistributionGroup")]
    [TestCase("group", "MailUniversalSecurityGroup", null, "Remove-DistributionGroup")]
    [TestCase("group", null, "Enable-DistributionGroup", "Remove-DistributionGroup")]
    public void For_FixtureInEachState_RemovesItWithTheMatchingCmdlet(string kind, string? recipientTypeDetails, string? enableFirst, string remove)
    {
        var removal = LabFixtureRemoval.For(kind, recipientTypeDetails);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(removal.EnableFirst, Is.EqualTo(enableFirst));
            Assert.That(removal.Remove, Is.EqualTo(remove));
        }
    }
}
