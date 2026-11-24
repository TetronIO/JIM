// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Localisation;
using NUnit.Framework;

namespace JIM.Models.Tests.Activities;

/// <summary>
/// A target name JIM writes itself ("Factory reset", "Rule for User") is stored as its English and as a message, so the
/// portal can show it in the reader's language while everything else reads the English (#3030).
/// </summary>
[TestFixture]
public class ActivityTargetNameDetailTests
{
    [Test]
    public void TargetNameDetail_Set_StoresTheMessageAndItsEnglish()
    {
        var detail = JimMessage.Create("JIM.Models.Tests:TestMessages", "NotFound", "Factory reset");
        var activity = new Activity { TargetNameDetail = detail };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.TargetName, Is.EqualTo(detail.ToEnglish()));
            Assert.That(activity.TargetNameDetailJson, Is.Not.Null);
            Assert.That(activity.TargetNameDetail!.ToEnglish(), Is.EqualTo(detail.ToEnglish()));
        }
    }

    [Test]
    public void TargetNameDetail_NeverSet_IsNullAndLeavesTheName()
    {
        var activity = new Activity { TargetName = "Full Import" };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.TargetNameDetail, Is.Null);
            Assert.That(activity.TargetName, Is.EqualTo("Full Import"));
        }
    }
}
