// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Models.Security;
using NUnit.Framework;

namespace JIM.Models.Tests.Security;

/// <summary>
/// Whether an API key can authenticate at a given moment: enabled, and not past its expiry. A Connected System's
/// SCIM keys are limited to two usable at once, counted by this rule.
/// </summary>
[TestFixture]
public class ApiKeyUsabilityTests
{
    private static readonly DateTime Now = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    [TestCase(true, 1, true, TestName = "IsUsableAt_EnabledAndExpiresLater_True")]
    [TestCase(true, null, true, TestName = "IsUsableAt_EnabledAndNeverExpires_True")]
    [TestCase(true, 0, false, TestName = "IsUsableAt_ExpiresThisMoment_False")]
    [TestCase(true, -1, false, TestName = "IsUsableAt_Expired_False")]
    [TestCase(false, 1, false, TestName = "IsUsableAt_Disabled_False")]
    public void IsUsableAt(bool enabled, int? expiresInDays, bool expected)
    {
        var key = new ApiKey { IsEnabled = enabled, ExpiresAt = expiresInDays.HasValue ? Now.AddDays(expiresInDays.Value) : null };

        Assert.That(key.IsUsableAt(Now), Is.EqualTo(expected));
    }
}
