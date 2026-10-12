// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Transactional.DTOs;
using NUnit.Framework;

namespace JIM.Models.Tests.Transactional;

/// <summary>
/// The roll-up of one delivery pass over many Connected Systems: what the pass's summary line and the Worker's lane
/// line report.
/// </summary>
[TestFixture]
public class PasswordDeliveryPassResultTests
{
    /// <summary>
    /// A lane whose only outcome was waiting for an export to an account (#2173) did something worth reporting: left
    /// out, the pass's and the lane's lines say nothing happened, under a per-system line that says otherwise.
    /// </summary>
    [Test]
    public void Add_ASystemWhoseChangesAllWaitedForAnExport_IsReportedAndCountedAsVisited()
    {
        var systemResult = new PasswordDeliveryRunResult { PostponedCount = 2 };
        var pass = new PasswordDeliveryPassResult();

        pass.Add("Panoply AD", systemResult);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(systemResult.HasSomethingToReport, Is.True);
            Assert.That(pass.ConnectedSystemsVisited, Is.EqualTo(1));
            Assert.That(pass.PostponedCount, Is.EqualTo(2));
            Assert.That(pass.HasSomethingToReport, Is.True);
            Assert.That(pass.Describe(), Does.Contain("2"));
        }
    }
}
