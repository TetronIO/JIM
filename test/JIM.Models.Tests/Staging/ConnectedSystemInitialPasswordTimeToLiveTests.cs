// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Models.Tests.Staging;

/// <summary>
/// How long an account provisioned into a Connected System stays owed an initial password before JIM records an
/// expiry. The value is the Connected System's, because an outage window is a property of the system rather than
/// of the deployment; a system that says nothing gets the same seven days it has always had.
/// </summary>
[TestFixture]
public class ConnectedSystemInitialPasswordTimeToLiveTests
{
    [Test]
    public void EffectiveInitialPasswordTimeToLive_NotConfigured_IsTheDefault()
    {
        var connectedSystem = new ConnectedSystem { Name = "Corporate AD" };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(connectedSystem.InitialPasswordTimeToLive, Is.Null);
            Assert.That(connectedSystem.EffectiveInitialPasswordTimeToLive,
                Is.EqualTo(PendingInitialPassword.DefaultTimeToLive));
        }
    }

    [Test]
    public void EffectiveInitialPasswordTimeToLive_Configured_IsTheConfiguredValue()
    {
        var connectedSystem = new ConnectedSystem
        {
            Name = "Corporate AD",
            InitialPasswordTimeToLive = TimeSpan.FromDays(21)
        };

        Assert.That(connectedSystem.EffectiveInitialPasswordTimeToLive, Is.EqualTo(TimeSpan.FromDays(21)));
    }

    /// <summary>
    /// A zero or negative value would expire every account the moment it was provisioned, which is the one
    /// outcome an administrator setting this can never be asking for. It is treated as unconfigured rather than
    /// obeyed, matching how the retention periods in <c>ServiceSettingsServer</c> guard themselves.
    /// </summary>
    [TestCase(0)]
    [TestCase(-1)]
    public void EffectiveInitialPasswordTimeToLive_ZeroOrNegative_FallsBackToTheDefault(int days)
    {
        var connectedSystem = new ConnectedSystem
        {
            Name = "Corporate AD",
            InitialPasswordTimeToLive = TimeSpan.FromDays(days)
        };

        Assert.That(connectedSystem.EffectiveInitialPasswordTimeToLive,
            Is.EqualTo(PendingInitialPassword.DefaultTimeToLive));
    }

    [Test]
    public void DefaultTimeToLive_IsSevenDays()
    {
        // Pinned because the Connected System setting defaults to it, so changing one without the other would
        // silently change what every existing deployment does.
        Assert.That(PendingInitialPassword.DefaultTimeToLive, Is.EqualTo(TimeSpan.FromDays(7)));
    }
}
