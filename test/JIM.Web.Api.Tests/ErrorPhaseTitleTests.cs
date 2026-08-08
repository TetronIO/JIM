// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Linq;
using JIM.Models.Activities;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The heading above a Run Profile Execution Item's error tells an administrator which phase went wrong.
/// A missing mapping breaks nothing visibly, it just names the wrong phase, which is why it needs a test
/// rather than a code review to catch: the two error types added for Connector-reported import problems
/// (#637) silently inherited a "Synchronisation Failed" heading, one of them on an object that imported.
/// </summary>
[TestFixture]
public class ErrorPhaseTitleTests
{
    [Test]
    public void ErrorPhaseTitles_CoverEveryErrorType()
    {
        var unmapped = Enum.GetValues<ActivityRunProfileExecutionItemErrorType>()
            .Where(errorType => !JIM.Web.Helpers.ErrorPhaseTitles.ContainsKey(errorType))
            .ToList();

        Assert.That(unmapped, Is.Empty,
            "Every error type needs an explicit heading naming the phase it belongs to. Unmapped: " +
            string.Join(", ", unmapped));
    }

    [Test]
    public void GetErrorPhaseTitle_ForAnImportRejection_SaysTheImportWasRejected()
    {
        Assert.That(JIM.Web.Helpers.GetErrorPhaseTitle(ActivityRunProfileExecutionItemErrorType.ConnectorConfigurationError),
            Is.EqualTo("Import Rejected"));
    }

    [Test]
    public void GetErrorPhaseTitle_ForAnAttributeValueError_DoesNotClaimTheObjectFailed()
    {
        // The object imported; only the failing attribute was omitted. Saying "Synchronisation Failed" or
        // "Import Rejected" here would send an administrator looking for a problem that does not exist.
        var title = JIM.Web.Helpers.GetErrorPhaseTitle(ActivityRunProfileExecutionItemErrorType.ImportAttributeValueError);

        Assert.That(title, Is.EqualTo("Attribute Not Imported"));
    }

    [Test]
    public void GetErrorPhaseTitle_ForNoErrorType_FallsBackWithoutThrowing()
    {
        Assert.That(JIM.Web.Helpers.GetErrorPhaseTitle(null), Is.Not.Empty);
    }
}
