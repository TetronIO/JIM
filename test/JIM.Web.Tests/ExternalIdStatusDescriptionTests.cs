// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// External ID status descriptions render through <c>TooltipText</c>, which lays a description out
/// one sentence per line. Where a description has two things to say, saying them as two sentences is
/// what produces the break; joining them with "and" renders one long line instead. That is a
/// property of the wording, not of the component, so it is pinned here.
/// </summary>
[TestFixture]
public class ExternalIdStatusDescriptionTests
{
    [Test]
    public void GetExternalIdStatusDescription_PendingRemoval_SeparatesWhatHappenedFromWhatHappensNext()
    {
        var description = Helpers.GetExternalIdStatusDescription(ExternalIdStatus.PendingRemoval);

        // Detection and the pending removal are two separate facts, and were joined by "and" while
        // the Rejected and Deleted descriptions beside them were already sentence pairs. That made
        // this the only one of the three to render as a single unbroken line.
        Assert.That(description, Does.Contain("source system. "));
    }
}
