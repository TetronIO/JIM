// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using NUnit.Framework;

namespace JIM.Models.Tests.Activities;

/// <summary>
/// Which errors list the objects an ambiguous match found (#2052): the portal, REST API and PowerShell load candidates
/// only for these, so an error missing here would record them and never show them.
/// </summary>
[TestFixture]
public class MatchCandidateRecordingTests
{
    [TestCase(ActivityRunProfileExecutionItemErrorType.AmbiguousMatch)]
    [TestCase(ActivityRunProfileExecutionItemErrorType.AmbiguousProvisioningMatch)]
    public void AreRecordedFor_AmbiguousMatchErrors_ReturnsTrue(ActivityRunProfileExecutionItemErrorType errorType)
    {
        Assert.That(ActivityRunProfileExecutionItemMatchCandidate.AreRecordedFor(errorType), Is.True);
    }

    [TestCase(null)]
    [TestCase(ActivityRunProfileExecutionItemErrorType.NotSet)]
    [TestCase(ActivityRunProfileExecutionItemErrorType.ObjectMatchingRuleFailed)]
    [TestCase(ActivityRunProfileExecutionItemErrorType.CouldNotExportDueToExistingConnectedSystemObject)]
    public void AreRecordedFor_OtherErrors_ReturnsFalse(ActivityRunProfileExecutionItemErrorType? errorType)
    {
        Assert.That(ActivityRunProfileExecutionItemMatchCandidate.AreRecordedFor(errorType), Is.False);
    }
}
