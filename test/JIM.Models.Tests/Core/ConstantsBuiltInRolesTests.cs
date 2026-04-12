// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using NUnit.Framework;

namespace JIM.Models.Tests.Core;

[TestFixture]
public class ConstantsBuiltInRolesTests
{
    [Test]
    public void RoleClaimType_IsIdpAgnostic_UsesShortNameAsync()
    {
        Assert.That(Constants.BuiltInRoles.RoleClaimType, Is.EqualTo("role"));
    }

    [Test]
    public void RoleClaimType_DoesNotContainLegacyMicrosoftUriAsync()
    {
        Assert.That(Constants.BuiltInRoles.RoleClaimType, Does.Not.Contain("schemas.microsoft.com"));
    }
}
