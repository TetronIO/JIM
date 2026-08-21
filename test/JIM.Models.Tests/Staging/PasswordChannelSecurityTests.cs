// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using NUnit.Framework;
using Serilog;

namespace JIM.Models.Tests.Staging;

/// <summary>
/// The one rule that decides whether JIM sends a password down a channel it cannot confirm is encrypted (#1119).
/// <para>
/// Stated once and read by all three paths that write a password to a Connected System: the initial password on
/// an account JIM provisions, a password an administrator sets by hand, and a synchronised password change. Each
/// of them responds differently to a refusal, and each of them must decide on identical grounds; a rule restated
/// per path is a rule that drifts, and the drift here means passwords going out in the clear from the one path
/// somebody forgot.
/// </para>
/// </summary>
[TestFixture]
public class PasswordChannelSecurityTests
{
    private static ConnectedSystem System(bool requireSecureTransport) => new()
    {
        Id = 3,
        Name = "Corporate AD",
        RequireSecureTransport = requireSecureTransport
    };

    [Test]
    public void RefusesChannel_RequiredAndChannelIsNotSecure_RefusesAsync()
    {
        var refuses = PasswordChannelSecurity.RefusesChannel(System(true), new StubConnector(secure: false));

        Assert.That(refuses, Is.True);
    }

    [Test]
    public void RefusesChannel_RequiredAndChannelIsSecure_AllowsAsync()
    {
        var refuses = PasswordChannelSecurity.RefusesChannel(System(true), new StubConnector(secure: true));

        Assert.That(refuses, Is.False);
    }

    [Test]
    public void RefusesChannel_NotRequiredAndChannelIsNotSecure_AllowsAsync()
    {
        // The administrator has not asked JIM to refuse, and some directories genuinely cannot offer an
        // encrypted connection. The Connector warns; the choice stays with the person who knows the deployment.
        var refuses = PasswordChannelSecurity.RefusesChannel(System(false), new StubConnector(secure: false));

        Assert.That(refuses, Is.False);
    }

    [Test]
    public void RefusesChannel_NotRequiredAndChannelIsSecure_AllowsAsync()
    {
        var refuses = PasswordChannelSecurity.RefusesChannel(System(false), new StubConnector(secure: true));

        Assert.That(refuses, Is.False);
    }

    [Test]
    public void RefusalMessage_NamesTheSystemAndWhatToDoAsync()
    {
        var message = PasswordChannelSecurity.RefusalMessage(System(true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(message, Does.Contain("Corporate AD"),
                "An administrator reading this on an Activity needs to know which system refused.");
            Assert.That(message, Does.Contain("Require Secure Transport"),
                "And which setting to look at, by the name it carries in the portal.");
        }
    }

    /// <summary>
    /// A Connector that reports whether its password channel is encrypted, and nothing else: the rule under test
    /// reads exactly one fact from a Connector.
    /// </summary>
    private sealed class StubConnector(bool secure) : IConnectorPasswordManagement
    {
        public IReadOnlyCollection<PasswordExpiryBehaviour> SupportedExpiryBehaviours => [];

        public bool IsPasswordChannelSecure { get; } = secure;

        public void OpenPasswordConnection(IList<ConnectedSystemSettingValue> settings) { }

        public Task<PasswordSetResult> SetPasswordAsync(ConnectedSystemObject target, string password, PasswordSetOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException("This stub answers one question and sets no passwords.");

        public void ClosePasswordConnection() { }

        public Task<PasswordPreflightResult> RunPasswordPreflightAsync(List<ConnectedSystemSettingValue> settings, IReadOnlyList<string> containerExternalIds, ILogger logger, CancellationToken cancellationToken) =>
            throw new NotSupportedException("This stub answers one question and runs no preflight.");
    }
}
