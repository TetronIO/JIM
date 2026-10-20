// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Web;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// How an Activity's target type is named in the portal: from its enum name, except where that name spells an
/// acronym as a word.
/// </summary>
[TestFixture]
public class HelpersActivityTargetTypeNameTests
{
    [TestCase(ActivityTargetType.ScimRequests, "SCIM Requests")]
    [TestCase(ActivityTargetType.ConnectedSystemRunProfile, "Connected System Run Profile")]
    [TestCase(ActivityTargetType.Authentication, "Authentication")]
    public void GetActivityTargetTypeName_TargetType_Name(ActivityTargetType targetType, string expected)
    {
        Assert.That(Helpers.GetActivityTargetTypeName(targetType), Is.EqualTo(expected));
    }
}
