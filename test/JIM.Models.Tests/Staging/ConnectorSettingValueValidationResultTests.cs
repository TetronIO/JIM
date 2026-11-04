// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Models.Tests.Staging;

/// <summary>
/// A settings finding is an error, which refuses the save, or a warning, which saves and is shown (#2021, D5). Every
/// surface that shows warnings reads them through <see cref="ConnectorSettingValueValidationResult.IsWarning"/>.
/// </summary>
[TestFixture]
public class ConnectorSettingValueValidationResultTests
{
    [Test]
    public void Warning_IsValidAndAWarning()
    {
        var warning = ConnectorSettingValueValidationResult.Warning(null, "The account cannot run Enable-Mailbox.");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(warning.IsValid, Is.True, "a warning never refuses a save");
            Assert.That(warning.IsWarning, Is.True);
            Assert.That(warning.ErrorMessage, Is.EqualTo("The account cannot run Enable-Mailbox."));
        }
    }

    [Test]
    public void IsWarning_AnError_IsFalse()
    {
        Assert.That(new ConnectorSettingValueValidationResult { IsValid = false, ErrorMessage = "Refused." }.IsWarning, Is.False);
    }

    [Test]
    public void IsWarning_AValidResultWithNothingToSay_IsFalse()
    {
        // The LDAP Connector returns one of these when the directory answers.
        Assert.That(new ConnectorSettingValueValidationResult { IsValid = true }.IsWarning, Is.False);
    }

    [Test]
    public void IsWarning_WarningSeverityWithoutAMessage_IsFalse()
    {
        var silent = new ConnectorSettingValueValidationResult { IsValid = true, Severity = ConnectorSettingValidationSeverity.Warning, ErrorMessage = " " };

        Assert.That(silent.IsWarning, Is.False, "a warning that says nothing is not shown");
    }

    [Test]
    public void IsWarning_WarningSeverityButInvalid_IsFalse()
    {
        var contradictory = new ConnectorSettingValueValidationResult { IsValid = false, Severity = ConnectorSettingValidationSeverity.Warning, ErrorMessage = "Refused." };

        Assert.That(contradictory.IsWarning, Is.False, "anything that refuses the save is shown as an error, whatever its severity says");
    }
}
